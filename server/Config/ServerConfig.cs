using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Server.Config;

/// 伺服器設定(從 server.config.json 載入,camelCase)。record 便於用 with 施加環境變數覆蓋。
public sealed record ServerConfig
{
    /// Kestrel 綁定位址(可多個,逗號分隔),如 "http://0.0.0.0:8080"。默认 8080 與 Agent 內建預設 / dist 成品 / 部署文件一致。
    public string Urls { get; init; } = "http://0.0.0.0:8080";

    /// 資料根目錄:SQLite 檔案與截圖原圖都在此下。
    public string DataDir { get; init; } = "./data";

    /// SQLite 檔案路徑;":memory:" 走記憶體(測試用)。相對路徑相對 DataDir。
    public string DbPath { get; init; } = "horus.db";

    /// 預共享 HMAC 金鑰(base64)。與 Agent 同一把。留空則**關閉驗簽**(僅本機整合測試,生產必須配)。
    public string? PskBase64 { get; init; }

    /// 管理/看板權杖。所有 /api/* 請求需帶 X-Horus-Admin 頭(圖片位元組端點可用 ?t= 查詢)。
    /// 留空則關閉管理鑑權(僅本機整合測試)。防止學員機調 /api/exams/{id}/config 關掉全場檢測。
    public string? AdminToken { get; init; }

    /// 擊鍵旁路金鑰(base64)。判題後端持它對 /ingest/keystroke 提交體簽名(X-Horus-KSig)。
    /// 留空則**關閉擊鍵鑑權**(僅本機整合測試)。防同網學員機偽造/栽贓他人擊鍵樣本。與採集 PSK / 管理權杖相互獨立。
    public string? KeystrokeSecretBase64 { get; init; }

    /// 允許在非 loopback 綁定下缺 PSK / 管理權杖啟動(裸奔)。預設 false = fail-closed。僅整合測試開。
    public bool AllowInsecure { get; init; }

    /// 啟動成功後自動在預設瀏覽器打開管理端看板(僅 Windows 交互式執行的真 exe;測試宿主/輸出重定向/服務化不彈)。
    public bool OpenDashboard { get; init; } = true;

    /// Cloudflare Web Analytics 公開 site token。僅未來啟用的 betaoi.cn 正式主機手動載入；cc 由 Cloudflare 自動注入；LAN/IP 下不出網。
    public string? CloudflareWebAnalyticsToken { get; init; } = "c113fb69d7e84d38a645c5160f6f1bda";

    // ---- M4 身分層:wentian OIDC 取代共享 PSK(見 docs/m4-identity-oidc.md)----
    /// 採集面鑑權模式:"psk"(預設·共享 PSK·M1-M3 原樣) | "oidc"(僅 OIDC 工作階段) | "both"(共存·遷移期退回網)。
    public string AuthMode { get; init; } = "psk";
    /// 貝塔通 OIDC issuer。OIDC 模式必配，不提供靜默預設。
    /// P147/P149 當前為 pass.betaoi.cc；cn 休眠。issuer 標識符與端點前綴分別設定。
    /// 切換 issuer 後重新登入；現場設定由具體部署操作審閱，本輪不批量改設備。
    public string? OidcIssuer { get; init; }
    /// 協定端點的取回前綴，留空使用 issuer；根路徑為 /auth、/token、/jwks、/me、/session/end。
    /// 保留手寫設定與覆蓋能力，不做執行環境 discovery 或開啟休眠 cn。
    public string? OidcEndpointBase { get; init; }
    /// Horus 在 wentian 註冊的 client_id(預設 horus-client)。
    public string? OidcClientId { get; init; }
    /// client_secret 明文(僅整合測試;生產用 OidcClientSecretEnc 或 env HORUS_OIDC_SECRET)。Server-Broker:secret 只在伺服器。
    public string? OidcClientSecret { get; init; }
    /// client_secret DPAPI 密文(與視覺 key 同機制,見 SecretProtect)。
    public string? OidcClientSecretEnc { get; init; }
    /// wentian 的 JWKS(RSA 公鑰)內聯 JSON:區域網路離線驗 id_token 用,免執行環境拉取。留空則啟動時從 issuer 拉取 + 快取。
    public string? OidcJwksJson { get; init; }
    /// 採集工作階段的 **absolute** 門(分鐘):建工作階段時寫死的到期時刻。★ **任何活動都推不動它**。
    /// 預設 **360(6 小時)**,與貝塔通給的預設值一致(其 P88)。
    ///
    /// ★★ **它必須覆蓋得住一整場考試**:考試 2–3 小時,而學員往往在開考前就登入了。
    ///   不夠長的表現是**考到一半被踢**,而重登要完整走一遍瀏覽器授權(取消 SSO 後還要輸密碼)。
    ///   開考預檢(其 P91)因此改為 **Horus 自己算自己的剩餘** —— 原設計靠 IdP 下發一個 claim
    ///   告訴 RP「你的工作階段還剩多久」,那條隨取消 SSO 一併作廢。
    /// ★ 各平台**完全自由調這三個值、不登記、貝塔通不攔**(其 P90 已取消原來那條
    ///   「RP 的 absolute 不得超過 IdP 的」)。備援改由永不放棄的撤權重投承擔。
    public int OidcSessionMinutes { get; init; } = 360;

    // ---- 三道門的另外兩道(貝塔通 P88–P92·兩條連線路徑共用) ----
    // ★ 採集端與看板**共用這兩個值**:三道門的數值口徑對兩者相同,不同的只是「誰來發心跳、
    //   `active` 怎麼算」(看板用 visibilityState + 輸入事件;採集端用機器使用者活動)。
    /// **idle** 門(分鐘):距最後一次「人還在」多久算失效。預設 30。擋的是**頁面開著但人走了**。
    /// ★ `last_seen_at` **只由帶 `active: true` 的心跳更新**,任何業務請求都不續它(其 P92)。
    public int SessionIdleMinutes { get; init; } = 30;
    /// **心跳**門(分鐘):距最後一次收到心跳多久算失效。預設 15(= 5 分鐘間隔 × 容忍 3 次)。
    /// 擋的是**關頁面 / 關瀏覽器走人**;與 idle 擋的是兩件事,**不要合併**。
    public int SessionHeartbeatMinutes { get; init; } = 15;

    /// **開考預檢**用的預計考試時長(分鐘),預設 180。★ 它**不參與任何鑑權判定**,
    /// 只用來回答一個問題:「你這次登入的剩餘壽命夠不夠撐完一場考試」(貝塔通 P91)。
    ///
    /// ★★ 原設計是**靠 IdP 下發一個 claim** 告訴 RP「你的工作階段還剩多久」,
    ///   **取消 SSO(其 P84)之後那條已作廢** —— 改為 Horus 按自己的 absolute 自己算。
    /// ★ 兩個用戶端上它的意義不同,見 `docs/m4-identity-oidc.md`:
    ///   採集端每場考試都是**新登入**,所以剩餘恆為滿,這個檢查真正能逮到的是
    ///   **`oidcSessionMinutes` 配短了**;監考看板那邊監考員可能幾小時前就登入了,
    ///   剩餘不足是會真實發生的。
    public int ExpectedExamMinutes { get; init; } = 180;

    [JsonIgnore]
    public bool OidcEnabled => AuthMode is "oidc" or "both";
    [JsonIgnore]
    public bool PskAcceptedForIngest => AuthMode is "psk" or "both";
    /// 端點取回位址的前綴:優先 <see cref="OidcEndpointBase"/>,留空則回落 issuer(P72)。
    [JsonIgnore]
    private string? EndpointBase =>
        (string.IsNullOrEmpty(OidcEndpointBase) ? OidcIssuer : OidcEndpointBase)?.TrimEnd('/');

    // ★★ 貝塔通的協定端點**掛在根路徑**(其 docs/oidc-mounting.md:`/auth` `/token` `/me` `/jwks`
    //    `/session/end` `/request` 已佔用根命名空間),**不是** 舊 wentian 的 `/oauth/*`。
    //    照舊路徑拼會得到 404,而表現是「token 端點非 200」—— 讀起來像 IdP 掛了,不像路徑拼錯。
    /// OIDC token 端點。
    [JsonIgnore]
    public string? OidcTokenEndpoint => EndpointBase is null ? null : EndpointBase + "/token";
    /// OIDC 授權端點(建監考員登入 URL 用)。
    [JsonIgnore]
    public string? OidcAuthorizeEndpoint => EndpointBase is null ? null : EndpointBase + "/auth";
    /// JWKS 端點(未內聯 OidcJwksJson 時啟動拉取用)。
    [JsonIgnore]
    public string? OidcJwksEndpoint => EndpointBase is null ? null : EndpointBase + "/jwks";
    /// userinfo 端點。★ **身分 claims 只在這裡拿得到**,id_token 裡沒有(見 <see cref="Identity.Userinfo"/>)。
    [JsonIgnore]
    public string? OidcUserinfoEndpoint => EndpointBase is null ? null : EndpointBase + "/me";
    /// RP-Initiated Logout 端點(貝塔通 P24:RP 退出連帶退掉 IdP 工作階段)。
    [JsonIgnore]
    public string? OidcEndSessionEndpoint => EndpointBase is null ? null : EndpointBase + "/session/end";

    // ---- M4·RBAC:監考員看板 OIDC 登入(wentian dashboard web client·取代靜態 adminToken·見 m4-identity-oidc.md §10)----
    /// 管理端鑑權模式:"token"(預設·靜態 adminToken·M1-M3 原樣) | "oidc"(僅 wentian 長老 OIDC 工作階段·R3 無權杖後門)。
    public string AdminAuthMode { get; init; } = "token";
    /// wentian dashboard web client_id(如 horus-dashboard)。AdminAuthMode=oidc 必配。
    public string? OidcDashboardClientId { get; init; }
    /// dashboard client_secret 明文(僅整合測試;生產用 Enc 或 env HORUS_OIDC_DASHBOARD_SECRET)。Server 持有,瀏覽器從不經手。
    public string? OidcDashboardClientSecret { get; init; }
    /// dashboard client_secret DPAPI 密文(與視覺/採集 secret 同機制)。
    public string? OidcDashboardClientSecretEnc { get; init; }
    /// dashboard 回呼 URI(須與 wentian 註冊的 OAUTH_HORUS_DASHBOARD_REDIRECT_URIS 一條精確一致,如 https://<服务器>/cb)。
    public string? OidcDashboardRedirectUri { get; init; }
    // ---- 探活權杖的專屬 `aud`(owner 2026-08-11 拍板) ----
    // ★★ **背景**:`/internal/revoke` 與 `GET /internal/health` 的權杖此前 `aud` **完全相同**
    //   (都是本機 client_id),於是契約裡「多個入站端點唯一的區分是 `aud`」對這一對**不成立**。
    //   owner 拍板由貝塔通給探活權杖一個專屬 `aud`,把那句話恢復成**真不變數**。
    // ★ **為什麼值得改,儘管現有三家 RP 恰好都安全**:漏做「權杖 `sub` == 訊息 `sub`」是**零症狀**的,
    //   把安全性押在「每一家 RP 都讀懂並正確實作了契約」上不划算 —— 成均與將來的 RP 還沒寫。
    /// 本機**是否期待收到貝塔通的撤權廣播與存活探活**。預設 **false**(貝塔通 **P101**)。
    ///
    /// ★★★ **P101 的判據不是「懶得開隧道」**:Horus Server 是**監考電腦上的區域網路筆記本**
    ///   (自簽 HTTPS、無公用網路網域),而 `/internal/revoke` 與 `/internal/health` 都要求
    ///   **貝塔通主動撥進來** —— ★ **出站 ≠ 可入站**:換 token / 取 userinfo / 拉 JWKS 全是出站,
    ///   一直好用;這兩條是入站,方向反了。**這是架構選擇,不是填個表。**
    ///
    /// ★★ **殘餘風險是有界的,而且這個界是被 P88 削出來的**:撤權後最長 **≤ absolute**
    ///   (本儲存庫 <see cref="OidcSessionMinutes"/> / <see cref="AdminSessionMinutes"/> = 6 小時,
    ///   而契約寫下時是 7 天)、**且再也登不進來**(授權階段就被拒)、且 Server 只在考試期線上。
    ///   ★ 換隧道能把 ≤6h 縮到分鐘級,代價是**給一臺裝著全場監考取證與截圖的機器常設一個公用網路入口**。
    ///   owner 2026-08-11 拍板不換。
    ///   ★★★ **順帶帶走一條通用教訓**:這個風險從 7 天縮到 6 小時是 P88 乾的,
    ///   **而沒人回頭重算過它** —— **口徑變過之後,掛在舊口徑上的風險評估要回頭重算**,
    ///   否則會為一個已經縮小十倍的風險付舊價錢。
    ///
    /// ★ **觸發條件**(P101 寫死):哪天真需要**分鐘級**撤權,**上拉取模型**(Horus 主動去取),
    ///   **不是開隧道** —— 拉取順著本機出站-only 的拓撲,隧道方向是逆的。
    ///   ⏳ 那個拉取端點**目前不存在**,要走得先在貝塔通側立項。
    ///
    /// ★ **置 false 不會關掉任何實作**:`/internal/revoke`、`/internal/health`、兩道防線與它們的迴歸
    ///   **全部保留**(走拉取模型那天要用同一套驗簽與處置)。它只改**預檢怎麼說話**。
    public bool BetapassRevokePushExpected { get; init; }

    /// 探活權杖 `aud` 在 client_id 之後的後綴。**必須與貝塔通簽發時用的字面值一致**。
    /// ★ 做成設定是因為對側尚未完成實作:字面值對不上時這是**改設定**而不是改程式碼。
    public string OidcHealthAudienceSuffix { get; init; } = "#health";

    /// 探活是否**額外接受**舊口徑(`aud` = client_id 本身)。預設 **false**(只認專屬 `aud`)。
    ///
    /// ★★ **2026-08-11 對側 P100**:其 `healthProbeAudience()` 發的是 `&lt;client_id&gt;#health`,
    ///   契約也明寫「`/internal/revoke` 只認裸 `client_id`、`/internal/health` **只認**
    ///   `&lt;client_id&gt;#health`」。預設因此從過渡期的**新舊都收**收窄成**只認專屬值** ——
    ///   留著舊值就等於讓「`aud` 攔不住撤權/探活這一對」的例外繼續存在,而消滅它正是 P100 的目的。
    ///   ★ **注意口徑**:2026-08-11 核即時,貝塔通與問天錄兩側的改動都還**只在工作樹裡、尚未提交**
    ///   (`git log -S"healthProbeAudience"` 為空),**owner 尚未 push/部署**。
    ///   決策本身是 owner 拍的、程式碼也寫好了,但「線上跑的是哪一版」是另一回事 —— 逃生口就是為它留的。
    ///
    /// ★ 這一項因此**降級為逃生口**:只有在「程式碼已更新、但線上跑的貝塔通還是 P100 之前的版本」
    ///   那段視窗裡才置 true。
    ///
    /// ★★★ **把這個預設值的相依鏈寫死在這裡,因為它跨儲存庫庫**:
    ///   「預設只認專屬值」**相依對側真的發專屬值**。那份改動 2026-08-11 時還只在對側工作樹裡 ——
    ///   owner 若始終沒推,本儲存庫的探活會**恆 401**。
    ///   ★ **而那件事的代價確實很小,理由本身是核過的、不是猜的**:對側
    ///   `git show HEAD:src/identity/health/probe.ts` 第 81 行是
    ///   `res.ok ? {online:true} : {online:true, error:...}` —— **非 2xx 也算線上**
    ///   (`5bb16b6`,已確認是其 HEAD 的祖先,不是工作樹)。所以恆 401 的後果只是
    ///   「在其後台顯示成**線上但 HTTP 401**」,**不會**掛起撤權重投。
    ///   ★★ **接手要複核就跑這兩條**,別讀檔案:
    ///     `git -C ../BetaPass show HEAD:src/identity/health/probe.ts | grep res.ok`
    ///     `git -C ../BetaPass log -S healthProbeAudience -- src/`
    ///   —— **`git log -1` 告訴你的是 HEAD,不是磁碟上是什麼**;並行工作階段下工作樹常年是髒的,
    ///   直接讀檔案幾乎必錯(本輪同一天兩個方向各錯了一次)。
    ///
    /// ★★★ **但這兩條命令回答的是「儲存庫裡是什麼」,而這個開關關心的是「線上跑的是什麼」。**
    ///   工作樹 → 提交 → **部署**,三層各錯一次都夠嗆,而**沒有任何 git 命令答得出第三層**。
    ///   ★ 所以真正的判據不在這兩條命令上,而在**跑起來的行為**:看
    ///   `GET /api/preflight` 的 `health_audience`(本機認哪些 `aud`)+ 貝塔通後台那條
    ///   「端點異常 / HTTP 401」。★★ **兩邊都正常 = 口徑對上了**;
    ///   本機認專屬值而對側後台一直報 401 = 線上貝塔通還是舊版,**這才是該開逃生口的時刻**。
    public bool OidcHealthAudienceAcceptLegacy { get; init; }

    /// 登出回跳位址(RP-Initiated Logout 的 `post_logout_redirect_uri`)。
    /// 留空則按 <see cref="OidcDashboardRedirectUri"/> **同源**推導 `/logout/done`。
    ///
    /// ★★ **必須在貝塔通後台的「登出回跳位址」一欄登記**,且與這裡**精確一致** ——
    ///   沒登記時上游會忽略它,使用者會停在貝塔通自己的「已退出」頁,回不到 Horus。
    ///   ★ 那**不算故障**:本機工作階段在跳走**之前**就已經清掉了(見 AdminOidcEndpoints 的
    ///   `/admin/logout`),使用者確實已經退出,只是少了一次回跳。
    public string? OidcPostLogoutRedirectUri { get; init; }
    /// 管理工作階段的 **absolute** 門(分鐘):監考員登入後憑證壽命。預設 **360(6 小時)**,同採集面。
    /// idle 與心跳兩道門走共用的 <see cref="SessionIdleMinutes"/> / <see cref="SessionHeartbeatMinutes"/>。
    public int AdminSessionMinutes { get; init; } = 360;

    [JsonIgnore]
    public bool DashboardOidcEnabled => string.Equals(AdminAuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

    /// 實際使用的登出回跳位址:顯式設定優先,否則按 dashboard 回呼**同源**推導 `/logout/done`。
    /// 兩者都取不到時回傳 null —— 那時登出仍然照走,只是不帶 `post_logout_redirect_uri`。
    [JsonIgnore]
    public string? PostLogoutRedirectUriEffective
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OidcPostLogoutRedirectUri)) return OidcPostLogoutRedirectUri;
            if (string.IsNullOrWhiteSpace(OidcDashboardRedirectUri)) return null;
            return Uri.TryCreate(OidcDashboardRedirectUri, UriKind.Absolute, out Uri? u)
                ? new Uri(u, "/logout/done").ToString() : null;
        }
    }

    // ---- HTTPS(遠端監考工作站 OIDC 回呼須 https;自簽證書啟動生成/載入)----
    /// 自簽證書 pfx 路徑(相對 DataDir)。留空則在 DataDir 下自動生成 horus-https.pfx。僅當 Urls 含 https 時生效。
    public string? HttpsCertPath { get; init; }
    /// 自簽證書 pfx 密碼(留空=無密碼)。
    public string? HttpsCertPassword { get; init; }
    /// 自簽證書額外 SAN 主機/IP(逗號分隔),如伺服器 LAN IP / 主機名(localhost/127.0.0.1 自動含)。
    public string? HttpsSanHosts { get; init; }

    // ---- 視覺分析(L2:視覺 LLM 取代 OCR + L3 Logo,合併單一視覺級)----
    /// 視覺分析器:留空/"off" = 關(預設) | "mock"(確定性·測試整合測試) | "openai"(OpenAI 相容端點)。
    public string? VisionProvider { get; init; }
    /// OpenAI 相容視覺端點基址(DeepSeek-V4 / MiMo-V2.5 / Qwen-VL / GLM-4V 通用)。provider="openai" 時用。
    public string? VisionBaseUrl { get; init; }
    /// 視覺模型名(如 deepseek-v4-pro / MiMo-V2.5)。
    public string? VisionModel { get; init; }
    /// 視覺端點 API key **明文**(僅整合測試;生產請用 visionApiKeyEnc 加密儲存)。env HORUS_VISION_KEY 覆蓋。
    public string? VisionApiKey { get; init; }
    /// 視覺端點 API key **DPAPI 密文**(base64·設定檔案不存明文)。在部署機上跑 `protect-secret` 生成。見 SecretProtect。
    public string? VisionApiKeyEnc { get; init; }
    /// 視覺判定入可疑佇列的信賴度閾值(預設 60)。
    public int VisionConfidenceThreshold { get; init; } = 60;
    /// 是否也分析隨機基線圖(預設 false = 只分析觸發型,§5 最小化上傳/成本)。
    public bool VisionAnalyzeBaseline { get; init; }
    /// 基線分析抽樣率 N:`VisionAnalyzeBaseline=true` 時只分析 **1/N** 的隨機基線圖(確定性按 imageId 抽樣·控雲成本)。
    /// 預設 1 = 全分析(保持既有語義);設 >1 抽樣(如 10 ≈ 分析 10%),其餘基線標 analysis_state=1 終結、補償重掃不再拾回。
    public int VisionBaselineSampleRate { get; init; } = 1;
    /// 補償重掃間隔(分鐘):週期性拾回 analysis_state=0 的觸發型證據圖(被佇列丟棄 / 伺服器重新啟動丟記憶體佇列 / 臨時雲失敗的)。預設 5;≤0=關閉。
    public double VisionBackstopMinutes { get; init; } = 5;
    /// 單張圖視覺分析的最大認領次數(含失敗):臨時雲失敗由補償重掃重試,達此上限則放棄(防端點持續失敗時死循環重掃)。預設 5。
    public int VisionMaxAttempts { get; init; } = 5;
    /// 視覺分析並行度(單 reader 之外:一次批量拉取 N 張並行送視覺端點,提速單場大批量)。預設 1 = 串行、不壓端點;上限 8。
    public int VisionConcurrency { get; init; } = 1;

    // ---- §5 送雲前的派生處理(owner 決策 2026-07-02:不再打碼/裁剪,只降取樣;供應商=境內雲 MiMo·PIPL 無跨境)----
    /// 送雲圖長邊像素上限(降取樣·省 token/少送無關像素·順帶剝離中繼資料)。預設 1600;0=不降取樣直通。
    public int VisionMaxEdge { get; init; } = 1600;

    // ---- M3 CLIP 按圖搜圖(provider-agnostic 嵌入器·C# 暴力餘弦·無需 sqlite-vec·僅嵌證據/可疑圖)----
    /// 圖像嵌入器:留空/"off"=關(預設) | "mock"(確定性·測試整合測試) | "openai"(OpenAI 相容 /v1/embeddings)。
    public string? EmbedProvider { get; init; }
    /// 嵌入端點基址;**留空則重複使用 `VisionBaseUrl`**(KEY一致·同 provider)。/embeddings 從此拼。
    public string? EmbedBaseUrl { get; init; }
    /// 嵌入模型名(供應商的圖像/多模態 embedding 模型)。provider=openai 必配。
    public string? EmbedModel { get; init; }
    /// 嵌入 API key 明文;**留空則重複使用視覺 key**(KEY一致)。env HORUS_EMBED_KEY 覆蓋。
    public string? EmbedApiKey { get; init; }
    /// 嵌入 API key DPAPI 密文(同視覺機制)。
    public string? EmbedApiKeyEnc { get; init; }
    /// 嵌入維度(暴力餘弦無所謂維度·僅記錄/檢核)。預設 512(CLIP ViT-B/32)。
    public int EmbedDim { get; init; } = 512;
    /// 後台嵌入補掃間隔(分鐘):拾回證據/可疑圖裡尚無 embedding 的。預設 5;≤0=關閉。
    public double EmbedBackstopMinutes { get; init; } = 5;
    /// 按圖搜圖餘弦下限:低於此分的影格視為無關(CLIP 餘弦對不相關圖常 ≈0),過濾掉避免噪聲(B1)。
    /// 預設 0.2(CLIP 經驗值);設為 ≤0 則不過濾(退回舊行為)。
    public double EmbedCosineFloor { get; init; } = 0.2;
    /// 本機 ONNX CLIP 圖像編碼器模型路徑。**留空 = 約定名 `model.onnx`(在 DataDir 下)**——與 HF 倉
    /// Qdrant/clip-ViT-B-32-vision 原始檔案名一致,下載後免改名;也可顯式指定相對(按 DataDir)或絕對路徑。
    public string? EmbedOnnxModelPath { get; init; }
    /// ONNX 輸入張量名(留空=模型第一個輸入·CLIP 常為 pixel_values)。
    public string? EmbedOnnxInput { get; init; }
    /// ONNX 輸出張量名(留空=模型第一個輸出·常為 image_embeds / output)。
    public string? EmbedOnnxOutput { get; init; }

    [JsonIgnore]
    public bool EmbedEnabled => !string.IsNullOrWhiteSpace(EmbedProvider)
                                && !string.Equals(EmbedProvider, "off", StringComparison.OrdinalIgnoreCase);
    /// 嵌入端點基址(留空重複使用視覺)。
    [JsonIgnore]
    public string? EmbedBaseUrlEffective => string.IsNullOrWhiteSpace(EmbedBaseUrl) ? VisionBaseUrl : EmbedBaseUrl;
    /// OpenAI 相容 embeddings 端點(基址去尾斜槓 + /embeddings)。
    [JsonIgnore]
    public string? EmbedEmbeddingsEndpoint => string.IsNullOrEmpty(EmbedBaseUrlEffective) ? null : EmbedBaseUrlEffective!.TrimEnd('/') + "/embeddings";

    /// 事件風險分 ≥ 此值 → 入可疑佇列。預設 50(見 architecture §16)。
    public int RiskThreshold { get; init; } = 50;

    // ---- M3 歸檔 / 清理(architecture §13/§15)----
    /// 是否啟用後台歸檔作業(預設 true)。:memory: 或無到齡考試時自然 no-op;測試可關閉後台、手動觸發 RunOnce。
    public bool ArchiveEnabled { get; init; } = true;
    /// 考試**結束**多少天后轉 archive 並清理 live。預設 30(§13/§16)。
    public int RetentionDays { get; init; } = 30;
    /// 歸檔"關鍵資料"判據:事件有效風險 ≥ 此值,或被 suspicious_queue 引用。預設 50(§16)。
    public int ArchiveCriticalRisk { get; init; } = 50;
    /// 歸檔庫 SQLite 檔案路徑(相對 DataDir)。預設 horus-archive.db。
    public string ArchiveDbPath { get; init; } = "horus-archive.db";
    /// 後台歸檔掃描間隔(小時)。預設 6;≤0 = 關閉後台自動掃描(仍可手動 / 測試觸發)。
    public double ArchiveScanIntervalHours { get; init; } = 6;

    /// 伺服器側 pHash 近重複判定:同座位相同 phash 視為重複,不另存原圖。M1 用精確相等。
    public bool DedupImagesByPhash { get; init; } = true;

    /// 心跳線上判定視窗(秒):最近一次心跳在此視窗內則座位線上。
    public int OnlineWindowSeconds { get; init; } = 90;

    /// 唯讀連接池大小(SQLite WAL 並行讀:看板輪詢 / 完整性稽核 / 歸檔 copy 互不串行)。預設 4。:memory: 模式忽略(退回寫連接)。
    public int ReadPoolSize { get; init; } = 4;

    /// "最近風險"統計視窗(秒):座位熱力取此視窗內事件的最大 risk。
    public int RecentRiskWindowSeconds { get; init; } = 300;

    [JsonIgnore]
    public byte[]? Psk => string.IsNullOrWhiteSpace(PskBase64) ? null : Convert.FromBase64String(PskBase64);

    [JsonIgnore]
    public byte[]? Ksk => string.IsNullOrWhiteSpace(KeystrokeSecretBase64) ? null : Convert.FromBase64String(KeystrokeSecretBase64);

    [JsonIgnore]
    public bool AuthEnabled => Psk is not null;

    [JsonIgnore]
    public bool KeystrokeAuthEnabled => Ksk is not null;

    [JsonIgnore]
    public bool AdminAuthEnabled => !string.IsNullOrEmpty(AdminToken) || DashboardOidcEnabled;

    [JsonIgnore]
    public bool VisionEnabled => !string.IsNullOrWhiteSpace(VisionProvider)
                                 && !string.Equals(VisionProvider, "off", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path)) return new ServerConfig();
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Opt) ?? new ServerConfig();
    }
}
