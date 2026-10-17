using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Contracts;
using Horus.Server.Analysis;
using Horus.Server.Analysis.Search;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Horus.Server.Ingest;
using Horus.Server.Jobs;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Api;

/// 看板唯讀 API(/api/exams, /seats, /suspicious, /events, /images) + 管理/複核寫 API
/// (建考試、結束考試、可疑裁決)。系統給線索,處分由人裁決。
public static class Endpoints
{
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static void MapApi(this WebApplication app)
    {
        Db db = app.Services.GetRequiredService<Db>();
        Storage storage = app.Services.GetRequiredService<Storage>();
        ServerConfig cfg = app.Services.GetRequiredService<ServerConfig>();
        AgentHub hub = app.Services.GetRequiredService<AgentHub>();
        ArchiveService archive = app.Services.GetRequiredService<ArchiveService>();
        AdminSessionStore adminSessions = app.Services.GetRequiredService<AdminSessionStore>();   // M4·RBAC 登出銷燬
        SessionStore sessions = app.Services.GetRequiredService<SessionStore>();                  // 採集工作階段(全場遠程登出)
        ImageSearchStore searchStore = app.Services.GetRequiredService<ImageSearchStore>();
        ImageEmbedService embedService = app.Services.GetRequiredService<ImageEmbedService>();

        // ---- 登入:檢核權杖 → 下發 HttpOnly cookie(免受 admin gate;否則拿不到 cookie 就進不來) ----
        // cookie 值 = 管理權杖;JS 讀不到(HttpOnly),SameSite=Strict 防 CSRF。gate 用 FixedTimeEquals 比對。
        app.MapPost("/api/login", async (HttpContext ctx) =>
        {
            if (!cfg.AdminAuthEnabled) return Results.Json(new { ok = true, authRequired = false });
            // M4·RBAC(R3):oidc 模式下靜態權杖登入退役,監考員走 wentian OIDC(/admin/login),無權杖後門。
            if (cfg.DashboardOidcEnabled)
                return Results.Json(new { ok = false, error = "use_oidc_login", loginUrl = "/admin/login" }, statusCode: 400);
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Results.Json(new { ok = false, error = "bad_json" }, statusCode: 400); }
            string token = (string?)body?["token"] ?? "";
            if (!Crypto.FixedTimeEquals(token, cfg.AdminToken!))
                return Results.Json(new { ok = false, error = "invalid_token" }, statusCode: 401);
            ctx.Response.Cookies.Append("horus_admin", token, new CookieOptions
            {
                HttpOnly = true,                 // JS 讀不到 → 防 XSS 竊取
                SameSite = SameSiteMode.Strict,  // 僅同源請求攜帶 → 防 CSRF
                Path = "/",
                IsEssential = true,
                // 不設 Secure:LAN 走 HTTP,置 Secure 會導致 cookie 不被髮送。HttpOnly+SameSite 仍防竊取與跨站。
            });
            return Results.Json(new { ok = true, authRequired = true });
        });

        // ---- 鑑權模式(公開·gate 豁免):前端據此決定登入門顯示權杖輸入還是 wentian OIDC 登入按鈕 ----
        app.MapGet("/api/authmode", () => Results.Json(new
        {
            mode = cfg.DashboardOidcEnabled ? "oidc" : "token",
            authRequired = cfg.AdminAuthEnabled,
            loginUrl = "/admin/login",
            // 採集面鑑權模式(psk/oidc/both):看板據此在 both 灰度期高亮仍走 PSK 的座位。
            collectAuthMode = cfg.AuthMode,
            // 按圖搜圖是否可用(嵌入器已配)——看板據此顯示「按圖搜圖」按鈕。
            imageSearchEnabled = embedService.Enabled,
        }));

        // ---- 當前登入是誰(admin 門內)----
        //
        // ★★ **這不是體驗優化,是貝塔通 R9 唯一穩定生效的緩解。**
        //   機房電腦**無還原卡**、瀏覽器混裝國產核心因而組策略不現實、Windows 共享 PC 模式
        //   也不現實 —— 上一個人不點退出就走時,最長 15 分鐘(關了瀏覽器)或 30 分鐘
        //   (頁面還開著)內,下一個人可以直接用他的工作階段。**身分層關不掉這個視窗。**
        //   讓下一個人**立刻看見這不是自己的號**,是能真正生效的那一條。
        //   ★ 對考場尤其直接:學生輪流坐同一臺機器正是常態。
        //
        // ★ 只回姓名與 sub —— 身分出口本來就只有三項(貝塔通 P81),這裡不多給一個欄位。
        app.MapGet("/api/me", (HttpContext ctx) =>
        {
            if (!cfg.DashboardOidcEnabled) return Results.Json(new { oidc = false });
            AdminSession? s = adminSessions.Get(ctx.Request.Cookies["horus_admin"] ?? "", Now());
            // 走得到這裡說明 gate 已放行;取不到只可能是 token 模式或極窄的競態。
            return s is null
                ? Results.Json(new { oidc = true })
                : Results.Json(new { oidc = true, sub = s.Sub, name = s.Name, expiresAt = s.ExpiresAt });
        });

        // ---- 存活心跳:三道門的**唯一寫入口**(貝塔通 P88–P92·rp-contract「三道門與存活心跳」) ----
        //
        // 訊息 `{ active: boolean }`,回 **204 無回應體**。**不查使用者表、不查權限、不做重活。**
        //
        // ★★ **它不是業務請求,而反過來也寫死:任何業務請求都不得續 idle。**
        //   這一條對監考看板尤其致命 —— 看板是**持續自動輪詢**的,把輪詢算作活動的話
        //   idle 那道門**永遠不會到點**,監考機上開著看板就等於永不登出。
        //   落法:admin gate(`Program.cs`)只**讀**工作階段不寫時間戳,續期只發生在這一個端點裡。
        //   ★ 這正是貝塔通 P25 被翻案(P92)的直接原因。
        //
        // ★ 它**走 admin gate**(不在豁免名單裡):要續誰的工作階段總得先知道是誰。
        //   於是工作階段已被三道門中任一道判掉時,這裡回傳的是 gate 的 401 —— 前端據此彈登入門,
        //   而不是讓一個死工作階段被自己的心跳救活。
        //
        // ★ `token` 模式(靜態 adminToken·無管理工作階段)下是 no-op:照回 204,免得前端刷一屏 404。
        app.MapPost("/api/heartbeat", async (HttpContext ctx) =>
        {
            bool active;
            try
            {
                JsonNode? body = await JsonNode.ParseAsync(ctx.Request.Body);
                active = (bool?)body?["active"] ?? false;
            }
            catch (JsonException) { return Results.Json(new { ok = false, error = "bad_json" }, statusCode: 400); }

            if (cfg.DashboardOidcEnabled)
                adminSessions.Heartbeat(ctx.Request.Cookies["horus_admin"] ?? "", active, Now());
            return Results.NoContent();
        });

        // ---- 按圖搜圖(M3·admin 門內):以一張證據圖為查詢,在本場已嵌入圖裡暴力餘弦撈相似影格 ----
        app.MapPost("/api/exams/{examId}/search-image", async (string examId, HttpContext ctx) =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Results.Json(new { ok = false, error = "bad_json" }, statusCode: 400); }
            string queryImageId = (string?)body?["imageId"] ?? "";
            int topN = Math.Clamp((int?)body?["topN"] ?? 20, 1, 100);   // B2:上界鉗制,防超大 topN 拖垮
            if (queryImageId.Length == 0) return Results.Json(new { ok = false, error = "missing_image_id" }, statusCode: 400);

            // 查詢圖向量:已嵌入直接取;否則即時補嵌(嵌入器關則失敗)。
            float[]? q = searchStore.Get(queryImageId) ?? await embedService.EmbedImageAsync(queryImageId, ctx.RequestAborted);
            if (q is null)
                return Results.Json(new { ok = false, error = embedService.Enabled ? "query_not_embeddable" : "search_disabled" }, statusCode: 400);

            List<(string id, float[] vec)> corpus = searchStore.GetExam(examId);
            List<(string id, double score)> top = ImageSearchStore.TopN(q, corpus, topN, excludeId: queryImageId, cosineFloor: cfg.EmbedCosineFloor);
            return Results.Json(new
            {
                ok = true,
                queryImageId,
                corpusSize = corpus.Count,
                results = top.Select(t => new { imageId = t.id, score = Math.Round(t.score, 4) }),
            });
        });

        // ---- 考前連通性 / 設定預檢(admin 門內):監考員開考前一鍵自查,防 fail-closed 當天翻車。----
        // 檢查採集/管理端鑑權設定完整性、wentian issuer 可達(JWKS 拉取)、視覺 key、active 考試白名單覆蓋(§10.1)。
        app.MapGet("/api/preflight", async (HttpContext ctx) =>
        {
            var checks = new List<object>();
            int fails = 0, warns = 0;
            void Add(string id, string level, string label, string detail)
            {
                if (level == "fail") fails++; else if (level == "warn") warns++;
                checks.Add(new { id, level, label, detail });
            }

            // 1) 採集面鑑權
            if (cfg.OidcEnabled)
                Add("collect_auth", string.IsNullOrEmpty(cfg.OidcIssuer) ? "fail" : "ok", "采集面 OIDC",
                    string.IsNullOrEmpty(cfg.OidcIssuer) ? "authMode=oidc/both 但未配 oidcIssuer" : $"authMode={cfg.AuthMode} · issuer={cfg.OidcIssuer}");
            else if (cfg.AuthEnabled) Add("collect_auth", "ok", "采集面 PSK", "authMode=psk(共享 PSK)");
            else Add("collect_auth", "warn", "采集面鉴权", "未配 PSK / OIDC(仅联调)");

            // 2) 管理端鑑權
            if (cfg.DashboardOidcEnabled)
            {
                var missing = new List<string>();
                if (string.IsNullOrEmpty(cfg.OidcDashboardClientId)) missing.Add("oidcDashboardClientId");
                if (string.IsNullOrEmpty(cfg.OidcDashboardRedirectUri)) missing.Add("oidcDashboardRedirectUri");
                try { if (string.IsNullOrEmpty(OidcSecret.ResolveDashboard(cfg))) missing.Add("dashboard secret"); }
                catch (Exception ex) { missing.Add("dashboard secret(" + ex.Message + ")"); }
                if (missing.Count > 0) Add("admin_auth", "fail", "监考员 OIDC", "adminAuthMode=oidc 但缺:" + string.Join(" / ", missing));
                else Add("admin_auth", "ok", "监考员 OIDC", "dashboard client 已配 · redirect=" + cfg.OidcDashboardRedirectUri);
                if (!string.IsNullOrEmpty(cfg.OidcDashboardRedirectUri)
                    && !cfg.OidcDashboardRedirectUri!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    Add("admin_redirect_https", "warn", "回调 HTTPS", "oidcDashboardRedirectUri 非 https,远端工作站将无法回调(须自签 https)");
                // 登出回跳(貝塔通 P85):★ 這一項**只能核到本機這一半** —— 「貝塔通後台登記了沒有」
                //   本機查不到。沒登記的表現是上游忽略該參數、使用者停在貝塔通自己的「已退出」頁;
                //   ★ 那不算故障(本機工作階段在跳走之前就清掉了),但監考員會以為沒退成功,所以照實提示。
                Add("post_logout", string.IsNullOrEmpty(cfg.PostLogoutRedirectUriEffective) ? "warn" : "ok",
                    "登出回跳",
                    string.IsNullOrEmpty(cfg.PostLogoutRedirectUriEffective)
                        ? "推导不出回跳地址(oidcDashboardRedirectUri 也为空);登出仍会跳贝塔通,只是回不来"
                        : "本地地址=" + cfg.PostLogoutRedirectUriEffective + " · 须在贝塔通后台「登出回跳地址」一栏登记同一条");
            }
            else Add("admin_auth", "ok", "管理端令牌", "adminAuthMode=token(静态令牌·非 OIDC)");

            // 3) wentian issuer 可達性(fail-closed 關鍵:oidc 下不可達 = 當天登入/換 token 失敗)
            if (cfg.OidcEnabled || cfg.DashboardOidcEnabled)
            {
                // ★ 貝塔通的 JWKS 掛在 `/jwks`(根命名空間),不是 `/.well-known/jwks.json`。
                string jwksUrl = cfg.OidcJwksEndpoint!;
                bool inlineJwks = !string.IsNullOrWhiteSpace(cfg.OidcJwksJson);
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using HttpResponseMessage resp = await http.GetAsync(jwksUrl, ctx.RequestAborted);
                    string body = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
                    if (resp.IsSuccessStatusCode && body.Contains("\"keys\""))
                        Add("issuer_reachable", "ok", "贝塔通可达", "JWKS 拉取成功(" + jwksUrl + ")");
                    else
                        Add("issuer_reachable", "warn", "贝塔通可达性", $"JWKS 端点响应异常({(int)resp.StatusCode})");
                }
                catch (Exception ex)
                {
                    Add("issuer_reachable", inlineJwks ? "warn" : "fail", "贝塔通可达性",
                        "无法拉取 JWKS:" + ex.Message + (inlineJwks
                            ? "(已配内联 JWKS 可离线验签,但授权/换 token 仍需 issuer 可达)"
                            : "(且未配内联 oidcJwksJson,验签也无兜底)"));
                }
            }

            // 3.4) 探活權杖的 `aud` 口徑(owner 2026-08-11 拍板給探活一個專屬 aud)。
            //
            // ★★ **這一項存在的全部理由是「別讓過渡期變成永久」**:相容分支不會自己消失,
            //   而只要它還開著,「`aud` 攔不住撤權/探活這一對」的例外就還在 —— 那正是本次要消滅的。
            //   把它擺在監考員每次開考都會看的那一屏上,是唯一會被真的看見的位置。
            if (cfg.OidcEnabled || cfg.DashboardOidcEnabled)
            {
                var verifier = ctx.RequestServices.GetService(typeof(BetapassRevokeVerifier)) as BetapassRevokeVerifier;
                string auds = verifier is null ? "(未构造)" : string.Join(" / ", verifier.ProbeAudiences);
                string legacy = cfg.OidcHealthAudienceAcceptLegacy ? " · ⚠ 逃生口开着(连裸 client_id 也收)" : "";

                // ★★★ **這一項報的是「探活實際發生了什麼」,不是「設定成什麼」。**
                //   只報設定的話它永遠綠 —— 哪怕對側一次都沒探到過、或者每一發都被拒。
                //   本儲存庫自己反覆寫著「設定化最容易做成裝飾品,斷言必須落在**行為**上」,
                //   而它此前恰恰就是那個裝飾品。
                (double? okAt, double? rejAt, string? why) =
                    ctx.RequestServices.GetRequiredService<BetapassProbeState>().Snapshot();
                string Ago(double t) => $"{(Now() - t) / 60.0:F0} 分钟前";

                // ★★ **口徑由部署聲明,而結論仍由行為決定** —— 兩者都要,少哪個都不行:
                //   只看設定 → 永遠綠(它此前就是這個裝飾品);
                //   只看行為 → P101 下「從未探過」恆為真,變成一條**永遠亮著的 warn**,
                //   而永遠亮著的告警等於沒有告警,下一次真出事時沒人會看。
                bool expectPush = cfg.BetapassRevokePushExpected;

                if (rejAt is not null && (okAt is null || rejAt > okAt))
                    // 口徑對不上。最常見是線上貝塔通還是 P100 之前的版本,還在發裸 client_id。
                    Add("health_audience", "fail", "贝塔通探活",
                        $"最近一次探活**被拒**({Ago(rejAt.Value)}):{why}。本机在等 {auds}{legacy}"
                        + " —— 对侧后台只看得到一个 401,那边不会有人替你发现");
                else if (okAt is not null)
                    // ★ 探活到達了。若本機聲明「不接廣播」(P101),那說明**有人登記了回呼** ——
                    //   現實與聲明不符,照實報出來,別因為設定說不接就不看。
                    Add("health_audience", expectPush ? "ok" : "warn", "贝塔通探活",
                        expectPush
                            ? $"最近一次探活通过({Ago(okAt.Value)}) · 本机认 {auds}{legacy}"
                            : $"探活**居然到达了**({Ago(okAt.Value)}),而本机按 P101 声明不接撤权广播 —— "
                              + $"说明有人给本机登记了回调。请确认口径,或把 betapassRevokePushExpected 置 true{legacy}");
                else if (expectPush)
                    // ★ 從未探過:貝塔通每 5 分鐘就該來一次。伺服器剛起來那幾分鐘屬正常,
                    //   一直沒有則多半是**後台沒登記撤權回呼** —— 探活位址按它同源推導,
                    //   所以那一條登記同時決定探活與撤權兩件事。
                    Add("health_audience", "warn", "贝塔通探活",
                        $"**从未收到过探活**(它每 5 分钟应来一次)。服务器刚启动属正常;"
                        + $"否则多半是贝塔通后台**没登记本机的撤权回调**(探活地址按它同源推导)。本机认 {auds}{legacy}");
                else
                    // ★★ P101:**有意**不接。這是預期狀態,不是故障 —— 把界限與觸發條件一併說清,
                    //   免得下一個人看到「沒有探活」就以為連線路徑壞了、跑去開隧道。
                    Add("health_audience", "ok", "撤权广播",
                        "本机**有意不接**贝塔通的撤权广播与探活(其 P101:局域网机没有可拨地址,"
                        + $"出站≠可入站)。残余有界:撤权后最长 ≤{cfg.AdminSessionMinutes / 60} 小时(本机 absolute)、"
                        + "且再也登不进来、且本机只在考试期在线。★ 需要分钟级撤权时**上拉取模型,不是开隧道**");
            }

            // 3.5) 開考預檢:**監考員自己這次登入**還剩多久,夠不夠撐完一場考試(貝塔通 P91)。
            //
            // ★★ 原設計是靠 IdP 下發一個 claim 告訴 RP「你的工作階段還剩多久」——
            //   **取消 SSO(其 P84)之後那條作廢**,改為 Horus 拿自己的 absolute 自己算。
            // ★ 這一條在看板上比在採集端上更有用:採集端每場都是新登入、剩餘恆為滿,
            //   而監考員很可能幾小時前就登入了 —— absolute **任何活動都推不動它**,
            //   於是「考到一半看板自己掉了」是會真實發生的。
            if (cfg.DashboardOidcEnabled)
            {
                AdminSession? me = adminSessions.Get(ctx.Request.Cookies["horus_admin"] ?? "", Now());
                if (me is null)
                    Add("session_lifetime", "warn", "本次登录时长", "取不到当前管理会话(令牌模式或刚过期?)");
                else
                {
                    double leftMin = (me.ExpiresAt - Now()) / 60.0;
                    if (leftMin >= cfg.ExpectedExamMinutes)
                        Add("session_lifetime", "ok", "本次登录时长",
                            $"还剩 {leftMin:F0} 分钟 ≥ 预计考试 {cfg.ExpectedExamMinutes} 分钟");
                    else
                        Add("session_lifetime", "warn", "本次登录时长",
                            $"只剩 {leftMin:F0} 分钟,不足预计考试 {cfg.ExpectedExamMinutes} 分钟 —— "
                            + "absolute 那道门任何活动都推不动,建议开考前先退出重新登录一次");
                }
            }

            // 4) 視覺分析 key
            if (cfg.VisionEnabled)
            {
                try
                {
                    string k = SecretProtect.Resolve(cfg);
                    Add("vision", "ok", "视觉分析", $"provider={cfg.VisionProvider}" + (k.Length > 0 ? " · key 已解出" : " · key 为空(mock/本地?)"));
                }
                catch (Exception ex) { Add("vision", "warn", "视觉分析", "key 解密失败:" + ex.Message); }
            }

            // 5) active 考試白名單覆蓋(§10.1 缺口)
            List<(string id, string name)> actives = db.Read(conn =>
            {
                var l = new List<(string, string)>();
                using SqliteCommand c = conn.Cmd("SELECT exam_id,name FROM exams WHERE status='active' ORDER BY created_at DESC");
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) l.Add((r.GetString(0), r.GetString(1)));
                return l;
            });
            var activeExams = new List<object>();
            int noWl = 0;
            foreach ((string id, string name) e in actives)
            {
                bool hasWl = hub.GetPolicy(e.id).Hosts is not null;
                if (!hasWl) noWl++;
                activeExams.Add(new { examId = e.id, name = e.name, hasWhitelist = hasWl });
            }
            if (actives.Count == 0) Add("whitelist", "ok", "白名单覆盖", "当前无 active 考试");
            else if (noWl > 0) Add("whitelist", "warn", "白名单覆盖",
                $"{noWl}/{actives.Count} 个 active 考试未下发白名单 → 非黑名单站/进程 server_risk 退回靠 Agent 自报(§10.1 缺口)");
            else Add("whitelist", "ok", "白名单覆盖", $"全部 {actives.Count} 个 active 考试已下发白名单");

            // 6) both→oidc 灰度遷移狀態(go/no-go:切 oidc 前須全部線上座位已走 OIDC,否則這些座位會被拒)。
            int onlineTotal = 0, onlineOidc = 0;
            if (cfg.OidcEnabled)
            {
                double now2 = Now(), cut2 = now2 - cfg.OnlineWindowSeconds;
                (onlineTotal, onlineOidc) = db.Read(conn =>
                {
                    long tot = Scalar(conn,
                        "SELECT COUNT(DISTINCT seat_id) FROM agent_heartbeats WHERE ts>=@cut AND exam_id IN (SELECT exam_id FROM exams WHERE status='active')",
                        ("@cut", cut2));
                    long oidc = Scalar(conn,
                        @"SELECT COUNT(DISTINCT h.seat_id) FROM agent_heartbeats h WHERE h.ts>=@cut
                            AND h.exam_id IN (SELECT exam_id FROM exams WHERE status='active')
                            AND EXISTS (SELECT 1 FROM oidc_sessions s WHERE s.exam_id=h.exam_id AND s.seat_id=h.seat_id AND s.expires_at>@now)",
                        ("@cut", cut2), ("@now", now2));
                    return ((int)tot, (int)oidc);
                });
            }
            if (cfg.AuthMode == "psk")
                Add("migration", "ok", "迁移状态", "采集面 = psk(未启用 OIDC · A1 栽赃/A2 seq 抢占残留 · 见 §10.1)");
            else if (cfg.AuthMode == "oidc")
                Add("migration", "ok", "迁移状态", "采集面 = oidc(PSK 已拒 · A1/A2 已闭合)");
            else   // both
            {
                int pskSeats = onlineTotal - onlineOidc;
                if (onlineTotal == 0) Add("migration", "ok", "迁移状态", "both 灰度期 · 当前无在线座位");
                else if (pskSeats > 0) Add("migration", "warn", "迁移状态",
                    $"both 灰度期:{onlineOidc}/{onlineTotal} 在线座位已走 OIDC,仍有 {pskSeats} 座位走 PSK → 未全部迁移,勿切 oidc(切了这些座位会被拒)");
                else Add("migration", "ok", "迁移状态", $"both 灰度期:全部 {onlineTotal} 在线座位已走 OIDC → 可择机切 oidc 强制");
            }

            // 7) CLIP 模型(按圖搜圖相依):onnx provider 需本機模型檔案;遠程 provider 無本機檔案。
            if (!cfg.EmbedEnabled)
                Add("clip_model", "ok", "CLIP 模型", "按图搜图未启用(未配 embedProvider)→ 功能关闭");
            else if (string.Equals(cfg.EmbedProvider, "onnx", StringComparison.OrdinalIgnoreCase))
            {
                string mp = string.IsNullOrWhiteSpace(cfg.EmbedOnnxModelPath)
                    ? System.IO.Path.Combine(cfg.DataDir, "model.onnx")
                    : (System.IO.Path.IsPathRooted(cfg.EmbedOnnxModelPath) ? cfg.EmbedOnnxModelPath : System.IO.Path.Combine(cfg.DataDir, cfg.EmbedOnnxModelPath));
                if (System.IO.File.Exists(mp))
                    Add("clip_model", "ok", "CLIP 模型", $"onnx 模型存在:{mp}");
                else
                    Add("clip_model", "fail", "CLIP 模型", $"embedProvider=onnx 但模型文件缺失:{mp}");
            }
            else
                Add("clip_model", "ok", "CLIP 模型", $"provider={cfg.EmbedProvider}(远程端点,无本地模型文件)");

            return Results.Json(new { ok = fails == 0, fails, warns, checks, activeExams, migration = new { onlineTotal, onlineOidc } });
        });

        // ---- 登出:清 cookie(oidc 模式一併銷燬伺服器端管理工作階段)----
        app.MapPost("/api/logout", (HttpContext ctx) =>
        {
            if (cfg.DashboardOidcEnabled)
                adminSessions.Delete(ctx.Request.Cookies["horus_admin"] ?? "");
            ctx.Response.Cookies.Delete("horus_admin", new CookieOptions { Path = "/" });
            return Results.Json(new { ok = true });
        });

        // ---- 考試列表 ----
        app.MapGet("/api/exams", () =>
        {
            double cut = Now() - cfg.OnlineWindowSeconds;
            return db.Read(conn =>
            {
                var list = new List<object>();
                using SqliteCommand c = conn.Cmd(
                    "SELECT exam_id,name,status,started_at,ended_at FROM exams ORDER BY created_at DESC");
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    string examId = r.GetString(0);
                    list.Add(new
                    {
                        examId,
                        name = r.GetString(1),
                        status = r.GetString(2),
                        startedAt = NullDouble(r, 3),
                        endedAt = NullDouble(r, 4),
                        seatCount = Scalar(conn, "SELECT COUNT(*) FROM seats WHERE exam_id=@e", ("@e", examId)),
                        onlineCount = Scalar(conn,
                            "SELECT COUNT(DISTINCT seat_id) FROM agent_heartbeats WHERE exam_id=@e AND ts>=@cut",
                            ("@e", examId), ("@cut", cut)),
                        pendingSuspicious = Scalar(conn,
                            "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='pending'", ("@e", examId)),
                        // §10.1:未下發白名單的 active 考試,伺服器對非黑名單站/處理程序無獨立判據(server_risk 退回靠 Agent 自報)。
                        hasWhitelist = hub.GetPolicy(examId).Hosts is not null,
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 座位熱力 ----
        app.MapGet("/api/exams/{examId}/seats", (string examId) =>
        {
            double cut = Now() - cfg.OnlineWindowSeconds;
            double recentCut = Now() - cfg.RecentRiskWindowSeconds;
            return db.Read(conn =>
            {
                var list = new List<object>();
                // M4·S6/S7:LEFT JOIN 最近一條 OIDC 工作階段,看板直呈 wentian 身分畫像(使用者名/暱稱/道號/境界/戰力/頭像)。
                // 效能#2:各相關表**先按 seat_id 各自分組聚合**(hb/ev/hlt/sq 四個 CTE),再與 seats 單次 LEFT JOIN,
                // 避免"座位 × 多個相關子查詢"在 SQLite 內形成的交叉乘積(座位越多放大越明顯)。
                // SQLite 特例:oidc_sessions 用 GROUP BY + MAX(issued_at) 時其餘裸列取"最大 issued_at 那一行"的值。
                using SqliteCommand c = conn.Cmd(
                    @"WITH hb AS (
                        SELECT seat_id, MAX(ts) AS last_hb FROM agent_heartbeats WHERE exam_id=@e GROUP BY seat_id
                      ),
                      ev AS (
                        SELECT seat_id,
                               MAX(ts) AS last_ev,
                               MAX(risk) AS max_agent_risk,
                               MAX(COALESCE(server_risk,0)) AS max_server_risk,
                               COUNT(*) AS ev_count
                        FROM events WHERE exam_id=@e AND ts>=@rc GROUP BY seat_id
                      ),
                      hlt AS (
                        SELECT seat_id, COUNT(*) AS health_count
                        FROM events WHERE exam_id=@e
                          AND type IN ('watchdog_restart','suspected_suspend','screenshot_obscured','capability_degraded')
                        GROUP BY seat_id
                      ),
                      sq AS (
                        SELECT seat_id, COUNT(*) AS susp_count
                        FROM suspicious_queue WHERE exam_id=@e AND status='pending' AND source='suspicion'
                        GROUP BY seat_id
                      ),
                      sess AS (
                        SELECT exam_id, seat_id, sub, name, username,
                               MAX(issued_at) AS mx
                        FROM oidc_sessions WHERE exam_id=@e GROUP BY exam_id, seat_id
                      )
                    SELECT s.seat_id, s.student_id, s.display_name, s.agent_id, s.machine_id,
                           hb.last_hb, ev.last_ev,
                           COALESCE(MAX(ev.max_agent_risk, ev.max_server_risk),0) AS max_risk,
                           COALESCE(ev.ev_count,0) AS ev_count,
                           COALESCE(sq.susp_count,0),
                           sess.sub, sess.name, sess.username,
                           COALESCE(hlt.health_count,0)
                    FROM seats s
                    LEFT JOIN hb  ON hb.seat_id=s.seat_id
                    LEFT JOIN ev  ON ev.seat_id=s.seat_id
                    LEFT JOIN sq  ON sq.seat_id=s.seat_id
                    LEFT JOIN hlt ON hlt.seat_id=s.seat_id
                    LEFT JOIN sess ON sess.exam_id=s.exam_id AND sess.seat_id=s.seat_id
                    WHERE s.exam_id=@e ORDER BY s.seat_id",
                    ("@e", examId), ("@rc", recentCut));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    double? lastHb = NullDouble(r, 5);
                    double? lastEv = NullDouble(r, 6);
                    bool online = (lastHb is not null && lastHb >= cut) || (lastEv is not null && lastEv >= cut);
                    string? sub = NullStr(r, 10);
                    list.Add(new
                    {
                        seatId = r.GetString(0),
                        studentId = NullStr(r, 1),
                        displayName = NullStr(r, 2),
                        agentId = NullStr(r, 3),
                        machineId = NullStr(r, 4),
                        online,
                        // M4→both→oidc 灰度可見性:有 OIDC 工作階段=已遷移;線上但無工作階段=仍走 PSK(未遷移);離線=offline。
                        // 近似判據(有工作階段行即視為 OIDC;2h 考試內工作階段不過期,足夠指示遷移覆蓋)。
                        authMode = sub is not null ? "oidc" : (online ? "psk" : "offline"),
                        lastHeartbeatTs = lastHb,
                        lastEventTs = lastEv,
                        maxRecentRisk = r.GetInt32(7),
                        eventCount = r.GetInt32(8),
                        suspiciousCount = r.GetInt32(9),
                        // OIDC 身分畫像(未登入/PSK 模式為 null)
                        // ★ 身分只剩三項(貝塔通 P81):sub / 真實姓名 / 使用者名。
                        //   道號 / 頭像 / 境界 / 戰力是問天錄的業務欄位,身分中心不散布;
                        //   `userType` 更是隨 P83 整個消失 —— 看板存取資格已由 `horus-admin` 平台開關回答。
                        identity = sub is null ? null : new
                        {
                            sub,
                            name = NullStr(r, 11),
                            username = NullStr(r, 12),
                        },
                        // M5 採集端硬化:該座位的健康告警數(異常重新啟動/疑似掛起/遮屏/能力降級 事件計),看板據此標黃。
                        healthAlerts = r.GetInt32(13),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 可疑佇列（預設只看可裁決的 source='suspicion'；採集健康告警走獨立 /health） ----
        app.MapGet("/api/exams/{examId}/suspicious", (string examId, string? status, string? source) =>
        {
            string filter = string.IsNullOrEmpty(status) ? "pending" : status;
            string src = string.IsNullOrEmpty(source) ? "suspicion" : source;
            return db.Read(conn =>
            {
                var list = new List<object>();
                string sql = @"SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note
                               FROM suspicious_queue WHERE exam_id=@e AND source=@src";
                if (filter != "all") sql += " AND status=@st";
                sql += " ORDER BY score DESC, ts DESC LIMIT 500";

                var ps = new List<(string, object?)> { ("@e", examId), ("@src", src) };
                if (filter != "all") ps.Add(("@st", filter));
                using SqliteCommand c = conn.Cmd(sql, ps.ToArray());
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new
                    {
                        id = r.GetInt64(0),
                        seatId = r.GetString(1),
                        ts = r.GetDouble(2),
                        kind = r.GetString(3),
                        score = r.GetInt32(4),
                        status = r.GetString(5),
                        refs = ParseNode(NullStr(r, 6)),
                        reviewer = NullStr(r, 7),
                        decidedAt = NullDouble(r, 8),
                        note = NullStr(r, 9),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 採集健康（source='health' 的採集端健康告警：螢幕遮擋/能力降級/看門狗重新啟動；僅提示、不可裁決） ----
        app.MapGet("/api/exams/{examId}/health", (string examId) =>
        {
            return db.Read(conn =>
            {
                var list = new List<object>();
                using SqliteCommand c = conn.Cmd(
                    @"SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note
                      FROM suspicious_queue WHERE exam_id=@e AND source='health'
                      ORDER BY ts DESC LIMIT 500",
                    ("@e", examId));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new
                    {
                        id = r.GetInt64(0),
                        seatId = r.GetString(1),
                        ts = r.GetDouble(2),
                        kind = r.GetString(3),
                        score = r.GetInt32(4),
                        status = r.GetString(5),
                        refs = ParseNode(NullStr(r, 6)),
                        reviewer = NullStr(r, 7),
                        decidedAt = NullDouble(r, 8),
                        note = NullStr(r, 9),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 事件時間線 ----
        app.MapGet("/api/exams/{examId}/events", (string examId, string? seatId, int? limit) =>
        {
            int lim = Math.Clamp(limit ?? 200, 1, 2000);
            return db.Read(conn =>
            {
                var list = new List<object>();
                string sql = "SELECT id,seat_id,seq,ts,recv_ts,type,payload,risk,evidence_image_id,server_risk FROM events WHERE exam_id=@e";
                if (!string.IsNullOrEmpty(seatId)) sql += " AND seat_id=@s";
                sql += " ORDER BY id DESC LIMIT @lim";

                using SqliteCommand c = !string.IsNullOrEmpty(seatId)
                    ? conn.Cmd(sql, ("@e", examId), ("@s", seatId!), ("@lim", lim))
                    : conn.Cmd(sql, ("@e", examId), ("@lim", lim));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new
                    {
                        id = r.GetInt64(0),
                        seatId = r.GetString(1),
                        seq = r.GetInt64(2),
                        ts = r.GetDouble(3),
                        recvTs = r.GetDouble(4),
                        type = r.GetString(5),
                        payload = ParseNode(NullStr(r, 6)),
                        risk = r.GetInt32(7),
                        evidenceImageId = NullStr(r, 8),
                        serverRisk = NullInt(r, 9),
                    });
                }
                return Results.Json(list);
            });
        });

        // ---- 雜湊鏈完整性稽核(M3):離線複驗錨點自洽 + 鏈連續。只對未歸檔(live)考試有意義。 ----
        // 管理鑑權已由全域 /api gate 覆蓋。回傳 { ok, totalEvents, totalHashOk, totalChainOk, totalUnverifiable, agents:[…] }。
        // 已歸檔考試 live 事件已清空(整鏈因清理而斷,§13.2)→ 不回傳偽裝"全綠"的空報告,而是明確 applicable:false。
        app.MapGet("/api/exams/{examId}/integrity", (string examId) =>
            db.Read(conn =>
            {
                string? status = Scalar<string?>(conn, "SELECT status FROM exams WHERE exam_id=@e", ("@e", examId));
                if (status is null) return Results.NotFound(new { error = "no_such_exam" });
                // archiving(歸檔進行中,可能半清理)與 archived(已清理)都不做 live 連續性稽核,避免對殘缺 live 資料誤報鏈斷/偽裝全綠。
                if (status is "archived" or "archiving")
                    return Results.Json(new
                    {
                        examId, status, applicable = false,
                        note = "归档中/已归档:关键事件的 hash_self/sig 锚点在 archive 库,整链已因清理而断,不再做 live 连续性审计(见 §13.2)。归档复核走 GET /api/archive/exams/{examId}",
                    });
                return Results.Json(IntegrityAudit.Run(conn, examId, cfg.Psk));
            }));

        // ---- 證據圖位元組 ----
        app.MapGet("/api/images/{imageId}", async (string imageId, HttpContext ctx) =>
        {
            string? rel = db.Read(conn => Scalar<string?>(conn,
                "SELECT file_path FROM images WHERE image_id=@id", ("@id", imageId)));
            // 歸檔回落(F3):考試到齡歸檔後 live 行已清、原圖移冷存,證據圖仍應可通過端點調取取證。
            rel ??= ArchiveImagePath(archive.ArchivePath, imageId);
            if (rel is null) { ctx.Response.StatusCode = 404; return; }
            string? full = storage.Resolve(rel);
            if (full is null || !File.Exists(full)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "image/webp";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";   // 權杖在 ?t= 查詢裡,別經 Referer 外洩
            ctx.Response.Headers["Cache-Control"] = "no-store";        // 別把帶權杖的 URL 寫入磁碟快取
            await ctx.Response.SendFileAsync(full);
        });

        // ---- 證據圖中繼資料 ----
        app.MapGet("/api/images/{imageId}/meta", (string imageId) =>
            db.Read(conn =>
            {
                using SqliteCommand c = conn.Cmd(
                    @"SELECT image_id,seat_id,ts,trigger,phash,width,height,bytes,is_evidence,uploaded_to_ocr,analysis_state
                      FROM images WHERE image_id=@id", ("@id", imageId));
                using SqliteDataReader r = c.ExecuteReader();
                if (!r.Read()) return ArchiveImageMeta(archive.ArchivePath, imageId);   // 歸檔回落(F3)
                return Results.Json(new
                {
                    imageId = r.GetString(0),
                    seatId = r.GetString(1),
                    ts = r.GetDouble(2),
                    trigger = r.GetString(3),
                    phash = r.GetString(4),
                    width = NullInt(r, 5),
                    height = NullInt(r, 6),
                    bytes = NullLong(r, 7),
                    isEvidence = r.GetInt32(8) != 0,
                    uploadedToOcr = r.GetInt32(9) != 0,   // 隱私稽核:位元組是否真出網送雲(mock/本機恆 false)
                    analyzed = r.GetInt32(10) != 0,       // 視覺分析是否已終結(成功/確定態失敗)
                    archived = false,
                });
            }));

        // ---- 歸檔考試唯讀複核(F3):到齡歸檔後 live 已清,關鍵資料在 archive 庫。此端點讓歸檔考試仍可經 HTTP 取證複核。 ----
        // 回傳 { examId, summary, adjudications:[…], events:[…關鍵事件…], images:[…證據圖…] }。archive 庫不存在/無此考試 → 404。
        app.MapGet("/api/archive/exams/{examId}", (string examId) =>
        {
            if (!File.Exists(archive.ArchivePath)) return Results.NotFound(new { error = "no_archive" });
            try
            {
                using SqliteConnection conn = OpenArchiveRead(archive.ArchivePath);
                object? exam = null;
                using (SqliteCommand c = conn.Cmd("SELECT name,started_at,ended_at,archived_at,summary FROM archive_exams WHERE exam_id=@e", ("@e", examId)))
                using (SqliteDataReader r = c.ExecuteReader())
                    if (r.Read()) exam = new { name = NullStr(r, 0), startedAt = NullDouble(r, 1), endedAt = NullDouble(r, 2),
                                               archivedAt = r.GetDouble(3), summary = ParseNode(NullStr(r, 4)) };
                if (exam is null) return Results.NotFound(new { error = "no_such_archived_exam" });

                var adj = new List<object>();
                using (SqliteCommand c = conn.Cmd("SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note FROM archive_adjudications WHERE exam_id=@e ORDER BY ts", ("@e", examId)))
                using (SqliteDataReader r = c.ExecuteReader())
                    while (r.Read()) adj.Add(new { id = r.GetInt64(0), seatId = r.GetString(1), ts = r.GetDouble(2), kind = r.GetString(3),
                        score = NullInt(r, 4), status = r.GetString(5), refs = ParseNode(NullStr(r, 6)), reviewer = NullStr(r, 7),
                        decidedAt = NullDouble(r, 8), note = NullStr(r, 9) });

                var evs = new List<object>();
                using (SqliteCommand c = conn.Cmd("SELECT id,seat_id,seq,ts,type,payload,risk,server_risk,evidence_image_id FROM archive_events WHERE exam_id=@e ORDER BY ts LIMIT 2000", ("@e", examId)))
                using (SqliteDataReader r = c.ExecuteReader())
                    while (r.Read()) evs.Add(new { id = r.GetInt64(0), seatId = r.GetString(1), seq = NullLong(r, 2), ts = r.GetDouble(3),
                        type = r.GetString(4), payload = ParseNode(NullStr(r, 5)), risk = NullInt(r, 6), serverRisk = NullInt(r, 7),
                        evidenceImageId = NullStr(r, 8) });

                var imgs = new List<object>();
                using (SqliteCommand c = conn.Cmd("SELECT image_id,seat_id,ts,trigger,phash,bytes FROM archive_images WHERE exam_id=@e ORDER BY ts LIMIT 2000", ("@e", examId)))
                using (SqliteDataReader r = c.ExecuteReader())
                    while (r.Read()) imgs.Add(new { imageId = r.GetString(0), seatId = r.GetString(1), ts = r.GetDouble(2),
                        trigger = NullStr(r, 3), phash = NullStr(r, 4), bytes = NullLong(r, 5) });

                return Results.Json(new { examId, archived = true, exam, adjudications = adj, events = evs, images = imgs });
            }
            catch (SqliteException) { return Results.NotFound(new { error = "no_archive" }); }
        });

        // ================= 管理 / 複核 (寫) =================

        // 建/更新考試 + 座位綁定
        app.MapPost("/api/exams", async (HttpContext ctx) =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            if (body is null) return Results.BadRequest(new { error = "bad_json" });
            string examId = (string?)body["examId"] ?? "";
            string name = (string?)body["name"] ?? examId;
            if (string.IsNullOrEmpty(examId)) return Results.BadRequest(new { error = "missing_examId" });
            if (!IsSafeId(examId)) return Results.BadRequest(new { error = "bad_examId" });   // 禁路徑危險字元/超長(防目錄混放·允許中文)
            if (body["seats"] is JsonArray seatsPre)
                foreach (JsonNode? sn in seatsPre)
                {
                    string sid = (string?)sn?["seatId"] ?? "";
                    if (sid.Length == 0) return Results.BadRequest(new { error = "empty_seatId" });   // 空 seatId 不進 seats 表(髒資料汙染席位關聯/看板)
                    if (!IsSafeId(sid)) return Results.BadRequest(new { error = "bad_seatId" });
                }

            double now = Now();
            int seatCount = db.Locked(conn =>
            {
                using (SqliteCommand ex = conn.Cmd(
                    @"INSERT INTO exams (exam_id,name,status,started_at,created_at)
                      VALUES (@id,@n,'active',@ts,@ts)
                      ON CONFLICT(exam_id) DO UPDATE SET name=@n",
                    ("@id", examId), ("@n", name), ("@ts", now)))
                    ex.ExecuteNonQuery();

                int n = 0;
                if (body["seats"] is JsonArray seats)
                {
                    foreach (JsonNode? sn in seats)
                    {
                        if (sn is null) continue;
                        using SqliteCommand sc = conn.Cmd(
                            @"INSERT INTO seats (exam_id,seat_id,student_id,machine_id,agent_id,display_name)
                              VALUES (@e,@s,@stu,@m,@a,@d)
                              ON CONFLICT(exam_id,seat_id) DO UPDATE SET
                                student_id=@stu, machine_id=@m, agent_id=@a, display_name=@d",
                            ("@e", examId), ("@s", (string?)sn["seatId"] ?? ""),
                            ("@stu", (string?)sn["studentId"]), ("@m", (string?)sn["machineId"]),
                            ("@a", (string?)sn["agentId"]), ("@d", (string?)sn["displayName"]));
                        sc.ExecuteNonQuery();
                        n++;
                    }
                }
                return n;
            });
            return Results.Json(new { ok = true, examId, seatCount });
        });

        // 結束考試:落狀態 + 向該考試線上 Agent 廣播 exam_ended(Agent 排空緩衝後停採、登出回待命)
        app.MapPost("/api/exams/{examId}/end", async (string examId, HttpContext ctx) =>
        {
            if (!IsSafeId(examId)) return Results.BadRequest(new { error = "bad_examId" });
            double now = Now();
            int changed = db.Locked(conn =>
            {
                // 狀態守衛:只結束 active/ended 考試;絕不把 archiving/archived 復活成 ended
                // (否則繞過 /integrity 的 applicable:false 保護 → 對已清空的 live 行回傳空稽核偽裝 ok:true)。
                using SqliteCommand c = conn.Cmd(
                    "UPDATE exams SET status='ended', ended_at=@ts WHERE exam_id=@e AND status IN ('active','ended')",
                    ("@ts", now), ("@e", examId));
                return c.ExecuteNonQuery();
            });
            if (changed == 0) return Results.NotFound();
            int notified = await hub.PushExamEndedAsync(examId, ctx.RequestAborted);
            return Results.Json(new { ok = true, examId, status = "ended", notified });
        });

        // 全場遠程登出(監考員):吊銷該考試全部 OIDC 採集工作階段 + 通知並強斷線上連接。
        // Agent 收到 session_revoked 即棄工作階段回待命;重連/上報因 invalid_session 一律 401,須重新 OIDC 登入。
        app.MapPost("/api/exams/{examId}/logout", async (string examId, HttpContext ctx) =>
        {
            if (!IsSafeId(examId)) return Results.BadRequest(new { error = "bad_examId" });
            int revoked = sessions.RevokeByExam(examId, Now());
            int notified = await hub.PushSessionRevokedAsync(examId, ctx.RequestAborted);
            return Results.Json(new { ok = true, examId, revoked, notified });
        });

        // 下發設定熱更新:存最新設定並推送給該考試所有線上 Agent(新連/重連 Agent 在 hello 時也會收到)
        app.MapPost("/api/exams/{examId}/config", async (string examId, HttpContext ctx) =>
        {
            if (!IsSafeId(examId)) return Results.BadRequest(new { error = "bad_examId" });   // 與建考試端點一致(縱深:防汙染設定表/記憶體字典鍵)
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            if (body is not JsonObject) return Results.BadRequest(new { error = "config 必须是对象" });
            int pushedTo = await hub.PushConfigAsync(examId, body.ToJsonString(), ctx.RequestAborted);
            return Results.Json(new { ok = true, examId, pushedTo });
        });

        // 讀回該考試當前已下發/持久化的設定(供看板白名單編輯器回填);未下發過 → config:null。
        app.MapGet("/api/exams/{examId}/config", (string examId) =>
        {
            if (!IsSafeId(examId)) return Results.BadRequest(new { error = "bad_examId" });
            string? cfg = hub.GetConfig(examId);
            if (cfg is null) return Results.Json(new { examId, config = (object?)null });
            try { return Results.Json(new { examId, config = JsonNode.Parse(cfg) }); }
            catch { return Results.Json(new { examId, config = (object?)null }); }   // 儲存損壞備援
        });

        // 監考員點名抓圖(D2):向指定線上 Agent 推 capture_now,Agent 立即抓一張(dedup 關閉)。agent 不線上 → pushed:false。
        app.MapPost("/api/agents/{agentId}/capture", async (string agentId, HttpContext ctx) =>
        {
            string reason = "capture_now";
            try
            {
                JsonNode? body = await JsonNode.ParseAsync(ctx.Request.Body);
                if ((string?)body?["reason"] is string rs && rs.Length > 0) reason = rs;
            }
            catch (JsonException) { /* 無 body / 非 JSON:用預設 reason */ }
            bool pushed = await hub.PushCaptureNowAsync(agentId, reason, ctx.RequestAborted);
            return Results.Json(new { ok = true, agentId, pushed });
        });

        // 歸檔作業手動觸發(維運):立即掃描到齡考試並歸檔 + 清理。後台每 6h 也自動跑。回傳本次報告。
        // ct 用 **ApplicationStopping** 而非 ctx.RequestAborted:歸檔一旦啟動就跑完一批(用戶端斷開/超時不半途中斷),
        // 僅伺服器關停才在考試之間停(每場原子安全)。
        app.MapPost("/api/archive/run", () =>
        {
            ArchiveService.Report report = archive.RunOnce(Now(), app.Lifetime.ApplicationStopping);
            return Results.Json(report);
        });

        // 可疑裁決(人工)
        app.MapPost("/api/suspicious/{id:long}/decide", async (long id, HttpContext ctx) =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            string status = (string?)body?["status"] ?? "";
            if (status is not ("confirmed" or "dismissed"))
                return Results.BadRequest(new { error = "status must be confirmed|dismissed" });
            string? reviewer = (string?)body?["reviewer"];
            string? note = (string?)body?["note"];
            double now = Now();

            return db.Locked(conn =>
            {
                // 採集健康告警(source='health')唯讀、不可裁決：攔截誤調,保持「採集健康」面板與作弊裁決解耦。
                using (SqliteCommand chk = conn.Cmd(
                    @"SELECT 1 FROM suspicious_queue WHERE id=@id AND source='health'", ("@id", id)))
                {
                    if (chk.ExecuteScalar() is not null)
                        return Results.BadRequest(new { error = "health items are read-only" });
                }
                using (SqliteCommand up = conn.Cmd(
                    @"UPDATE suspicious_queue SET status=@st, reviewer=@r, note=@n, decided_at=@ts WHERE id=@id",
                    ("@st", status), ("@r", reviewer), ("@n", note), ("@ts", now), ("@id", id)))
                {
                    if (up.ExecuteNonQuery() == 0) return Results.NotFound();
                }
                using SqliteCommand c = conn.Cmd(
                    @"SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note
                      FROM suspicious_queue WHERE id=@id", ("@id", id));
                using SqliteDataReader r = c.ExecuteReader();
                r.Read();
                var item = new
                {
                    id = r.GetInt64(0),
                    seatId = r.GetString(1),
                    ts = r.GetDouble(2),
                    kind = r.GetString(3),
                    score = r.GetInt32(4),
                    status = r.GetString(5),
                    refs = ParseNode(NullStr(r, 6)),
                    reviewer = NullStr(r, 7),
                    decidedAt = NullDouble(r, 8),
                    note = NullStr(r, 9),
                };
                return Results.Json(new { ok = true, item });
            });
        });
    }

    /// examId / seatId 安全性:非空、≤128、不含路徑危險字元(/ \ : * ? " &lt; &gt; | 控制字元)與 ".."。
    /// **允許中文/字母數字/-_ 等普通字元**(考試名可中文)。防不同 id 經 Storage.Safe 歸一到同一磁碟目錄(目錄混放),
    /// 並收斂所有下游鍵長度。管理端點已鑑權,此為縱深收斂。internal:ExamDispatch 派生 seat 時重複使用同一規則。
    internal static bool IsSafeId(string id)
    {
        if (id.Length is 0 or > 128 || id.Contains("..", StringComparison.Ordinal)) return false;
        foreach (char c in id)
            if (c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        return true;
    }

    // ---- 歸檔庫唯讀小工具(F3:歸檔後證據/中繼資料仍可經 HTTP 取證) ----
    private static SqliteConnection OpenArchiveRead(string archiveDbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archiveDbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// 歸檔庫裡該圖的冷存相對路徑(archive/<exam>/<seat>/<id>.webp);無歸檔庫/無此圖 → null。
    private static string? ArchiveImagePath(string archiveDbPath, string imageId)
    {
        if (!File.Exists(archiveDbPath)) return null;
        try
        {
            using SqliteConnection conn = OpenArchiveRead(archiveDbPath);
            using SqliteCommand c = conn.Cmd("SELECT file_path FROM archive_images WHERE image_id=@id", ("@id", imageId));
            return c.ExecuteScalar() as string;
        }
        catch (SqliteException) { return null; }
    }

    /// 歸檔庫裡該圖的中繼資料(帶 archived:true)+ 視覺結果;無則 404。
    private static IResult ArchiveImageMeta(string archiveDbPath, string imageId)
    {
        if (!File.Exists(archiveDbPath)) return Results.NotFound();
        try
        {
            using SqliteConnection conn = OpenArchiveRead(archiveDbPath);
            using SqliteCommand c = conn.Cmd(
                "SELECT image_id,seat_id,ts,trigger,phash,width,height,bytes FROM archive_images WHERE image_id=@id", ("@id", imageId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return Results.NotFound();
            return Results.Json(new
            {
                imageId = r.GetString(0), seatId = r.GetString(1), ts = r.GetDouble(2),
                trigger = NullStr(r, 3), phash = NullStr(r, 4), width = NullInt(r, 5), height = NullInt(r, 6),
                bytes = NullLong(r, 7), archived = true,
            });
        }
        catch (SqliteException) { return Results.NotFound(); }
    }

    // ---- 讀取小工具 ----
    private static long Scalar(SqliteConnection conn, string sql, params (string, object?)[] ps)
    {
        using SqliteCommand c = conn.Cmd(sql, ps);
        object? v = c.ExecuteScalar();
        return v is null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static T Scalar<T>(SqliteConnection conn, string sql, params (string, object?)[] ps)
    {
        using SqliteCommand c = conn.Cmd(sql, ps);
        object? v = c.ExecuteScalar();
        return v is null || v is DBNull ? default! : (T)v;
    }

    private static string? NullStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? NullDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static int? NullInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? NullLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return JsonValue.Create(json); }
    }
}
