using Horus.Contracts;

namespace Horus.Agent.Model;

/// 信號源產出的原始信號(未蓋時間戳/序號/雜湊)。
/// SignalType / AgentEvent 等線協定類型已移入 Horus.Contracts 供 Agent+Server 共用。
public sealed record RawSignal(
    SignalType Type,
    Dictionary<string, object?> Payload,
    int Risk = 0,
    bool TriggerCapture = false,
    string? CaptureReason = null);
