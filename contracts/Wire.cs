using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Contracts;

/// 信號類型。序列化為 snake_case(見 Json.Wire),與 api-contract / schema 對齊。
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
    // M5 採集端硬化健康信號(Agent 自檢上報·純檢測:讓規避暴露,不阻斷)。序列化 snake_case。
    WatchdogRestart,      // 採集處理程序被結束後由看門狗拉起,重新啟動上報(關採集的取證)
    SuspectedSuspend,     // 採集循環 wall-clock 跳變超閾值(被 suspend / 睡眠 / 鎖屏)
    ScreenshotObscured,   // 截圖純色 / 低熵 / 尺寸異常(黑屏 / 遮擋 / DRM 保護視窗)
    CapabilityDegraded,   // 非管理員 / 某信號源(ETW/UIA/WMI)持續失敗
}

/// 上報事件(封裝後含雜湊鏈欄位)。欄位順序與 api-contract §0.1 canonical 一致。
/// Agent 用它建置+封裝;測試用它建構合法簽名事件。伺服器寫入資料庫走 JsonDocument 直取原始 payload,
/// 不經此類型反序列化(避免 object?→JsonElement 再序列化的保真問題)。
public sealed record AgentEvent
{
    [JsonIgnore] public int V { get; init; } = 1;       // 協定版本走信封,不進 event 體

    public required string ExamId { get; init; }
    public required string SeatId { get; init; }
    public required string AgentId { get; init; }
    public required string MachineId { get; init; }
    public double Ts { get; init; }                      // Unix 秒(含小數),本機時鐘
    public required SignalType Type { get; init; }
    public Dictionary<string, object?> Payload { get; init; } = new();
    public int Risk { get; init; }                       // 本機初判 0-100
    public string? EvidenceImageId { get; init; }
    public long Seq { get; init; }
    public string? HashPrev { get; init; }
    public string? HashSelf { get; init; }
}

/// 全域 JSON 約定:camelCase 欄位名、snake_case 枚舉值、省略 null。
/// 序列化與反序列化兩端必須一致(伺服器需用同樣約定復算 canonical 雜湊)。
public static class Json
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
