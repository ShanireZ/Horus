using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Horus.Agent.Config;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 讀取前台瀏覽器位址欄 URL —— 本系統對抗網頁 AI 的第一防線。
/// 判題站網域白名單放行;出現非白名單 URL → 高風險 + 觸發抓圖。
/// 注意:UIAutomation 抓位址欄對瀏覽器版本/語言敏感;隱身/冷門瀏覽器可能讀不到 →
///       降級為"瀏覽器處理程序級告警 + 強制抓圖"(交人工看截圖)。
public sealed class BrowserUrlSource : ISignalSource
{
    public string Name => "browser-url";
    public event Action<RawSignal>? Signal;

    private static readonly string[] BrowserProcs = { "chrome", "msedge", "firefox", "brave", "opera" };
    private const string UnreadableSentinel = "\0url_unreadable\0";   // _lastUrl 的"不可讀"哨兵,用於去重

    private readonly LiveConfig _live;                 // 白名單可熱更新
    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;   // 顯式限定:UseWindowsForms 注入了 Forms.Timer 造成歧義
    private string _lastUrl = "";

    // 位址欄元素快取(BUG#3 加固):同前台視窗(hwnd)重複使用已定位的位址欄 AutomationElement,
    // 跳過每次全樹 UIA 遍歷,降低 CPU 開銷與讀錯概率;視窗切換/快取失效才重新探測。
    private IntPtr _cachedHwnd;
    private AutomationElement? _addressBarCache;

    public BrowserUrlSource(LiveConfig live, TimeSpan? interval = null)
    {
        _live = live;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, _interval);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Poll()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) { InvalidateCache(); return; }

        GetWindowThreadProcessId(hwnd, out uint pid);
        string proc;
        try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { InvalidateCache(); return; }
        if (Array.IndexOf(BrowserProcs, proc) < 0) { InvalidateCache(); return; }   // 前台不是瀏覽器

        string? url = TryReadAddressBar(hwnd);

        if (string.IsNullOrEmpty(url))
        {
            // 是瀏覽器但讀不到 URL → 降級告警 + 抓圖。**只在進入"不可讀"狀態時發一次**,
            // 否則每 poll(約 2s)都發會刷爆 events + 可疑佇列;持續期由隨機基線抓圖覆蓋。
            if (_lastUrl == UnreadableSentinel) return;
            _lastUrl = UnreadableSentinel;
            Signal?.Invoke(new RawSignal(SignalType.BrowserUrl,
                new() { ["process"] = proc, ["url"] = null, ["note"] = "url_unreadable" },
                Risk: RiskScores.BrowserUrlUnreadable, TriggerCapture: true, CaptureReason: "browser_url_unreadable"));
            return;
        }

        if (url == _lastUrl) return;
        _lastUrl = url;

        bool whitelisted = IsWhitelisted(url);
        Signal?.Invoke(new RawSignal(SignalType.BrowserUrl,
            new() { ["process"] = proc, ["url"] = url, ["whitelisted"] = whitelisted },
            Risk: whitelisted ? 0 : RiskScores.BrowserUrlNonWhitelist,
            TriggerCapture: !whitelisted,
            CaptureReason: whitelisted ? null : "browser_non_whitelist_url"));
    }

    private bool IsWhitelisted(string url)
    {
        try { return _live.IsWhitelistedHost(new Uri(url).Host); }
        catch { return false; }
    }

    /// 清空位址欄快取(前台視窗切換 / 處理程序非瀏覽器 / 讀取失敗時)。
    private void InvalidateCache()
    {
        _cachedHwnd = IntPtr.Zero;
        _addressBarCache = null;
    }

    /// 位址欄定位加固(BUG#3):同 hwnd 重複使用快取元素,跳過全樹遍歷;快取失效才按「toolbar→edit」窄條件重探,
    /// 失敗再放寬到全樹(保持原行為備援),仍讀不到則清空快取下次重探。
    private string? TryReadAddressBar(IntPtr hwnd)
    {
        // 1) 重複使用快取:同前台視窗且快取有效 → 直接讀值(廉價,免 UIA 遍歷)。
        if (_cachedHwnd == hwnd && _addressBarCache is not null)
        {
            string? v = ReadValue(_addressBarCache);
            if (v is not null) return v;
            // 快取元素已失效(視窗結構變)→ 下方重探。
        }

        // 2) 探測:先窄後寬。
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) { InvalidateCache(); return null; }

            AutomationElement? addr = FindAddressBar(root);
            if (addr is not null)
            {
                _cachedHwnd = hwnd;
                _addressBarCache = addr;
                return ReadValue(addr);
            }
        }
        catch { /* UIA 偶發異常,跳過本次 */ }
        InvalidateCache();
        return null;
    }

    /// 先在前台視窗的 toolbar 容器後代裡找 Edit(位址欄通常位於工具欄內),命中即回傳;
    /// 找不到(冷門/隱身結構)再退化為全樹 Edit 遍歷(保持原行為 · 備援)。兩路都收窄到「像 URL」的 Edit,
    /// 避免把數值輸入框等誤當位址欄。
    private static AutomationElement? FindAddressBar(AutomationElement root)
    {
        var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        var toolbarCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar);
        foreach (AutomationElement tb in root.FindAll(TreeScope.Descendants, toolbarCond))
        {
            foreach (AutomationElement e in tb.FindAll(TreeScope.Descendants, editCond))
                if (LooksLikeUrl(e)) return e;
        }
        // 退化:全樹(原邏輯),並收窄到「像 URL」的 Edit。
        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, editCond))
            if (LooksLikeUrl(e)) return e;
        return null;
    }

    /// 控件值「像 URL」:非空、且以 http 開頭或含 '.'。(與舊邏輯一致,集中於此便於單測/重複使用)
    private static bool LooksLikeUrl(AutomationElement e)
    {
        string? v = ReadValue(e);
        return v is not null && (v.StartsWith("http", StringComparison.OrdinalIgnoreCase) || v.Contains('.'));
    }

    private static string? ReadValue(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
        {
            string val = vp.Current.Value;
            if (!string.IsNullOrWhiteSpace(val)) return Normalize(val);
        }
        return null;
    }

    private static string Normalize(string raw)
        => raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "http://" + raw;

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
