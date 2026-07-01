using System.Runtime.InteropServices;
using System.Windows.Forms;
using Honus.Agent.Model;
using Honus.Contracts;

namespace Honus.Agent.Signals;

/// 剪贴板更新监听。大段粘贴(文本长度/行数超阈值)→ 高风险 + 触发抓图。
/// 隐私:**默认只记元数据(长度/行数),不上传剪贴板明文**。
/// 实现:自带一个 STA 线程 + message-only 隐藏窗口接收 WM_CLIPBOARDUPDATE(剪贴板访问要求 STA)。
public sealed class ClipboardWatcher : ISignalSource
{
    public string Name => "clipboard";
    public event Action<RawSignal>? Signal;

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly int _largePasteThreshold;
    private Thread? _thread;
    private MsgWindow? _win;

    public ClipboardWatcher(int largePasteThreshold = 200) => _largePasteThreshold = largePasteThreshold;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            _win = new MsgWindow(OnClipboardUpdate);
            AddClipboardFormatListener(_win.Handle);
            Application.Run();   // 在本 STA 线程跑消息泵
        })
        { IsBackground = true, Name = "honus-clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void OnClipboardUpdate()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            int len = text.Length;
            int lines = text.Count(c => c == '\n') + 1;
            bool large = len >= _largePasteThreshold || lines >= 5;

            Signal?.Invoke(new RawSignal(SignalType.Clipboard,
                new() { ["len"] = len, ["lines"] = lines, ["large"] = large },  // 不含明文
                Risk: large ? 60 : 0,
                TriggerCapture: large,
                CaptureReason: large ? "large_paste" : null));
        }
        catch { /* 剪贴板被占用,跳过本次 */ }
    }

    public void Stop()
    {
        if (_win is not null) RemoveClipboardFormatListener(_win.Handle);
        // TODO: 干净关停消息泵(在 _thread 上 Application.ExitThread())。M1 依赖进程退出回收。
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// message-only 隐藏窗口(parent = HWND_MESSAGE)
    private sealed class MsgWindow : NativeWindow
    {
        private readonly Action _onUpdate;
        public MsgWindow(Action onUpdate)
        {
            _onUpdate = onUpdate;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });   // HWND_MESSAGE
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE) _onUpdate();
            base.WndProc(ref m);
        }
    }
}
