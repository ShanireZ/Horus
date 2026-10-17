using System.Management;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 可行動裝置/USB 捲到達。任何插入 → 中風險 + 觸發抓圖。
/// Win32_VolumeChangeEvent.EventType = 2 表示設備到達(Arrival)。
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
