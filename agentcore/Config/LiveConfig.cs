using System.Text.Json;

namespace Horus.Agent.Config;

/// 執行環境可熱更新的設定(白名單 / 閾值 / 截圖參數)。信號源與抓屏器持有它、每次讀取,
/// 伺服器下發 config_update 時 Apply(json) 原子替換,下一輪採集即生效。執行緒安全:
/// 引用型欄位整體替換(不就地改集合),int 欄位 volatile。
public sealed class LiveConfig
{
    private volatile HashSet<string> _hosts;
    private volatile HashSet<string> _procs;
    private volatile int _largePaste;
    private volatile int _targetHeight;
    private volatile int _webpQuality;
    private volatile int _baselineMin;
    private volatile int _baselineMax;

    public LiveConfig(AgentConfig cfg)
    {
        _hosts = new(cfg.WhitelistHosts.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        _procs = new(cfg.WhitelistProcs.Select(p => p.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        _largePaste = cfg.LargePasteThreshold;
        _targetHeight = cfg.TargetHeight;
        _webpQuality = cfg.WebpQuality;
        _baselineMin = cfg.BaselineMinSeconds;
        _baselineMax = cfg.BaselineMaxSeconds;
    }

    public bool IsWhitelistedHost(string host) => _hosts.Contains(host);
    public bool IsWhitelistedProc(string procNoExt) => _procs.Contains(procNoExt);
    public int LargePasteThreshold => _largePaste;
    public int TargetHeight => _targetHeight;
    public int WebpQuality => _webpQuality;
    public int BaselineMinSeconds => _baselineMin;
    public int BaselineMaxSeconds => _baselineMax;

    /// 應用伺服器下發的設定(camelCase);僅更新出現的欄位,並做基本約束保護。
    public void Apply(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return;

        if (TryArray(c, "whitelistHosts", out List<string> hosts))
            _hosts = new(hosts, StringComparer.OrdinalIgnoreCase);
        if (TryArray(c, "whitelistProcs", out List<string> procs))
            _procs = new(procs.Select(p => p.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        // 上下限鉗制:防止(可能來自被冒用的下發端)極端值造成資源風暴,如 targetHeight=10萬、baseline=1s。
        if (TryInt(c, "largePasteThreshold", out int lp)) _largePaste = Math.Clamp(lp, 1, 100_000);
        if (TryInt(c, "targetHeight", out int th)) _targetHeight = Math.Clamp(th, 240, 2160);
        if (TryInt(c, "webpQuality", out int wq)) _webpQuality = Math.Clamp(wq, 1, 100);

        // baseline 區間:只下發一端時不篡改另一端(以現值為界);兩端都下發且矛盾才交換。
        int newMin = _baselineMin, newMax = _baselineMax;
        bool minSent = TryInt(c, "baselineMinSeconds", out int bmin);
        bool maxSent = TryInt(c, "baselineMaxSeconds", out int bmax);
        if (minSent) newMin = Math.Clamp(bmin, 5, 3600);
        if (maxSent) newMax = Math.Clamp(bmax, 5, 3600);
        if (minSent && maxSent) { if (newMin > newMax) (newMin, newMax) = (newMax, newMin); }
        else if (minSent) newMin = Math.Min(newMin, newMax);   // 只下發 min:不超過現 max,不動 max
        else if (maxSent) newMax = Math.Max(newMax, newMin);   // 只下發 max:不低於現 min,不動 min
        _baselineMin = newMin;
        _baselineMax = newMax;
    }

    private static bool TryInt(JsonElement o, string key, out int v)
    {
        v = 0;
        return o.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    private static bool TryArray(JsonElement o, string key, out List<string> v)
    {
        v = new List<string>();
        if (!o.TryGetProperty(key, out JsonElement e) || e.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement item in e.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                string s = item.GetString()!.Trim();   // 去尾隨空格:防下發端設定不潔致精確匹配失效、判題站誤判非白名單
                if (s.Length > 0) v.Add(s);
            }
        return true;
    }
}
