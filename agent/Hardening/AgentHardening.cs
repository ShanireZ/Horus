using System.Security.Principal;

namespace Horus.Agent.Hardening;

/// M5 採集端硬化·Windows 專屬助手(重新啟動取證標記 + 管理員自檢)。純邏輯判定在 agentcore(RestartClassifier 等·已單測),
/// 此處只做檔案 IO 與 Win32 身分查詢(真機驗收)。
public static class RestartMarker
{
    /// 讀舊標記(無檔案→null)後寫 "running"。回傳舊標記供 <see cref="RestartClassifier.IsUnexpectedRestart"/> 判定。
    /// 語義:上次正常退出會寫 "clean";若啟動讀到仍是 "running" → 上次被結束/崩潰/斷電(異常終止)。
    public static string? ReadThenMarkRunning(string path)
    {
        string? prev = null;
        try { if (File.Exists(path)) prev = File.ReadAllText(path); } catch { /* 讀失敗按無標記處理 */ }
        try { File.WriteAllText(path, RestartClassifier.Running); } catch { /* 寫失敗不致命 */ }
        return prev;
    }

    /// 正常退出時寫 "clean"(下次啟動據此判定非異常重新啟動)。
    public static void MarkClean(string path)
    {
        try { File.WriteAllText(path, RestartClassifier.Clean); } catch { /* 寫失敗不致命 */ }
    }
}

/// 當前處理程序是否管理員(採集需管理員跑 ETW / UIAutomation / WMI;非管理員=能力降級)。
public static class WinPrivilege
{
    public static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
