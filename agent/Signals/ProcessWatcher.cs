using System.Management;
using Honus.Agent.Model;
using Honus.Contracts;

namespace Honus.Agent.Signals;

/// 进程启动/退出(WMI __InstanceCreation/DeletionEvent)。
/// 非白名单进程启动 → 高风险 + 触发抓图。
/// TODO: 低延迟可改用 ETW(Microsoft.Diagnostics.Tracing.TraceEvent)。CommandLine 需管理员权限才非空。
public sealed class ProcessWatcher : ISignalSource
{
    public string Name => "process-watcher";
    public event Action<RawSignal>? Signal;

    private readonly HashSet<string> _whitelist;
    private ManagementEventWatcher? _start, _stop;

    public ProcessWatcher(IEnumerable<string> whitelistProcs)
        => _whitelist = new(whitelistProcs.Select(p => p.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        _start = Make("__InstanceCreationEvent", OnStart);
        _stop = Make("__InstanceDeletionEvent", OnStop);
        _start.Start();
        _stop.Start();
    }

    private static ManagementEventWatcher Make(string evt, EventArrivedEventHandler h)
    {
        var q = new WqlEventQuery($"SELECT * FROM {evt} WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'");
        var w = new ManagementEventWatcher(q);
        w.EventArrived += h;
        return w;
    }

    private void OnStart(object sender, EventArrivedEventArgs e)
    {
        var p = (ManagementBaseObject)e.NewEvent["TargetInstance"];
        string name = ((string?)p["Name"] ?? "").ToLowerInvariant();
        string nameNoExt = name.EndsWith(".exe") ? name[..^4] : name;
        bool allowed = _whitelist.Contains(nameNoExt);

        Signal?.Invoke(new RawSignal(SignalType.ProcessStart,
            new()
            {
                ["name"] = name,
                ["pid"] = p["ProcessId"],
                ["cmd"] = p["CommandLine"],
                ["whitelisted"] = allowed,
            },
            Risk: allowed ? 0 : 70,
            TriggerCapture: !allowed,
            CaptureReason: allowed ? null : "non_whitelist_process"));
    }

    private void OnStop(object sender, EventArrivedEventArgs e)
    {
        var p = (ManagementBaseObject)e.NewEvent["TargetInstance"];
        Signal?.Invoke(new RawSignal(SignalType.ProcessExit, new()
        {
            ["name"] = p["Name"],
            ["pid"] = p["ProcessId"],
        }));
    }

    public void Stop() { _start?.Stop(); _stop?.Stop(); }
    public void Dispose() { _start?.Dispose(); _stop?.Dispose(); }
}
