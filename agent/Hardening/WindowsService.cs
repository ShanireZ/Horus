using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Horus.Agent.Hardening;

/// M5 保活層1:以 **Windows 服務(LocalSystem)** 承載看門狗 —— 學員標準帳戶殺不掉 LocalSystem 服務,
/// 服務被停 / 採集被關都靠**心跳斷→看板告警**備援(層3)。服務體 = <see cref="Watchdog.RunSupervisor"/>(serviceSession=true·
/// 經 SessionLauncher 把採集拉進使用者交互工作階段)。安裝/卸載走 sc.exe(免額外 SCM P/Invoke)。**真機驗收**。
[SupportedOSPlatform("windows")]
public static class WindowsService
{
    public const string ServiceName = "HorusAgentWatchdog";

    /// 以服務身分執行(由 SCM 啟動;也可控制檯直接跑偵錯)。configArgs=傳給採集模式的參數(config 路徑)。
    public static void Run(string selfExe, string[] configArgs, string instanceKey)
    {
        IHost host = Host.CreateDefaultBuilder()
            .UseWindowsService(o => o.ServiceName = ServiceName)
            .ConfigureServices(s => s.AddHostedService(_ => new WatchdogHostedService(selfExe, configArgs, instanceKey)))
            .Build();
        host.Run();
    }

    /// 註冊服務:binPath = "<exe> --service <configArgs>"。start=auto·LocalSystem。回傳結束代碼。
    public static int Install(string selfExe, string[] configArgs)
    {
        string bin = "\"" + selfExe + "\" --service" + string.Concat(configArgs.Select(a => " \"" + a + "\""));
        int rc = Sc($"create {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto obj= LocalSystem DisplayName= \"Horus 监考采集看门狗\"");
        if (rc == 0) { Sc($"description {ServiceName} \"Horus 监考:看门狗保活采集端(纯检测·不阻断)\""); Sc($"start {ServiceName}"); }
        return rc;
    }

    /// 卸載服務:先停再刪。
    public static int Uninstall()
    {
        Sc($"stop {ServiceName}");
        return Sc($"delete {ServiceName}");
    }

    private static int Sc(string args)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false });
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-service] sc {args} 失败: {ex.Message}"); return -1; }
    }
}

/// 服務託管的後台任務:跑看門狗 supervisor(serviceSession=true)。SCM 停止 → stoppingToken 取消 → 看門狗退出。
[SupportedOSPlatform("windows")]
internal sealed class WatchdogHostedService(string selfExe, string[] configArgs, string instanceKey) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => Watchdog.RunSupervisor(selfExe, configArgs, adoptPid: -1, instanceKey, serviceSession: true, stoppingToken), stoppingToken);
}
