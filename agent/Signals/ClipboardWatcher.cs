using System.Runtime.InteropServices;
using System.Windows.Forms;
using Horus.Agent.Config;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 剪貼簿更新監聽。大段粘貼(文字長度/行數超閾值)→ 高風險 + 觸發抓圖。
/// 隱私:**預設只記中繼資料(長度/行數),不上傳剪貼簿明文**。
/// 實作:自帶一個 STA 執行緒 + message-only 隱藏視窗接收 WM_CLIPBOARDUPDATE(剪貼簿訪問要求 STA)。
public sealed class ClipboardWatcher : ISignalSource
{
    public string Name => "clipboard";
    public event Action<RawSignal>? Signal;

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly LiveConfig _live;                 // 大段粘貼閾值可熱更新
    private Thread? _thread;
    private MsgWindow? _win;

    public ClipboardWatcher(LiveConfig live) => _live = live;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            _win = new MsgWindow(OnClipboardUpdate);
            AddClipboardFormatListener(_win.Handle);
            Application.Run();   // 在本 STA 執行緒跑訊息泵
        })
        { IsBackground = true, Name = "horus-clipboard" };
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
            // 只需 len/lines 中繼資料(payload 不含明文)。超大文字(如整份紀錄/文件)不做全量逐字元行掃描,避免 CPU 尖峰:
            // 長度一旦達閾值即判 large,行數只在前 ScanCap 字元內統計(足夠觸發 lines>=5 判定)。
            const int ScanCap = 64 * 1024;
            int lines = (len <= ScanCap ? text.AsSpan() : text.AsSpan(0, ScanCap)).Count('\n') + 1;
            bool large = len >= _live.LargePasteThreshold || lines >= 5;

            Signal?.Invoke(new RawSignal(SignalType.Clipboard,
                new() { ["len"] = len, ["lines"] = lines, ["large"] = large },  // 不含明文
                Risk: large ? 60 : 0,
                TriggerCapture: large,
                CaptureReason: large ? "large_paste" : null));
        }
        catch { /* 剪貼簿被佔用,跳過本次 */ }
    }

    public void Stop()
    {
        if (_win is not null)
        {
            RemoveClipboardFormatListener(_win.Handle);
            // 乾淨關停 STA 訊息泵:向視窗 Post 自訂訊息,由 WndProc 在 STA 執行緒上調 Application.ExitThread 退出循環
            // (Application.ExitThread 必須在本執行緒呼叫;跨執行緒呼叫會拋異常)。避免相依處理程序退出回收(閉合第二節待辦)。
            PostMessage(_win.Handle, WmExit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WmExit = 0x0400 + 1;   // WM_USER+1:請求 STA 執行緒退出訊息泵

    /// message-only 隱藏視窗(parent = HWND_MESSAGE)
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
            else if (m.Msg == WmExit) Application.ExitThread();   // 在本 STA 執行緒上乾淨退出訊息泵
            else base.WndProc(ref m);
        }
    }
}
