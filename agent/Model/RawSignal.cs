using Honus.Contracts;

namespace Honus.Agent.Model;

/// 信号源产出的原始信号(未盖时间戳/序号/哈希)。
/// SignalType / AgentEvent 等线协议类型已移入 Honus.Contracts 供 Agent+Server 共用。
public sealed record RawSignal(
    SignalType Type,
    Dictionary<string, object?> Payload,
    int Risk = 0,
    bool TriggerCapture = false,
    string? CaptureReason = null);
