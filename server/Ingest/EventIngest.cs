using System.Net.WebSockets;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Analysis;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 事件通道(WebSocket /ingest/events)。見 api-contract §1。
/// 握手檢核 X-Horus-Auth;每事件檢核 sig;冪等寫入資料庫(agent_id,seq,type);risk≥閾值入可疑佇列。
/// M4:鑑權金鑰可為共享 PSK 或 OIDC 工作階段 K_sess(見 IngestAuth);OIDC 路徑強制事件體身分==工作階段身分(閉合 A1/A2)。
public sealed class EventIngest(Db db, ServerConfig cfg, AgentHub hub, SessionStore sessions, ILogger<EventIngest> log)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

        string examId = ctx.Request.Query["examId"].ToString();
        string seatId = ctx.Request.Query["seatId"].ToString();
        string agentId = ctx.Request.Query["agentId"].ToString();

        // 握手鑑權(見 §1.1)。M4:先解析鑑權上下文(PSK ↔ OIDC 工作階段共存)。
        string sessionId = ctx.Request.Headers["X-Horus-Session"].ToString();
        IngestAuth.Resolved auth = IngestAuth.Resolve(cfg, sessions, sessionId, examId, seatId, agentId, Now());
        if (!auth.Ok)
        {
            ctx.Response.StatusCode = 401;
            log.LogWarning("事件握手鉴权拒绝 agent={Agent} seat={Seat} 原因={Err}", agentId, seatId, auth.Error);
            return;
        }
        if (auth.Key is not null)   // 用 PSK 或工作階段 K_sess 驗握手
        {
            string got = ctx.Request.Headers["X-Horus-Auth"].ToString();
            string want = Auth.Handshake(auth.Key, examId, seatId, agentId);
            if (!Crypto.FixedTimeEquals(got, want))
            {
                ctx.Response.StatusCode = 401;
                log.LogWarning("事件握手鉴权失败 agent={Agent} seat={Seat}", agentId, seatId);
                return;
            }
        }

        using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
        CancellationToken ct = ctx.RequestAborted;
        AgentHub.Conn conn = hub.Register(agentId, examId, ws);   // 登記線上連接(供 config_update 下推)
        log.LogInformation("Agent 连接 exam={Exam} seat={Seat} agent={Agent}", examId, seatId, agentId);

        // 閉合全場登出競態:握手期(IngestAuth.Resolve 已讀取工作階段)與 hub.Register 之間存在視窗——若此刻發生 /logout,
        // RevokeByExam(DELETE 工作階段)恆先於 PushSessionRevokedAsync(遍歷 _conns 強斷)。故本連接若在 abort 遍歷時尚未登記,
        // 會被漏斷;而收影格循環只用握手期快取的 auth,從不復查工作階段。登記後立即複查一次 OIDC 工作階段仍在:
        // 因 DELETE 先於 abort,凡"錯過 abort"的連接必然登記於 DELETE 之後 → 此複查必發現工作階段已吊銷 → 主動斷開。
        if (auth.Session is not null && sessions.Get(sessionId, Now()) is null)
        {
            hub.Unregister(agentId, conn);
            log.LogInformation("Agent 登记后发现会话已吊销/过期,断开 exam={Exam} seat={Seat} agent={Agent}", examId, seatId, agentId);
            try { ws.Abort(); } catch { /* 已斷,忽略 */ }
            return;
        }

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                string? msg = await WsUtil.ReceiveTextAsync(ws, ct);
                if (msg is null) break;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(msg); }
                catch { continue; }   // 非法 JSON,忽略

                using (doc)
                {
                    JsonElement root = doc.RootElement;
                    string frameType = Str(root, "type") ?? "";
                    try
                    {
                        switch (frameType)
                        {
                            case "hello":    await OnHelloAsync(conn, agentId, ct); break;
                            case "event":    await OnEventAsync(conn, root, auth.Key, auth.Session, ct); break;
                            case "ping":     await conn.SendAsync("{\"v\":1,\"type\":\"pong\"}", ct); break;
                            case "pong":     break;
                            default:         break;
                        }
                    }
                    catch (Exception ex) { log.LogError(ex, "处理帧异常 type={Type}", frameType); }
                }
            }
        }
        finally { hub.Unregister(agentId, conn); }

        // 完成關閉握手(對端主動關閉時回一影格 Close),避免用戶端 WS 報異常關閉。
        try
        {
            if (ws.State is WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* 連接已斷,忽略 */ }

        log.LogInformation("Agent 断开 seat={Seat} agent={Agent}", seatId, agentId);
    }

    private async Task OnHelloAsync(AgentHub.Conn conn, string agentId, CancellationToken ct)
    {
        (long maxSeq, string? examStatus) = db.Locked(c2 =>
        {
            using SqliteCommand c = c2.Cmd("SELECT COALESCE(MAX(seq),0) FROM events WHERE agent_id=@a", ("@a", agentId));
            long ms = Convert.ToInt64(c.ExecuteScalar());
            using SqliteCommand st = c2.Cmd("SELECT status FROM exams WHERE exam_id=@e", ("@e", conn.ExamId));
            return (ms, st.ExecuteScalar() as string);
        });
        await conn.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "hello_ack", maxSeq }), ct);

        // 該考試若已設設定,連上即推一次,使新連 / 重連的 Agent 拿到當前設定
        string? cfgJson = hub.GetConfig(conn.ExamId);
        if (cfgJson is not null)
            await conn.SendAsync(AgentHub.BuildConfigFrame(cfgJson), ct);

        // 考試派發:Agent 離線期間考試被結束/歸檔 → 重連 hello 時補發 exam_ended(先讓上面的續傳視窗存在:
        // Agent 收到後先排空緩衝再停採回待命,end 時刻附近的證據不丟)。無考試記錄(整合測試/psk 未建考試)不發。
        if (examStatus is not null && examStatus != "active")
            await conn.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "exam_ended", examId = conn.ExamId }), ct);
    }

    private async Task OnEventAsync(AgentHub.Conn link, JsonElement frame, byte[]? authKey, HorusSession? session, CancellationToken ct)
    {
        if (!frame.TryGetProperty("event", out JsonElement e) || e.ValueKind != JsonValueKind.Object) return;

        // BUG#6:信封外層 seq 與內層 @event.seq 冗餘(線協定)。Agent 用內層 e.Seq 簽名,故**以內層為權威**;
        // 若外層與內層不一致(協定被中間改寫 / 版本錯配)記告警,但不阻斷(仍按內層權威 seq 驗籤與寫入資料庫)。
        long seq = e.TryGetProperty("seq", out JsonElement eSeqEl) && eSeqEl.TryGetInt64(out long eSeq)
            ? eSeq
            : (frame.TryGetProperty("seq", out JsonElement sq) && sq.TryGetInt64(out long s) ? s : 0);
        if (frame.TryGetProperty("seq", out JsonElement outerSeq) && outerSeq.TryGetInt64(out long outerS) && outerS != seq)
            log.LogWarning("信封外层 seq({Outer}) 与内层 seq({Inner}) 不一致(以内层为准)", outerS, seq);
        string? sig = Str(frame, "sig");

        string examId = Str(e, "examId") ?? "";
        string seatId = Str(e, "seatId") ?? "";
        string agentId = Str(e, "agentId") ?? "";
        string machineId = Str(e, "machineId") ?? "";

        // M4·閉合 A1:OIDC 路徑下,事件體自報身分**必須 == 工作階段綁定身分**,否則是拿自己工作階段給他人栽贓 —— 拒收。
        // (K_sess 只證明「是本工作階段所籤」,不證明「身分為真」;身分真偽靠此強制 + OIDC 認證。)seq 空間歸屬本工作階段 agent → 閉合 A2。
        if (session is not null && !session.IdentityMatches(examId, seatId, agentId))
        {
            await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "identity_mismatch", seq }), ct);
            log.LogWarning("事件身份与会话不符(疑跨身份栽赃) session={Seat} bodyAgent={Agent}", session.SeatId, agentId);
            return;
        }
        double ts = e.TryGetProperty("ts", out JsonElement tse) && tse.TryGetDouble(out double t) ? t : 0;
        string typeStr = Str(e, "type") ?? "";
        int risk = e.TryGetProperty("risk", out JsonElement rke) && rke.TryGetInt32(out int rr) ? rr : 0;
        string? evidenceImageId = Str(e, "evidenceImageId");
        string? hashPrev = Str(e, "hashPrev");
        string? hashSelf = Str(e, "hashSelf");
        string payloadRaw = e.TryGetProperty("payload", out JsonElement pe) ? pe.GetRawText() : "{}";

        // 驗籤:sig = HMAC(key, hashSelf + "\n" + seq)。key = PSK 或 OIDC 工作階段 K_sess(見 IngestAuth)。僅相依 hashSelf 字串。
        if (authKey is not null)
        {
            string want = EventCanonical.Sig(authKey, hashSelf ?? "", seq);
            if (sig is null || !Crypto.FixedTimeEquals(sig, want))
            {
                await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "bad_sig", seq }), ct);
                log.LogWarning("事件验签失败 agent={Agent} seq={Seq}", agentId, seq);
                return;
            }

            // 完整性複驗(M3·閉合 §10.1「伺服器不重算 canonical」):sig 只證明「知道 PSK 且承諾了 hashSelf+seq」,
            // 本身**不保證 hashSelf 綁定 payload**。此處從**原始 payload 文字 + 寫入資料庫欄位**逐位元組復算 canonicalCore、
            // 復算 hashSelf,要求與自報 hashSelf 一致 —— 使 hashSelf/sig 成為**真正錨定 payload/欄位**的取證錨點。
            // 任何 payload / 欄位與 hashSelf 不符(自回報錯誤亂或傳輸中被非 PSK 方篡改) → 拒收,不寫入資料庫。
            // 注:持 PSK 學員機仍可自洽地偽造 payload+hashSelf(結構性殘留,靠截圖/視覺/人工備援,見 §10.1),
            //     此步保證的是「錨點確實承諾其 payload」+「鏈後稽核可發現寫入資料庫後改動/刪增」。
            if (!EventCanonical.VerifyHashSelf(hashPrev ?? "GENESIS", examId, seatId, agentId, machineId,
                    ts, typeStr, payloadRaw, risk, evidenceImageId, seq, hashSelf))
            {
                await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "bad_hash", seq }), ct);
                log.LogWarning("事件哈希复验失败(hashSelf 不承诺其 payload) agent={Agent} seq={Seq}", agentId, seq);
                return;
            }
        }

        double recvTs = Now();

        // 伺服器側風險復判(**不信任 Agent 自報 risk**):憑獨立黑名單 + 該考試已下發白名單重算。
        // 有效風險 = max(agentRisk, serverRisk);持 PSK 學員機把「訪問 AI 站」籤成 risk=0 也壓不住入隊。
        // 策略取 AgentHub **已解析快取**(下發時重建),免每事件熱路徑重複 JsonDocument.Parse + 建 HashSet。
        SignalType sigType = ParseType(typeStr);
        JsonElement payloadEl = e.TryGetProperty("payload", out JsonElement pEl) ? pEl : default;
        RiskModel.Policy policy = hub.GetPolicy(examId);
        int serverRisk = RiskModel.Derive(sigType, payloadEl, policy.Hosts, policy.Procs, policy.PasteThreshold);
        int effRisk = Math.Max(risk, serverRisk);

        // 寫入資料庫 + 入可疑佇列在**同一寫鎖交易**內完成:避免二者分處兩個交易時,崩潰/歸檔視窗卡在中間導致
        // 高危事件已寫入資料庫卻漏入隊(復傳因 ON CONFLICT DO NOTHING 不再補入隊),或入隊一條指向已歸檔刪除事件的孤兒 pending。
        db.Locked(conn =>
        {
            // 歸檔中/已歸檔考試:短路不寫入資料庫(避免"讀快照→DELETE"視窗內的 late-ingest 被無錨點刪)。仍會 ack 使 Agent 停發。
            if (conn.IsExamSealed(examId)) return;

            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO events (exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,server_risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (@exam,@seat,@agent,@machine,@seq,@ts,@recv,@type,@payload,@risk,@srisk,@ev,@hp,@hs,@sig)
                  ON CONFLICT(agent_id,seq) DO NOTHING",
                ("@exam", examId), ("@seat", seatId), ("@agent", agentId), ("@machine", machineId), ("@seq", seq),
                ("@ts", ts), ("@recv", recvTs), ("@type", typeStr), ("@payload", payloadRaw),
                ("@risk", risk), ("@srisk", serverRisk), ("@ev", evidenceImageId), ("@hp", hashPrev), ("@hs", hashSelf), ("@sig", sig));
            int changed = ins.ExecuteNonQuery();

            long? id = null;
            if (changed > 0)
            {
                using SqliteCommand idc = conn.Cmd("SELECT last_insert_rowid()");
                id = Convert.ToInt64(idc.ExecuteScalar());

                // 觸發型抓圖 → 標記證據圖
                if (evidenceImageId is not null)
                {
                    using SqliteCommand mk = conn.Cmd("UPDATE images SET is_evidence=1 WHERE image_id=@id", ("@id", evidenceImageId));
                    mk.ExecuteNonQuery();
                }
            }

            // 心跳寫線上表:不論新舊都刷新;寫後**裁剪**只保留該 (exam,seat,agent) 最新 ts 的一行 ——
            // 線上判定只需最新一條,故無須留歷史;同時防持 PSK 者用不斷變化的 ts 無界追加撐爆表(replay 的舊 ts 會被裁掉)。
            if (typeStr == "heartbeat")
            {
                string status = TryGetPayloadStr(payloadRaw, "status") ?? "alive";
                using (SqliteCommand hb = conn.Cmd(
                    "INSERT INTO agent_heartbeats (agent_id,exam_id,seat_id,ts,status) VALUES (@a,@e,@s,@ts,@st) ON CONFLICT(exam_id,seat_id,agent_id,ts) DO UPDATE SET status=@st",
                    ("@a", agentId), ("@e", examId), ("@s", seatId), ("@ts", ts), ("@st", status)))
                    hb.ExecuteNonQuery();
                using (SqliteCommand prune = conn.Cmd(
                    @"DELETE FROM agent_heartbeats WHERE exam_id=@e AND seat_id=@s AND agent_id=@a
                        AND ts < (SELECT MAX(ts) FROM agent_heartbeats WHERE exam_id=@e AND seat_id=@s AND agent_id=@a)",
                    ("@e", examId), ("@s", seatId), ("@a", agentId)))
                    prune.ExecuteNonQuery();
            }

            // 只對**新寫入資料庫**事件入可疑佇列(避免重傳重複入隊);用有效風險判閾值,url_unreadable 無視閾值。
            // agentRisk 低於閾值但 serverRisk 頂上去 → 記 agent_risk_understated,是篡改逃逸的取證信號。
            // M5 健康信號(source='health')無論風險分都入「採集健康」面板(純提示,不汙染作弊裁決率)。
            string healthKind = Suspicion.KindFor(sigType, payloadEl);
            bool isHealth = Suspicion.SourceForKind(healthKind) == "health";
            if (id is not null && typeStr != "heartbeat" &&
                (effRisk >= cfg.RiskThreshold || IsForcedReview(typeStr, payloadRaw) || isHealth))
            {
                string? tamperNote = !isHealth && serverRisk >= cfg.RiskThreshold && risk < cfg.RiskThreshold
                    ? $"agent_risk_understated agent={risk} server={serverRisk}" : null;
                EnqueueSuspicious(conn, examId, seatId, ts, typeStr, effRisk, id.Value, evidenceImageId, payloadRaw, tamperNote);
            }
        });

        // ---- 三道門的採集面寫入口(貝塔通 P88–P92)----
        // ★ **重複使用既有上行通道**:Agent 本來就在持續上傳事件流,不必另開一個心跳端點
        //   (其 rp-contract 對桌面用戶端明寫可以重複使用)。
        // ★★ `active` **由 Agent 自己定義**(採集機上有無使用者活動),不是瀏覽器那套 —— 它更準。
        //   ★ **缺 `active` 欄位時按 false 算**:那說明採集端版本比伺服器舊,是部署事故。
        //   按 true 算能讓它繼續跑,但代價是 idle 那道門對那批機器**靜默失效**,
        //   而失效這件事沒有任何症狀。寧可讓它以「學生 30 分鐘後被踢」的形態暴露出來。
        // ★ 放在寫交易**之外**:Heartbeat 自己要取寫鎖,套在裡面就是重入。
        if (typeStr == "heartbeat" && session is not null)
            sessions.Heartbeat(session.SessionId, TryGetPayloadBool(payloadRaw, "active") ?? false, Now());

        // 逐條 ack 本條 seq(不用範圍 upto):即使 seq 空間有空洞,也不會誤刪從未送達的低 seq 事件
        await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "ack", seq }), ct);
    }

    /// 從 payload 原文裡取一個布爾;不存在 / 類型不符回傳 null(由呼叫方決定預設語義)。
    private static bool? TryGetPayloadBool(string payloadRaw, string key)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(payloadRaw);
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty(key, out JsonElement v))
                return v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
        }
        catch (JsonException) { /* payload 非法 JSON:當作沒這個欄位 */ }
        return null;
    }

    /// 抓不到 URL 的降級信號 = 強制人工複核(無視風險閾值),否則該備援鏈斷在最後一步。
    private static bool IsForcedReview(string typeStr, string payloadRaw)
        => typeStr == "browser_url" && TryGetPayloadStr(payloadRaw, "note") == "url_unreadable";

    /// 入可疑佇列。**在呼叫方的寫鎖交易內**執行(與事件寫入資料庫同一交易),故傳入 conn 而非另開 db.Locked。
    private static void EnqueueSuspicious(SqliteConnection conn, string examId, string seatId, double ts, string typeStr,
        int score, long eventId, string? evidenceImageId, string payloadRaw, string? note)
    {
        SignalType type = ParseType(typeStr);
        JsonElement payload;
        try { using var pd = JsonDocument.Parse(payloadRaw); payload = pd.RootElement.Clone(); }
        catch { payload = default; }

        string kind = Suspicion.KindFor(type, payload);
        string source = Suspicion.SourceForKind(kind);   // M5 健康信號→health(唯讀面板)；其餘→suspicion(可裁決)
        var refs = new List<string> { $"event:{eventId}" };
        if (evidenceImageId is not null) refs.Add($"image:{evidenceImageId}");
        string refsJson = JsonSerializer.Serialize(refs);

        using SqliteCommand c = conn.Cmd(
            @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs,note,source)
              VALUES (@e,@s,@ts,@k,@sc,'pending',@refs,@note,@src)",
            ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", score),
            ("@refs", refsJson), ("@note", note), ("@src", source));
        c.ExecuteNonQuery();
    }

    // ---- 小工具 ----
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string? Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

    private static long LongProp(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out JsonElement e) && e.TryGetInt64(out long v) ? v : 0;

    private static string? TryGetPayloadStr(string payloadRaw, string prop)
    {
        try { using var d = JsonDocument.Parse(payloadRaw); return Str(d.RootElement, prop); }
        catch { return null; }
    }

    private static SignalType ParseType(string s) => s switch
    {
        "window_focus" => SignalType.WindowFocus,
        "browser_url" => SignalType.BrowserUrl,
        "process_start" => SignalType.ProcessStart,
        "process_exit" => SignalType.ProcessExit,
        "clipboard" => SignalType.Clipboard,
        "alt_tab_burst" => SignalType.AltTabBurst,
        "usb" => SignalType.Usb,
        "screenshot" => SignalType.Screenshot,
        "heartbeat" => SignalType.Heartbeat,
        // M5 採集端硬化健康信號
        "watchdog_restart" => SignalType.WatchdogRestart,
        "suspected_suspend" => SignalType.SuspectedSuspend,
        "screenshot_obscured" => SignalType.ScreenshotObscured,
        "capability_degraded" => SignalType.CapabilityDegraded,
        _ => SignalType.WindowFocus,
    };
}
