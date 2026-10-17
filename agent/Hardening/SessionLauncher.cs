using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Agent.Hardening;

/// M5 保活層1 輔助:Windows 服務跑在 **session 0**(無交互桌面·截不到屏),故服務拉起採集 agent 時須用
/// `CreateProcessAsUser` 把它啟到**當前交互工作階段**(session 1+),採集才能抓到學員螢幕。
/// **真機驗收項**:純 P/Invoke,本機無法執行驗證;任一步失敗回傳 null,由呼叫方退回普通 Process.Start
/// (退回在"看門狗本身已在使用者工作階段"的層2 場景下仍正確;僅"服務→使用者工作階段"層1 相依本類)。
[SupportedOSPlatform("windows")]
public static class SessionLauncher
{
    /// 在當前活動控制檯工作階段裡以該工作階段使用者身分啟動 exe;成功回傳子處理程序 pid,失敗回傳 null。
    public static int? StartInActiveSession(string exePath, IReadOnlyList<string> args)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return null;   // 無活動工作階段(無人登入)

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken)) return null;
        IntPtr dupToken = IntPtr.Zero, envBlock = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out dupToken))
                return null;
            if (!CreateEnvironmentBlock(out envBlock, dupToken, false)) envBlock = IntPtr.Zero;   // 環境塊失敗不致命

            string cmdLine = "\"" + exePath + "\"";
            foreach (string a in args) cmdLine += " \"" + a.Replace("\"", "\\\"") + "\"";

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\default" };
            bool ok = CreateProcessAsUser(dupToken, null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, envBlock, null, ref si, out PROCESS_INFORMATION pi);
            if (!ok) return null;
            int pid = (int)pi.dwProcessId;
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            return pid;
        }
        catch { return null; }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    // ---- Win32 ----
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, ref SECURITY_ATTRIBUTES lpTokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr phNewToken);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
}
