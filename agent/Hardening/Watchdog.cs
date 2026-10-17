using System.Diagnostics;

namespace Horus.Agent.Hardening;

/// M5 保活·看門狗(supervisor)。三層縱深的**層2**:反覆確保採集 agent 子處理程序存活,異常退出即按退避重新啟動。
/// 最強的**層1**由 Windows 服務承載(見 <see cref="WindowsService"/>);**層3**是伺服器心跳告警(採集被關即看板暴露)。
/// 單例:命名 Mutex 防多看門狗並存。子處理程序被結束(非 0 退出)→ 重新啟動;正常退出(0,如 Ctrl+C)→ 不重新啟動同退。
public static class Watchdog
{
    /// 以 supervisor 身分執行。selfExe=本 exe;childArgs=傳給採集模式的參數(通常是 config 路徑);
    /// adoptPid≥0 時先監控**已存在**的採集處理程序(層2 互拉:看門狗被殺後由 agent 重新拉起並 adopt 現有 agent)。
    /// serviceSession=true(層1 服務模式):經 <see cref="SessionLauncher"/> 把採集拉進使用者交互工作階段(session 0 截不到屏);
    /// false(層2 使用者工作階段看門狗):普通 Process.Start。
    public static int RunSupervisor(string selfExe, string[] childArgs, int adoptPid, string instanceKey, bool serviceSession, CancellationToken ct)
    {
        using var single = new Mutex(initiallyOwned: false, "Global\\Horus_Watchdog_" + Sanitize(instanceKey), out bool _);
        bool held;
        try { held = single.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }   // 上個看門狗崩潰遺留 → 接管
        if (!held) { Console.WriteLine("[horus-watchdog] 已有看门狗在运行,本实例退出。"); return 0; }

        try
        {
            // adopt:先守著已存在的採集處理程序,等它退出再進入拉起循環。
            if (adoptPid > 0 && TryGetProcess(adoptPid) is Process existing)
            {
                Console.WriteLine($"[horus-watchdog] adopt 现有采集 pid={adoptPid}");
                WaitForExit(existing, ct);
                if (ct.IsCancellationRequested) return 0;
            }

            int backoffMs = 1000;
            while (!ct.IsCancellationRequested)
            {
                DateTime startedAt = DateTimeOffset.UtcNow.UtcDateTime;
                Process? child = LaunchChild(selfExe, childArgs, serviceSession);
                if (child is null)
                {
                    if (!Sleep(backoffMs, ct)) break;
                    backoffMs = Math.Min(backoffMs * 2, 30000);
                    continue;
                }
                Console.WriteLine($"[horus-watchdog] 采集子进程已启动 pid={child.Id}");
                WaitForExit(child, ct);

                if (ct.IsCancellationRequested) { TryKill(child); break; }
                int code = SafeExitCode(child);
                if (code == 0) { Console.WriteLine("[horus-watchdog] 采集正常退出,看门狗同退。"); break; }

                // 執行足夠久(>60s)視為一次獨立崩潰 → 重置退避,避免偶發重新啟動把退避累積到 30s。
                if ((DateTimeOffset.UtcNow.UtcDateTime - startedAt).TotalSeconds > 60) backoffMs = 1000;
                Console.Error.WriteLine($"[horus-watchdog] 采集异常退出 code={code},{backoffMs}ms 后重启");
                if (!Sleep(backoffMs, ct)) break;
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
        finally { try { single.ReleaseMutex(); } catch { /* 未持有忽略 */ } }
        return 0;
    }

    /// 採集端(agent 模式)守護:監控看門狗 pid,若看門狗被殺 → 拉起新看門狗(adopt 本 agent),繼續守新看門狗。層2 互拉。
    public static async Task GuardWatchdogAsync(int watchdogPid, string selfExe, string[] childArgs, string instanceKey, CancellationToken ct)
    {
        int current = watchdogPid;
        while (!ct.IsCancellationRequested)
        {
            Process? wd = TryGetProcess(current);
            if (wd is null) { current = RelaunchAdoptWatchdog(selfExe, childArgs, instanceKey); if (current <= 0) return; }
            else
            {
                try { await wd.WaitForExitAsync(ct); }
                catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;
                Console.Error.WriteLine("[horus-agent] 看门狗已退出,尝试重新拉起(adopt 本进程)。");
                current = RelaunchAdoptWatchdog(selfExe, childArgs, instanceKey);
                if (current <= 0) return;
            }
        }
    }

    private static int RelaunchAdoptWatchdog(string selfExe, string[] childArgs, string instanceKey)
    {
        try
        {
            var psi = new ProcessStartInfo(selfExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add("--adopt");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (string a in childArgs) psi.ArgumentList.Add(a);
            Process? p = Process.Start(psi);
            return p?.Id ?? -1;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 重拉看门狗失败: {ex.Message}"); return -1; }
    }

    private static Process? LaunchChild(string selfExe, string[] childArgs, bool serviceSession)
    {
        // 層1 服務模式:CreateProcessAsUser 拉進使用者工作階段(截屏必需);失敗退回普通啟動(在使用者工作階段跑的看門狗仍正確)。
        if (serviceSession && OperatingSystem.IsWindows())
        {
            int? pid = SessionLauncher.StartInActiveSession(selfExe, childArgs);
            if (pid is not null) { try { return Process.GetProcessById(pid.Value); } catch { return null; } }
            Console.Error.WriteLine("[horus-watchdog] 会话内启动失败,回退普通启动(可能截不到交互桌面)。");
        }
        try
        {
            var psi = new ProcessStartInfo(selfExe) { UseShellExecute = false };
            foreach (string a in childArgs) psi.ArgumentList.Add(a);
            psi.Environment["HORUS_WATCHDOG_PID"] = Environment.ProcessId.ToString();   // 子處理程序據此守護看門狗(層2 互拉)
            return Process.Start(psi);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-watchdog] 启动采集失败: {ex.Message}"); return null; }
    }

    private static void WaitForExit(Process p, CancellationToken ct)
    {
        try { p.WaitForExit((int)Math.Min(int.MaxValue, 1000)); while (!p.HasExited && !ct.IsCancellationRequested) p.WaitForExit(1000); }
        catch { /* 處理程序句柄異常按已退出處理 */ }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { Process p = Process.GetProcessById(pid); return p.HasExited ? null : p; }
        catch { return null; }
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }
    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ } }

    /// 可取消的 sleep;回傳 false 表示已取消(應退出循環)。
    private static bool Sleep(int ms, CancellationToken ct) => !ct.WaitHandle.WaitOne(ms);

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }
}
