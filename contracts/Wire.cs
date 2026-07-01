using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honus.Contracts;

/// 信号类型。序列化为 snake_case(见 Json.Wire),与 api-contract / schema 对齐。
public enum SignalType
{
    WindowFocus,
    BrowserUrl,
    ProcessStart,
    ProcessExit,
    Clipboard,
    AltTabBurst,
    Usb,
    Screenshot,
    Heartbeat,
}

/// 上报事件(封装后含哈希链字段)。字段顺序与 api-contract §0.1 canonical 一致。
/// Agent 用它构建+封装;测试用它构造合法签名事件。服务器落库走 JsonDocument 直取原始 payload,
/// 不经此类型反序列化(避免 object?→JsonElement 再序列化的保真问题)。
public sealed record AgentEvent
{
    [JsonIgnore] public int V { get; init; } = 1;       // 协议版本走信封,不进 event 体

    public required string ExamId { get; init; }
    public required string SeatId { get; init; }
    public required string AgentId { get; init; }
    public required string MachineId { get; init; }
    public double Ts { get; init; }                      // Unix 秒(含小数),本机时钟
    public required SignalType Type { get; init; }
    public Dictionary<string, object?> Payload { get; init; } = new();
    public int Risk { get; init; }                       // 本地初判 0-100
    public string? EvidenceImageId { get; init; }
    public long Seq { get; init; }
    public string? HashPrev { get; init; }
    public string? HashSelf { get; init; }
}

/// 全局 JSON 约定:camelCase 字段名、snake_case 枚举值、省略 null。
/// 序列化与反序列化两端必须一致(服务器需用同样约定复算 canonical 哈希)。
public static class Json
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
