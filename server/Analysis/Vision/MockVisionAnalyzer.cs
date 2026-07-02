using System.Text;

namespace Horus.Server.Analysis.Vision;

/// 确定性 Mock 视觉分析器:按图字节里的 ASCII 标记产出可判定结果。
/// 用于测试与本地联调 —— **不出网、不需 key**,让「图入库 → 分析 → 落库 → 风险聚合」全链可测,
/// 供应商决策(DeepSeek 云 API vs 自托管 MiMo)不阻塞骨架落地。
public sealed class MockVisionAnalyzer : IVisionAnalyzer
{
    public string Engine => "mock";

    public bool SendsOffNetwork => false;   // 本地确定性判定,不出网 → 派生可直通(测试用假图字节保留 ASCII 标记)

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
