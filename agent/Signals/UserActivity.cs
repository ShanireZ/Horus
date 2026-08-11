using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Agent.Signals;

/// 采集机上的**用户活动**判定 —— 三道门里 idle 那道的 `active` 依据(贝塔通 P89)。
///
/// ★★ **采集端不适用网页那套心跳规格**(其 rp-contract 专门为桌面客户端写了这一条):
///   `horus-client` 是原生桌面 exe,**没有 `BroadcastChannel`、没有 `document.visibilityState`**。
///   照抄浏览器那套的结果是一段永远走不到的死代码,或者一个**恒为 `false` 的 `active`** ——
///   后者的表现是**每个学生考到 30 分钟就被 idle 踢掉**,而且看起来像网络问题。
///
/// ★ **Agent 自己定义 `active`,而且它的信号更准**:它本来就在采集机器上有无用户活动,
///   「这台机器前面有没有人」是它天然就知道的事实,不需要也不应该去模拟浏览器那套。
///   这里用 Win32 `GetLastInputInfo` —— 全会话范围的键鼠输入,比任何单个窗口的事件都全。
///
/// ★ 这不是 <c>ISignalSource</c>:它不产生上报事件,只在心跳那一发里回答一个布尔。
///   ★ **有意不把空闲时长做成上报字段** —— 那会变成一条新的行为信号,而本类的职责只有会话续期。
[SupportedOSPlatform("windows")]
public static class UserActivity
{
    /// 「最近有过输入」的窗口。与网页侧的 `active` 算法同口径(贝塔通 P89:最近 5 分钟)。
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;   // 最后一次输入的时刻,与 GetTickCount 同基准(自开机的毫秒数)
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// 距最后一次键鼠输入多久。取不到时返回 <see cref="TimeSpan.Zero"/>(见 <see cref="IsActive"/> 的取舍)。
    public static TimeSpan IdleTime()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
        // ★ 两者都是 32 位、约 49.7 天回绕。unchecked 的无符号减法在回绕处仍给出正确差值,
        //   所以**不要**换成 TickCount64 去减 dwTime —— 那才会在回绕后算出一个几十天的空闲。
        uint now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - lii.dwTime));
    }

    /// 这台机器前面有没有人。
    ///
    /// ★ **取不到输入信息时按「有人」算**(fail-open)。判据:这道门挡的是「人走了但机器还开着」,
    ///   而 absolute 与心跳两道门仍然照常兜底;反过来 fail-closed 的代价是**某个 API 出岔子就把
    ///   全场学生踢出考试**。两边的错法不对称,所以这里有意选宽的那边。
    public static bool IsActive()
    {
        if (!OperatingSystem.IsWindows()) return true;   // 采集端只跑 Windows;非 Windows 不做判定
        try { return IdleTime() < Window; }
        catch (DllNotFoundException) { return true; }
        catch (EntryPointNotFoundException) { return true; }
    }
}
