using System.Text.Json;
using Horus.Contracts;

namespace Horus.Server.Analysis;

/// M1 伺服器側初篩:由事件類型 + payload 判定可疑類別(kind)。
/// 系統只初篩,處分由人裁決(architecture 鐵律 §3)。風險分由 Agent 本機初判,
/// 伺服器 risk ≥ 閾值即入 suspicious_queue;M2/M3 再疊加 OCR/Logo/擊鍵。
public static class Suspicion
{
    /* SignalType → kind → 前端標籤 總表（T4：新增信號時據此同步補 KIND_META，防漏標籤）
     * ScreenshotObscured   → screen_obscured     → 螢幕遮擋      (health)
     * CapabilityDegraded   → capability_degraded → 能力降級      (health)
     * WatchdogRestart      → watchdog_restart    → 看門狗重新啟動    (health)
     * SuspectedSuspend     → suspected_suspend    → 疑似掛起      (health)
     * BrowserUrl(ai)       → web_ai              → AI 網站       (suspicion)
     * BrowserUrl(search)   → search              → 搜題         (suspicion)
     * BrowserUrl(other)    → non_whitelist_web   → 非白名單網站  (suspicion)
     * BrowserUrl(unreadable)→ browser_unreadable → 瀏覽器不可讀  (suspicion)
     * ProcessStart         → non_whitelist_proc  → 非白名單處理程序  (suspicion)
     * Clipboard            → large_paste         → 大段粘貼      (suspicion)
     * Usb                  → usb                 → USB 設備      (suspicion)
     * (其它/備援)           → suspect             → 可疑         (suspicion)
     * 視覺判定(ide_plugin/remote_tool)經 VisionVerdict.Kind() 映射,同源歸 suspicion。
     */
    // 命中黑名單即細分標籤。黑名單與 RiskModel 共用同一份,避免風險判據與標籤判據漂移。
    private static string[] AiHosts => RiskModel.AiHosts;
    private static string[] SearchHosts => RiskModel.SearchHosts;

    /// 回傳 kind;回傳 null 表示該事件不單獨入隊(例如低於閾值的軟信號)。
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
                    // 與 RiskModel 共用按 DNS 標籤的匹配(避免 richardbard→bard 等子串誤標),判據與標籤不漂移。
                    if (RiskModel.HostMatchesAny(host, AiHosts)) return "web_ai";
                    if (RiskModel.HostMatchesAny(host, SearchHosts)) return "search";
                }
                return "non_whitelist_web";   // 非 AI/非搜索的非白名單站:中性標籤,別誤貼 web_ai
            case SignalType.ProcessStart: return "non_whitelist_proc";
            case SignalType.Clipboard:    return "large_paste";
            case SignalType.Usb:          return "usb";
            // M5 採集端硬化:入隊的健康信號給專屬 kind(看板可辨識)。
            case SignalType.ScreenshotObscured: return "screen_obscured";
            case SignalType.CapabilityDegraded: return "capability_degraded";
            case SignalType.WatchdogRestart:    return "watchdog_restart";
            case SignalType.SuspectedSuspend:   return "suspected_suspend";   // M8:統一經「採集健康」面板呈現
            default:                      return "suspect";
        }
    }

    /// 健康信號(採集端硬化三件套)入隊時歸屬 source='health',僅在「採集健康」面板呈現、不可裁決;
    /// 其餘作弊線索預設 source='suspicion'。判據與 KindFor 的三分支保持一致,避免漂移。
    public static string SourceForKind(string kind)
    {
        return kind is "screen_obscured" or "capability_degraded" or "watchdog_restart" or "suspected_suspend"
            ? "health" : "suspicion";
    }

    private static string HostOf(string url)
    {
        // 解析失敗回傳空串(與 RiskModel.HostOf 一致):不拿整條 URL 去撞黑名單標籤,免 path/query 假陽性(F5)。
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return ""; }
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
