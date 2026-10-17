using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 前台視窗標題 + 處理程序名。**事件驅動**(SetWinEventHook EVENT_SYSTEM_FOREGROUND),無輪詢、零週期開銷:
/// 僅在前台視窗切換時觸發回呼,標題/處理程序在回呼裡讀取並上報。相比每秒 Timer 輪詢,CPU 與延遲都更優(第二節待辦閉環)。
public sealed class ForegroundWindowSource : ISignalSource
{
    public string Name => "foreground-window";
    public event Action<RawSignal>? Signal;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int WM_QUIT = 0x0012;

    private readonly WinEventDelegate _hookDelegate;   // 必須持有引用,否則被 GC 回收後回呼失效
    private IntPtr _hook;
    private int _threadId;
    private Thread? _thread;

    public ForegroundWindowSource() => _hookDelegate = OnWinEvent;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            Application.Run();   // 訊息泵:WinEventProc 在本 STA 執行緒呼叫
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        })
        { IsBackground = true, Name = "horus-foreground" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        int tid = _threadId;
        if (tid != 0) PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);   // 退出訊息泵(僅 STA 執行緒可安全退出)
    }

    public void Dispose()
    {
        Stop();
        try { _thread?.Join(TimeSpan.FromSeconds(1)); } catch { /* 超時忽略 */ }
    }

    private void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;   // 只關心真實前台視窗物件
        Signal?.Invoke(new RawSignal(SignalType.WindowFocus, new()
        {
            ["title"] = GetTitle(hwnd),
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

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate lpfn, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
}
