using System.Text.Json;
using Honus.Contracts;

namespace Honus.Server.Analysis;

/// M1 服务器侧初筛:由事件类型 + payload 判定可疑类别(kind)。
/// 系统只初筛,处分由人裁决(architecture 铁律 §3)。风险分由 Agent 本地初判,
/// 服务器 risk ≥ 阈值即入 suspicious_queue;M2/M3 再叠加 OCR/Logo/击键。
public static class Suspicion
{
    // 网页 AI 站(命中即 web_ai)。非白名单浏览已由 Agent 判为高风险,这里只细分标签。
    private static readonly string[] AiHosts =
        { "openai", "chatgpt", "doubao", "deepseek", "gemini", "bard", "claude", "anthropic",
          "kimi", "moonshot", "tongyi", "qwen", "wenxin", "yiyan", "perplexity", "copilot", "poe", "you.com" };

    // 搜索引擎(命中即 search)。
    private static readonly string[] SearchHosts =
        { "google", "baidu", "bing", "sogou", "so.com", "360.cn", "duckduckgo", "yandex" };

    /// 返回 kind;返回 null 表示该事件不单独入队(例如低于阈值的软信号)。
    public static string KindFor(SignalType type, JsonElement payload)
    {
        switch (type)
        {
            case SignalType.BrowserUrl:
                if (TryStr(payload, "note", out string? note) && note == "url_unreadable")
                    return "browser_unreadable";
                if (TryStr(payload, "url", out string? url) && !string.IsNullOrEmpty(url))
                {
                    string host = HostOf(url);
                    if (MatchAny(host, AiHosts)) return "web_ai";
                    if (MatchAny(host, SearchHosts)) return "search";
                }
                return "web_ai";   // 判题考中任何非白名单浏览都归 web_ai 类可疑(M1 简化)
            case SignalType.ProcessStart: return "non_whitelist_proc";
            case SignalType.Clipboard:    return "large_paste";
            case SignalType.Usb:          return "usb";
            default:                      return "suspect";
        }
    }

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
}
