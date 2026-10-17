using System.Management;
using Horus.Agent.Config;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 處理程序啟動/退出(WMI __InstanceCreation/DeletionEvent,事件驅動·無輪詢)。
/// 非白名單處理程序啟動 → 高風險 + 觸發抓圖。
/// 事件驅動已滿足"低延遲"意圖;ETW(Microsoft.Diagnostics.Tracing.TraceEvent)可作為未來更低延遲/更細事件的備選,
/// 但需引入原生相依且仍受"CommandLine 需管理員權限才非空"限制,故保持託管的 WMI 事件監聽實作。
public sealed class ProcessWatcher : ISignalSource
{
    public string Name => "process-watcher";
    public event Action<RawSignal>? Signal;

    private readonly LiveConfig _live;                 // 處理程序白名單可熱更新
    private ManagementEventWatcher? _start, _stop;

    public ProcessWatcher(LiveConfig live) => _live = live;

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
        bool allowed = _live.IsWhitelistedProc(nameNoExt);

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
