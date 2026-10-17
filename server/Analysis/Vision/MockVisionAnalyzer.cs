using System.Text;

namespace Horus.Server.Analysis.Vision;

/// 確定性 Mock 視覺分析器:按圖位元組裡的 ASCII 標記產出可判定結果。
/// 用於測試與本機整合測試 —— **不出網、不需 key**,讓「圖入庫 → 分析 → 寫入資料庫 → 風險聚合」全鏈可測,
/// 供應商決策(DeepSeek 雲 API vs 自託管 MiMo)不阻塞骨架完成實作。
public sealed class MockVisionAnalyzer : IVisionAnalyzer
{
    public string Engine => "mock";

    public bool SendsOffNetwork => false;   // 本機確定性判定,不出網 → 派生可直通(測試用假圖位元組保留 ASCII 標記)

    public Task<VisionVerdict?> AnalyzeAsync(byte[] imageBytes, VisionContext ctx, CancellationToken ct)
    {
        string s = Encoding.ASCII.GetString(imageBytes);
        VisionVerdict v =
            s.Contains("AICHAT")       ? new() { Suspicious = true,  Category = "web_ai",      Confidence = 95, Hits = ["chatgpt-ui"],  Evidence = "检测到 AI 对话界面" } :
            s.Contains("SEARCHRESULT") ? new() { Suspicious = true,  Category = "search",      Confidence = 85, Hits = ["search-page"], Evidence = "检测到搜索引擎结果页" } :
            s.Contains("GHOSTCODE")    ? new() { Suspicious = true,  Category = "ide_plugin",  Confidence = 80, Hits = ["ghost-text"],  Evidence = "检测到 IDE AI 代码补全(灰色幽灵文本)" } :
            s.Contains("REMOTEDESK")   ? new() { Suspicious = true,  Category = "remote_tool", Confidence = 75, Hits = ["remote-tool"], Evidence = "检测到远程协助工具界面" } :
                                         new() { Suspicious = false, Category = "none",        Confidence = 10, Evidence = "未见明显作弊迹象" };
        return Task.FromResult<VisionVerdict?>(v);
    }
}
