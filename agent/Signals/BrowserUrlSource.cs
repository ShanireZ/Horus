using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Honus.Agent.Model;
using Honus.Contracts;

namespace Honus.Agent.Signals;

/// 读取前台浏览器地址栏 URL —— 本系统对抗网页 AI 的第一防线。
/// 判题站域名白名单放行;出现非白名单 URL → 高风险 + 触发抓图。
/// 注意:UIAutomation 抓地址栏对浏览器版本/语言敏感;隐身/冷门浏览器可能读不到 →
///       降级为"浏览器进程级告警 + 强制抓图"(交人工看截图)。
public sealed class BrowserUrlSource : ISignalSource
{
    public string Name => "browser-url";
    public event Action<RawSignal>? Signal;

    private static readonly string[] BrowserProcs = { "chrome", "msedge", "firefox", "brave", "opera" };

    private readonly HashSet<string> _whitelistHosts;
    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;   // 显式限定:UseWindowsForms 注入了 Forms.Timer 造成歧义
    private string _lastUrl = "";

    public BrowserUrlSource(IEnumerable<string> whitelistHosts, TimeSpan? interval = null)
    {
        _whitelistHosts = new(whitelistHosts, StringComparer.OrdinalIgnoreCase);
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, _interval);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Poll()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out uint pid);
        string proc;
        try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { return; }
        if (Array.IndexOf(BrowserProcs, proc) < 0) return;   // 前台不是浏览器

        string? url = TryReadAddressBar(hwnd);

        if (string.IsNullOrEmpty(url))
        {
            // 是浏览器但读不到 URL → 降级告警 + 抓图
            Signal?.Invoke(new RawSignal(SignalType.BrowserUrl,
                new() { ["process"] = proc, ["url"] = null, ["note"] = "url_unreadable" },
                Risk: 40, TriggerCapture: true, CaptureReason: "browser_url_unreadable"));
            return;
        }

        if (url == _lastUrl) return;
        _lastUrl = url;

        bool whitelisted = IsWhitelisted(url);
        Signal?.Invoke(new RawSignal(SignalType.BrowserUrl,
            new() { ["process"] = proc, ["url"] = url, ["whitelisted"] = whitelisted },
            Risk: whitelisted ? 0 : 80,
            TriggerCapture: !whitelisted,
            CaptureReason: whitelisted ? null : "browser_non_whitelist_url"));
    }

    private bool IsWhitelisted(string url)
    {
        try { return _whitelistHosts.Contains(new Uri(url).Host); }
        catch { return false; }
    }

    /// 地址栏控件定位对每种浏览器不同,此处给通用思路骨架(取第一个像 URL 的 Edit)。
    /// TODO: 生产应按浏览器缓存 AutomationElement / 用更窄的条件(toolbar→edit)避免全树遍历的开销。
    private static string? TryReadAddressBar(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
            {
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
                {
                    string val = vp.Current.Value;
                    if (!string.IsNullOrWhiteSpace(val) &&
                        (val.StartsWith("http", StringComparison.OrdinalIgnoreCase) || val.Contains('.')))
                        return Normalize(val);
                }
            }
        }
        catch { /* UIA 偶发异常,跳过本次 */ }
        return null;
    }

    private static string Normalize(string raw)
        => raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "http://" + raw;

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
