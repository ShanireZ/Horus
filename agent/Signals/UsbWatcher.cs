using System.Management;
using Honus.Agent.Model;
using Honus.Contracts;

namespace Honus.Agent.Signals;

/// 可移动设备/USB 卷到达。任何插入 → 中风险 + 触发抓图。
/// Win32_VolumeChangeEvent.EventType = 2 表示设备到达(Arrival)。
public sealed class UsbWatcher : ISignalSource
{
    public string Name => "usb";
    public event Action<RawSignal>? Signal;

    private ManagementEventWatcher? _w;

    public void Start()
    {
        var q = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2");
        _w = new ManagementEventWatcher(q);
        _w.EventArrived += (_, e) =>
            Signal?.Invoke(new RawSignal(SignalType.Usb,
                new() { ["drive"] = e.NewEvent["DriveName"] },
                Risk: 50, TriggerCapture: true, CaptureReason: "usb_insert"));
        _w.Start();
    }

    public void Stop() => _w?.Stop();
    public void Dispose() => _w?.Dispose();
}
