using Horus.Server.Analysis.Search;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Horus.Server.Tests;

/// 真 ONNX CLIP 模型冒煙(閉掉「真模型未本機驗證」殘留,2026-07-03 owner 已將模型放入 server/Data/model.onnx)。
/// 模型是部署物**不入 git**(.gitignore *.onnx),故本測試**發現模型才跑**、無模型環境靜默通過 ——
/// 有模型的開發機 / 部署驗收機上,它鎖定:模型可載入、IO 名自動選擇正確、推理產出 512 維單位向量且隨圖變化。
public class OnnxClipSmokeTests
{
    /// 從測試輸出目錄向上找儲存庫根(含 Horus.sln),定位 server/Data/model.onnx。
    private static string? FindModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Horus.sln"))) dir = dir.Parent;
        if (dir is null) return null;
        string p = Path.Combine(dir.FullName, "server", "Data", "model.onnx");
        return File.Exists(p) ? p : null;
    }

    private static byte[] MakePng(byte r, byte g, byte b)
    {
        using var img = new Image<Rgb24>(320, 240, new Rgb24(r, g, b));
        // 加一塊對比色矩形,避免純色圖在 center-crop 後資訊過少
        for (int y = 40; y < 120; y++)
            for (int x = 60; x < 200; x++)
                img[x, y] = new Rgb24((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task 真模型冒烟_加载_推理_512维单位向量_随图变化()
    {
        string? modelPath = FindModel();
        if (modelPath is null) return;   // 模型不入 git:無模型環境(CI/他機)靜默通過

        using var embedder = new OnnxClipEmbedder(modelPath, inputName: null, outputName: null, dim: 512,
            NullLogger<OnnxClipEmbedder>.Instance);

        float[]? red = await embedder.EmbedAsync(MakePng(220, 30, 30), CancellationToken.None);
        float[]? blue = await embedder.EmbedAsync(MakePng(30, 60, 220), CancellationToken.None);

        Assert.NotNull(red);    // null = 推理失敗(IO 名不符 / 模型損壞),看 OnnxClipEmbedder 紀錄
        Assert.NotNull(blue);
        Assert.Equal(512, red!.Length);
        Assert.Equal(512, blue!.Length);

        double norm = Math.Sqrt(red.Sum(x => (double)x * x));
        Assert.InRange(norm, 0.99, 1.01);                     // EmbedAsync 已單位化

        double cos = VecMath.Cosine(red, blue);
        Assert.True(cos < 0.9999, $"不同图产出几乎相同向量(cos={cos}),疑似取错输出张量(patch 序列/常数)");
        Assert.True(cos > -1.0001 && cos <= 1.0001);

        // 同圖重複推理確定性(同一處理程序內)
        float[]? red2 = await embedder.EmbedAsync(MakePng(220, 30, 30), CancellationToken.None);
        Assert.NotNull(red2);
        Assert.True(VecMath.Cosine(red, red2!) > 0.9999);
    }
}
