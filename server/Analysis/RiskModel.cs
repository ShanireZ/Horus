using System.Text.Json;
using Horus.Contracts;

namespace Horus.Server.Analysis;

/// 服务器侧风险复判(L1 重算)。**不信任 Agent 自报的 risk** —— 从 type + payload 独立推导服务器风险,
/// 与 Agent 自报取 max 作为「有效风险」用于入队 / 看板。
///
/// 闭合 architecture §10.1 头号残留:持 PSK 的学员机即便把「访问 AI 站」事件签成 risk=0,
/// 服务器凭独立黑名单(AI 站 / 搜索引擎 / 远控工具)仍判高危、照样入队,并记录 agent_risk_understated
/// 作为篡改取证信号。判据只用服务器自持的黑名单 + 该考试**已下发**白名单,绝不采信 Agent 的 whitelisted 自报。
public static class RiskModel
{
    // 网页 AI 站(命中即高危,不受 Agent whitelisted 自报影响)。与 Suspicion 共用同一份,避免漂移。
    public static readonly string[] AiHosts =
        { "openai", "chatgpt", "doubao", "deepseek", "gemini", "bard", "claude", "anthropic",
          "kimi", "moonshot", "tongyi", "qwen", "wenxin", "yiyan", "perplexity", "copilot", "poe", "you.com" };

    // 搜索引擎(命中即中高危)。
    public static readonly string[] SearchHosts =
        { "google", "baidu", "bing", "sogou", "so.com", "360.cn", "duckduckgo", "yandex" };

    // 远程协助 / 远控工具进程(独立高危;改名进程需特征库维护——architecture §11)。
    public static readonly string[] RemoteToolProcs =
        { "teamviewer", "anydesk", "todesk", "sunlogin", "向日葵", "rustdesk", "gotomypc", "ammyy", "vnc", "radmin" };

    // 大段粘贴默认阈值(architecture §16):≥200 字符 或 ≥5 行。可被已下发配置的 largePasteThreshold 覆盖(字符数)。
    public const int DefaultPasteCharThreshold = 200;
    public const int PasteLineThreshold = 5;

    /// 从事件独立推导服务器风险(0–100)。whitelistHosts/Procs 来自该考试**已下发**配置(未下发则为 null,
    /// 此时对「非白名单」类不加码、退回靠 Agent 自报的 max —— 保守,不制造假阳性)。
    /// pasteCharThreshold:大段粘贴字符阈值(来自已下发配置或默认 200)。
    public static int Derive(SignalType type, JsonElement payload,
        IReadOnlySet<string>? whitelistHosts, IReadOnlySet<string>? whitelistProcs,
        int pasteCharThreshold = DefaultPasteCharThreshold)
    {
        switch (type)
        {
            case SignalType.BrowserUrl:
                if (StrEq(payload, "note", "url_unreadable")) return 40;   // 读不到 URL 的降级:强制人工看图
                if (TryStr(payload, "url", out string? url) && !string.IsNullOrEmpty(url))
                {
                    string host = HostOf(url!);
                    if (MatchAny(host, AiHosts)) return 80;                 // 独立判定:AI 站
                    if (MatchAny(host, SearchHosts)) return 70;             // 独立判定:搜索引擎
                    if (whitelistHosts is not null)
                        return HostWhitelisted(host, whitelistHosts) ? 0 : 80;   // 有白名单可判:非白名单站高危
                }
                return 0;   // 无 URL / 无白名单可判 → 服务器不加码,交由 max(agentRisk) 承接

            case SignalType.ProcessStart:
                string proc = ProcName(payload);
                if (proc.Length > 0 && MatchAny(proc, RemoteToolProcs)) return 70;   // 独立判定:远控工具
                if (whitelistProcs is not null && proc.Length > 0)
                    return whitelistProcs.Contains(proc) ? 0 : 70;
                return 0;

            case SignalType.Clipboard:
                // **不信任 Agent 的 `large` 自报**:从 payload 的 len/lines 独立复判(闭合"签 large=false 低估逃逸")。
                return IsLargePaste(payload, pasteCharThreshold) ? 60 : 0;

            case SignalType.Usb:         return 50;
            case SignalType.AltTabBurst: return 40;
            default:                     return 0;   // window_focus / process_exit / heartbeat / screenshot
        }
    }

    /// 从已下发配置**解析一次**得到的策略(白名单集合 + 大段粘贴阈值)。可被缓存以免每事件热路径重解析(见 AgentHub.GetPolicy)。
    public sealed record Policy(IReadOnlySet<string>? Hosts, IReadOnlySet<string>? Procs, int PasteThreshold);

    /// 无配置 / 无对应字段时的默认策略:白名单 null(不加码)、阈值默认 200。
    public static readonly Policy EmptyPolicy = new(null, null, DefaultPasteCharThreshold);

    /// 从该考试**已下发**配置 JSON 提取策略:白名单集合(不区分大小写) + 大段粘贴字符阈值。
    /// 无配置 / 无对应字段 → 白名单 null(不加码)、阈值取默认 200。
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
                string s = (it.GetString() ?? "").Trim();   // 去尾随空格:与 Agent LiveConfig 一致,防精确匹配因空白失配
                if (s.Length > 0) set.Add(lower ? s.ToLowerInvariant() : s);
            }
        return set;
    }

    // ---- 小工具 ----
    private static bool HostWhitelisted(string host, IReadOnlySet<string> wl)
        => wl.Contains(host);   // 与 Agent LiveConfig.IsWhitelistedHost 一致:精确匹配(子域视为非白名单)

    private static string ProcName(JsonElement payload)
    {
        // 事件 payload 里进程字段:process_start 用 "name",browser_url 用 "process"。取其一并去 .exe / 转小写。
        string? n = TryStr(payload, "name", out string? a) ? a : (TryStr(payload, "process", out string? b) ? b : null);
        if (string.IsNullOrEmpty(n)) return "";
        n = n!.ToLowerInvariant();
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n;
    }

    /// 服务器侧独立复判大段粘贴:凭 payload 的 len(字符数)/lines(行数)与阈值判定,**不读 Agent 的 `large`**。
    /// payload 不含粘贴明文(隐私设计),但 len/lines 是元数据、足以独立复算。
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
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return url.ToLowerInvariant(); }
    }

    private static bool MatchAny(string host, string[] needles)
    {
        foreach (string n in needles)
            if (host.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
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
