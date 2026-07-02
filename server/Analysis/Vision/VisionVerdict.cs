namespace Horus.Server.Analysis.Vision;

/// 视觉 LLM 对一张截图的判定 —— **L2:视觉 LLM 取代 OCR + L3 Logo,合并为单一视觉级**(owner 拍板)。
/// 一次「看懂画面」同时做:文字提取 + AI 对话界面 / 搜索页 / IDE 幽灵补全 / 远控工具识别 + 分类。
/// 只作一条**线索**(是否可疑 + 类别 + 置信度 + 人话证据 + 可选文字),由人裁决(架构铁律 §3)。
public sealed record VisionVerdict
{
    public bool Suspicious { get; init; }
    /// web_ai | search | ide_plugin | remote_tool | other | none
    public string Category { get; init; } = "none";
    public int Confidence { get; init; }          // 0–100
    public string[] Hits { get; init; } = [];     // 命中标签/关键词(落库 ocr_results.hits)
    public string Evidence { get; init; } = "";   // 一句话中文证据(人话)
    public string? Text { get; init; }            // 截图里的关键可见文字(可选,落库 ocr_results.text)

    /// 类别 → 可疑队列 kind(与 Suspicion 既有 kind 对齐)。
    public string Kind() => Category switch
    {
        "web_ai" => "web_ai",
        "search" => "search",
        "ide_plugin" => "ide_plugin_suspect",
        "remote_tool" => "remote_tool",
        _ => "suspect",
    };
}

/// 分析上下文(供 adapter 提示词 / 日志 / 落库定位)。
public sealed record VisionContext(string ExamId, string SeatId, string ImageId, string Trigger);

/// 视觉分析器抽象。**provider-agnostic**:Mock(测试/联调·不出网)、OpenAI 兼容(DeepSeek-V4 / 小米 MiMo-V2.5 /
/// Qwen-VL / GLM-4V 靠 config 换 baseUrl+model+key)、或后续本地自托管(vLLM·零出网)都实现它。
/// 返回 null = 分析失败/跳过(fail-open,**绝不阻断采集管线**)。
public interface IVisionAnalyzer
{
    /// 落库 ocr_results.engine 的标识,如 "mock" / "openai:deepseek-v4-pro"。
    string Engine { get; }

    /// 该分析器是否会把图**送出局域网**(云端点)。true = 送云:送前**必须**剥离元数据(§5「原图永不出网」),
    /// 且派生失败时宁可跳过也不泄原图;false = 本地/mock(不出网),派生可直通原字节零开销。
    bool SendsOffNetwork { get; }

    Task<VisionVerdict?> AnalyzeAsync(byte[] imageBytes, VisionContext ctx, CancellationToken ct);
}
