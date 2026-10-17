namespace Horus.Agent.Hardening;

/// M5 防掛起:採集循環每輪喂進當前 wall-clock(Unix 秒),若相鄰兩次跳變遠超期望間隔 → 判定處理程序曾被
/// suspend / 系統睡眠 / 鎖屏(Task.Delay 與處理程序一同被掛起,恢復後 gap = 間隔 + 掛起時長)。純邏輯·可單測。
public sealed class SuspendMonitor
{
    private readonly double _expectedIntervalSec;
    private readonly double _toleranceFactor;
    private double _lastSec;
    private bool _primed;

    /// expectedIntervalSec:喂入節奏(如心跳 30s);toleranceFactor:超過 期望×此 才算掛起(預設 3×,躲開正常抖動)。
    public SuspendMonitor(double expectedIntervalSec, double toleranceFactor = 3.0)
    {
        _expectedIntervalSec = expectedIntervalSec <= 0 ? 1 : expectedIntervalSec;
        _toleranceFactor = toleranceFactor < 1 ? 1 : toleranceFactor;
    }

    /// 觀測一次 wall-clock;若檢出掛起回傳 gap 毫秒,否則 null。首次呼叫只定基線。
    public double? Observe(double nowSec)
    {
        if (!_primed) { _primed = true; _lastSec = nowSec; return null; }
        double gap = nowSec - _lastSec;
        _lastSec = nowSec;
        double threshold = _expectedIntervalSec * _toleranceFactor;
        return gap > threshold ? gap * 1000.0 : (double?)null;
    }

    public double ThresholdMs => _expectedIntervalSec * _toleranceFactor * 1000.0;
}

/// 採集能力健康度:各信號源(admin / etw / uia / wmi)連續失敗達閾值才判"降級"(防單次瞬時失敗抖動誤報)。純邏輯·可單測。
public sealed class CapabilityTracker
{
    private readonly int _threshold;
    private readonly Dictionary<string, int> _consecFails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _degraded = new(StringComparer.Ordinal);

    public CapabilityTracker(int consecutiveFailThreshold = 3)
        => _threshold = consecutiveFailThreshold < 1 ? 1 : consecutiveFailThreshold;

    /// 記一次成功:清零計數;若此前降級則回傳 true(表示"恢復",呼叫方可上報恢復)。
    public bool RecordSuccess(string capability)
    {
        _consecFails[capability] = 0;
        return _degraded.Remove(capability);
    }

    /// 記一次失敗:連續失敗達閾值且此前未降級 → 回傳 true(表示"剛跨入降級",呼叫方上報一次 capability_degraded)。
    public bool RecordFailure(string capability)
    {
        int n = _consecFails.TryGetValue(capability, out int v) ? v + 1 : 1;
        _consecFails[capability] = n;
        if (n >= _threshold && _degraded.Add(capability)) return true;   // 剛跨入降級(Add 回傳 true 表示原不在集合)
        return false;
    }

    public bool IsDegraded(string capability) => _degraded.Contains(capability);
    public IReadOnlyCollection<string> Degraded => _degraded;
}

/// M5 保活取證:判定本次啟動是否"異常重新啟動"(上次處理程序被結束而非正常退出)。
/// 約定:啟動時寫標記 "running";正常退出寫 "clean"。啟動讀到舊標記仍是 "running" → 上次異常終止(被殺 / 崩潰 / 斷電)。
/// 純邏輯(FS 讀寫由 Agent 做),此處只判定。
public static class RestartClassifier
{
    public const string Running = "running";
    public const string Clean = "clean";

    /// prevMarker = 啟動時讀到的舊標記內容(無檔案傳 null)。回傳是否異常重新啟動。
    /// 首次部署(無標記)不算異常。
    public static bool IsUnexpectedRestart(string? prevMarker)
        => string.Equals(prevMarker?.Trim(), Running, StringComparison.Ordinal);
}
