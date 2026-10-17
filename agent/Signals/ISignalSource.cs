using Horus.Agent.Model;

namespace Horus.Agent.Signals;

/// 信號源統一介面。各源在後台採集,經 Signal 事件吐出 RawSignal。
public interface ISignalSource : IDisposable
{
    string Name { get; }
    event Action<RawSignal>? Signal;
    void Start();
    void Stop();
}
