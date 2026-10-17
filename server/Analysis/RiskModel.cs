using System.Text.Json;
using Horus.Contracts;

namespace Horus.Server.Analysis;

/// 伺服器側風險復判(L1 重算)。**不信任 Agent 自報的 risk** —— 從 type + payload 獨立推導伺服器風險,
/// 與 Agent 自報取 max 作為「有效風險」用於入隊 / 看板。
///
/// 閉合 architecture §10.1 頭號殘留:持 PSK 的學員機即便把「訪問 AI 站」事件簽成 risk=0,
/// 伺服器憑獨立黑名單(AI 站 / 搜尋引擎 / 遠控工具)仍判高危、照樣入隊,並記錄 agent_risk_understated
/// 作為篡改取證信號。判據只用伺服器自持的黑名單 + 該考試**已下發**白名單,絕不採信 Agent 的 whitelisted 自報。
public static class RiskModel
{
    // 網頁 AI 站(命中即高危,不受 Agent whitelisted 自報影響)。與 Suspicion 共用同一份,避免漂移。
    public static readonly string[] AiHosts =
        { "openai", "chatgpt", "doubao", "deepseek", "gemini", "bard", "claude", "anthropic",
          "kimi", "moonshot", "tongyi", "qwen", "wenxin", "yiyan", "perplexity", "copilot", "poe", "you.com" };

    // 搜尋引擎(命中即中高危)。
    public static readonly string[] SearchHosts =
        { "google", "baidu", "bing", "sogou", "so.com", "360.cn", "duckduckgo", "yandex" };

    // 遠程協助 / 遠控工具處理程序(獨立高危;改名處理程序需特徵庫維護——architecture §11)。
    public static readonly string[] RemoteToolProcs =
        { "teamviewer", "anydesk", "todesk", "sunlogin", "向日葵", "rustdesk", "gotomypc", "ammyy", "vnc", "radmin" };

    // 大段粘貼預設閾值(architecture §16):≥200 字元 或 ≥5 行。可被已下發設定的 largePasteThreshold 覆蓋(字元數)。
    public const int DefaultPasteCharThreshold = 200;
    public const int PasteLineThreshold = 5;

    /// 從事件獨立推導伺服器風險(0–100)。whitelistHosts/Procs 來自該考試**已下發**設定(未下發則為 null,
    /// 此時對「非白名單」類不加碼、退回靠 Agent 自報的 max —— 保守,不製造假陽性)。
    /// pasteCharThreshold:大段粘貼字元閾值(來自已下發設定或預設 200)。
    public static int Derive(SignalType type, JsonElement payload,
        IReadOnlySet<string>? whitelistHosts, IReadOnlySet<string>? whitelistProcs,
        int pasteCharThreshold = DefaultPasteCharThreshold)
    {
        switch (type)
        {
            case SignalType.BrowserUrl:
                if (StrEq(payload, "note", "url_unreadable")) return RiskScores.BrowserUrlUnreadable;   // 讀不到 URL 的降級:強制人工看圖
                if (TryStr(payload, "url", out string? url) && !string.IsNullOrEmpty(url))
                {
                    string host = HostOf(url!);
                    // 空 host = 瀏覽器內部頁(about:blank / data: / chrome://)或解析失敗 → 不加碼(F11:免把內部頁誤判高危;
                    // 真實 Agent 的內部頁本走 url_unreadable,此為伺服器側縱深)。
                    if (host.Length == 0) return 0;
                    if (HostMatchesAny(host, AiHosts)) return RiskScores.BrowserUrlAi;           // 獨立判定:AI 站
                    if (HostMatchesAny(host, SearchHosts)) return RiskScores.BrowserUrlSearch;    // 獨立判定:搜尋引擎
                    if (whitelistHosts is not null)
                        return HostWhitelisted(host, whitelistHosts) ? 0 : RiskScores.BrowserUrlNonWhitelist;   // 有白名單可判:非白名單站高危
                }
                return 0;   // 無 URL / 無白名單可判 → 伺服器不加碼,交由 max(agentRisk) 承接

            case SignalType.ProcessStart:
                string proc = ProcName(payload);
                if (proc.Length > 0 && MatchAny(proc, RemoteToolProcs)) return RiskScores.RemoteTool;   // 獨立判定:遠控工具
                if (whitelistProcs is not null && proc.Length > 0)
                    return whitelistProcs.Contains(proc) ? 0 : RiskScores.NonWhitelistProc;
                return 0;

            case SignalType.Clipboard:
                // **不信任 Agent 的 `large` 自報**:從 payload 的 len/lines 獨立復判(閉合"簽 large=false 低估逃逸")。
                return IsLargePaste(payload, pasteCharThreshold) ? RiskScores.LargePaste : 0;

            case SignalType.Usb:         return RiskScores.Usb;
            case SignalType.AltTabBurst: return RiskScores.AltTabBurst;

            // M5 採集端硬化:伺服器獨立賦分(不信 Agent 自報),讓規避暴露入隊人工複核。
            case SignalType.ScreenshotObscured: return RiskScores.ScreenObscured;   // 考中遮屏/黑影格 → 入可疑佇列
            case SignalType.CapabilityDegraded: return RiskScores.CapabilityDegraded;   // 採集能力被削弱(非管理員/信號源失敗)→ 入隊
            case SignalType.WatchdogRestart:    return RiskScores.WatchdogRestart;    // 採集處理程序曾被結束(規避取證)→ 入隊
            // suspected_suspend:風險 0,不計入作弊裁決;經「採集健康」面板呈現(與另三個 M5 信號同源),睡眠/鎖屏等合法情形也提示監考員關注。

            default:                     return 0;   // window_focus / process_exit / heartbeat / screenshot / suspected_suspend
        }
    }

    /// 從已下發設定**解析一次**得到的策略(白名單集合 + 大段粘貼閾值)。可被快取以免每事件熱路徑重解析(見 AgentHub.GetPolicy)。
    public sealed record Policy(IReadOnlySet<string>? Hosts, IReadOnlySet<string>? Procs, int PasteThreshold);

    /// 無設定 / 無對應欄位時的預設策略:白名單 null(不加碼)、閾值預設 200。
    public static readonly Policy EmptyPolicy = new(null, null, DefaultPasteCharThreshold);

    /// 從該考試**已下發**設定 JSON 提取策略:白名單集合(不區分大小寫) + 大段粘貼字元閾值。
    /// 無設定 / 無對應欄位 → 白名單 null(不加碼)、閾值取預設 200。
    public static Policy PolicyFrom(string? configJson)
    {
        if (string.IsNullOrEmpty(configJson)) return EmptyPolicy;
        try
        {
            using JsonDocument d = JsonDocument.Parse(configJson);
            int th = d.RootElement.ValueKind == JsonValueKind.Object &&
                     d.RootElement.TryGetProperty("largePasteThreshold", out JsonElement lt) &&
                     lt.ValueKind == JsonValueKind.Number && lt.TryGetInt32(out int v) && v > 0
                ? v : DefaultPasteCharThreshold;
            return new Policy(Arr(d.RootElement, "whitelistHosts"), Arr(d.RootElement, "whitelistProcs", lower: true), th);
        }
        catch { return EmptyPolicy; }
    }

    private static IReadOnlySet<string>? Arr(JsonElement o, string key, bool lower = false)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.Array)
            return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement it in e.EnumerateArray())
            if (it.ValueKind == JsonValueKind.String)
            {
                string s = (it.GetString() ?? "").Trim();   // 去尾隨空格:與 Agent LiveConfig 一致,防精確匹配因空白失配
                if (s.Length > 0) set.Add(lower ? s.ToLowerInvariant() : s);
            }
        return set;
    }

    // ---- 小工具 ----
    private static bool HostWhitelisted(string host, IReadOnlySet<string> wl)
        => wl.Contains(host);   // 與 Agent LiveConfig.IsWhitelistedHost 一致:精確匹配(子域視為非白名單)

    private static string ProcName(JsonElement payload)
    {
        // 事件 payload 裡處理程序欄位:process_start 用 "name",browser_url 用 "process"。取其一併去 .exe / 轉小寫。
        string? n = TryStr(payload, "name", out string? a) ? a : (TryStr(payload, "process", out string? b) ? b : null);
        if (string.IsNullOrEmpty(n)) return "";
        n = n!.ToLowerInvariant();
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n;
    }

    /// 伺服器側獨立復判大段粘貼:憑 payload 的 len(字元數)/lines(行數)與閾值判定,**不讀 Agent 的 `large`**。
    /// payload 不含粘貼明文(隱私設計),但 len/lines 是中繼資料、足以獨立復算。
    private static bool IsLargePaste(JsonElement payload, int charThreshold)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        int len = IntOf(payload, "len");
        int lines = IntOf(payload, "lines");
        return len >= charThreshold || lines >= PasteLineThreshold;
    }

    private static int IntOf(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : 0;

    private static string HostOf(string url)
    {
        // 解析失敗回傳**空串**(不再退回回傳整條 URL)—— 否則子串匹配會在 path/query 上跑,
        // 把 "example.com/search?q=google" 誤判搜尋頁(閉合第三輪 F5 的整串匹配假陽性放大器)。
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
    }

    /// host 是否命中某黑名單標籤集。**按 DNS 標籤匹配**以杜絕子串誤傷(richardbard≠bard·foryou.com≠you.com):
    ///   • 含 '.' 的 needle(you.com / so.com / 360.cn)按**域後綴**:host==needle 或 host.EndsWith("."+needle);
    ///   • 裸品牌詞 needle(openai / claude / bard)匹配 host 的**某一整段標籤**(chat.openai.com 命中 openai;richardbard.com 不命中)。
    /// 取捨:連字元品牌鏡像(openai-mirror.cn)會漏,交白名單備援(非白名單本就高危)+ 人工裁決,換取顯著更低的假陽性(架構鐵律 §3)。
    /// needles 均為小寫;host 已在 HostOf 小寫化。RiskModel 與 Suspicion 共用此匹配,避免判據與標籤漂移。
    public static bool HostMatchesAny(string host, string[] needles)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string[] labels = host.Split('.');
        foreach (string n in needles)
        {
            if (n.Contains('.'))
            {
                if (host == n || host.EndsWith("." + n, StringComparison.Ordinal)) return true;
            }
            else
            {
                foreach (string lbl in labels)
                    if (lbl == n) return true;
            }
        }
        return false;
    }

    /// 處理程序名匹配:**保留子串匹配**(與 host 不同)——遠控工具改名(teamviewer2 / anydesk_x)應仍命中特徵詞(architecture §11)。
    private static bool MatchAny(string name, string[] needles)
    {
        foreach (string n in needles)
            if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TryStr(JsonElement obj, string prop, out string? val)
    {
        val = null;
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String)
        {
            val = e.GetString();
            return true;
        }
        return false;
    }

    private static bool StrEq(JsonElement obj, string prop, string expected)
        => TryStr(obj, prop, out string? v) && v == expected;
}
