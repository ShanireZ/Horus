namespace Horus.Server.Analysis.Vision;

/// 視覺 LLM 對一張截圖的判定 —— **L2:視覺 LLM 取代 OCR + L3 Logo,合併為單一視覺級**(owner 拍板)。
/// 一次「看懂畫面」同時做:文字提取 + AI 對話介面 / 搜尋頁 / IDE 幽靈補全 / 遠控工具識別 + 分類。
/// 只作一條**線索**(是否可疑 + 類別 + 信賴度 + 人話證據 + 可選文字),由人裁決(架構鐵律 §3)。
public sealed record VisionVerdict
{
    public bool Suspicious { get; init; }
    /// web_ai | search | ide_plugin | remote_tool | other | none
    public string Category { get; init; } = "none";
    public int Confidence { get; init; }          // 0–100
    public string[] Hits { get; init; } = [];     // 命中標籤/關鍵詞(寫入資料庫 ocr_results.hits)
    public string Evidence { get; init; } = "";   // 一句話中文證據(人話)
    public string? Text { get; init; }            // 截圖裡的關鍵可見文字(可選,寫入資料庫 ocr_results.text)

    /// 類別 → 可疑佇列 kind(與 Suspicion 既有 kind 對齊)。
    public string Kind() => Category switch
    {
        "web_ai" => "web_ai",
        "search" => "search",
        "ide_plugin" => "ide_plugin_suspect",
        "remote_tool" => "remote_tool",
        _ => "suspect",
    };
}

/// 分析上下文(供 adapter 提示詞 / 紀錄 / 寫入資料庫定位)。
public sealed record VisionContext(string ExamId, string SeatId, string ImageId, string Trigger);

/// 視覺分析器抽象。**provider-agnostic**:Mock(測試/整合測試·不出網)、OpenAI 相容(DeepSeek-V4 / 小米 MiMo-V2.5 /
/// Qwen-VL / GLM-4V 靠 config 換 baseUrl+model+key)、或後續本機自託管(vLLM·零出網)都實作它。
/// 回傳 null = 分析失敗/跳過(fail-open,**絕不阻斷採集管線**)。
public interface IVisionAnalyzer
{
    /// 寫入資料庫 ocr_results.engine 的標識,如 "mock" / "openai:deepseek-v4-pro"。
    string Engine { get; }

    /// 該分析器是否會把圖**送出區域網路**(雲端點)。true = 送雲:送前**必須**剝離中繼資料(§5「原圖永不出網」),
    /// 且派生失敗時寧可跳過也不洩原圖;false = 本機/mock(不出網),派生可直通原位元組零開銷。
    bool SendsOffNetwork { get; }

    Task<VisionVerdict?> AnalyzeAsync(byte[] imageBytes, VisionContext ctx, CancellationToken ct);
}
