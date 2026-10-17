using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Agent.Signals;

/// 採集機上的**使用者活動**判定 —— 三道門裡 idle 那道的 `active` 依據(貝塔通 P89)。
///
/// ★★ **採集端不適用網頁那套心跳規格**(其 rp-contract 專門為桌面用戶端寫了這一條):
///   `horus-client` 是原生桌面 exe,**沒有 `BroadcastChannel`、沒有 `document.visibilityState`**。
///   照抄瀏覽器那套的結果是一段永遠走不到的死程式碼,或者一個**恆為 `false` 的 `active`** ——
///   後者的表現是**每個學生考到 30 分鐘就被 idle 踢掉**,而且看起來像網路問題。
///
/// ★ **Agent 自己定義 `active`,而且它的信號更準**:它本來就在採集機器上有無使用者活動,
///   「這臺機器前面有沒有人」是它天然就知道的事實,不需要也不應該去模擬瀏覽器那套。
///   這裡用 Win32 `GetLastInputInfo` —— 全工作階段範圍的鍵鼠輸入,比任何單個視窗的事件都全。
///
/// ★ 這不是 <c>ISignalSource</c>:它不產生上報事件,只在心跳那一發裡回答一個布爾。
///   ★ **有意不把空閒時長做成上報欄位** —— 那會變成一條新的行為信號,而本類的職責只有工作階段續期。
[SupportedOSPlatform("windows")]
public static class UserActivity
{
    /// 「最近有過輸入」的視窗。與網頁側的 `active` 演算法同口徑(貝塔通 P89:最近 5 分鐘)。
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;   // 最後一次輸入的時刻,與 GetTickCount 同基準(自開機的毫秒數)
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// 距最後一次鍵鼠輸入多久。取不到時回傳 <see cref="TimeSpan.Zero"/>(見 <see cref="IsActive"/> 的取捨)。
    public static TimeSpan IdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
        // ★ 兩者都是 32 位、約 49.7 天迴繞。unchecked 的無符號減法在迴繞處仍給出正確差值,
        //   所以**不要**換成 TickCount64 去減 dwTime —— 那才會在迴繞後算出一個幾十天的空閒。
        uint now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - lii.dwTime));
    }

    /// 這臺機器前面有沒有人。
    ///
    /// ★ **取不到輸入資訊時按「有人」算**(fail-open)。判據:這道門擋的是「人走了但機器還開著」,
    ///   而 absolute 與心跳兩道門仍然照常備援;反過來 fail-closed 的代價是**某個 API 出岔子就把
    ///   全場學生踢出考試**。兩邊的錯法不對稱,所以這裡有意選寬的那邊。
    public static bool IsActive()
    {
        if (!OperatingSystem.IsWindows()) return true;   // 採集端只跑 Windows;非 Windows 不做判定
        try { return IdleTime() < Window; }
        catch (DllNotFoundException) { return true; }
        catch (EntryPointNotFoundException) { return true; }
    }
}
