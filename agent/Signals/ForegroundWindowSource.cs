using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Honus.Agent.Model;
using Honus.Contracts;

namespace Honus.Agent.Signals;

/// 前台窗口标题 + 进程名。轮询(默认 1s)。
/// TODO: 可改用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 事件驱动以降开销(需消息循环)。
public sealed class ForegroundWindowSource : ISignalSource
{
    public string Name => "foreground-window";
    public event Action<RawSignal>? Signal;

    private readonly TimeSpan _interval;
    private readonly System.Threading.Timer _timer;   // 显式限定:UseWindowsForms 注入了 Forms.Timer 造成歧义
    private (IntPtr Hwnd, string Title) _last;

    public ForegroundWindowSource(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, _interval);
    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Poll()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        string title = GetTitle(hwnd);
        if (hwnd == _last.Hwnd && title == _last.Title) return;   // 无变化,不上报
        _last = (hwnd, title);

        Signal?.Invoke(new RawSignal(SignalType.WindowFocus, new()
        {
            ["title"] = title,
            ["process"] = GetProcessName(hwnd),
            ["hwnd"] = hwnd.ToInt64(),
        }));
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return string.Empty; }
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
