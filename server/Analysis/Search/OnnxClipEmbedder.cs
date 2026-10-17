using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Server.Analysis.Search;

/// M3 按圖搜圖·CLIP 預處理:圖像位元組 → CHW float 張量(resize+center-crop 到 224² + CLIP 歸一化)。
/// **與嵌入器解耦 → 可單測**(純像素運算,不碰 ONNX 執行環境)。歸一化常數取 CLIP ViT-B/32 官方值。
public static class ClipPreprocess
{
    public const int Size = 224;
    private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };   // RGB
    private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// 回傳 float[3*224*224](通道優先 CHW)。decode 失敗拋(由呼叫方兜)。
    public static float[] ToTensor(byte[] imageBytes)
    {
        using Image<Rgb24> img = Image.Load<Rgb24>(imageBytes);
        img.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(Size, Size),
            Mode = ResizeMode.Crop,                     // 填滿再中心裁剪 ≈ CLIP 的 Resize+CenterCrop
            Position = AnchorPositionMode.Center,
        }));
        var t = new float[3 * Size * Size];
        int plane = Size * Size;
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < Size; y++)
            {
                Span<Rgb24> row = acc.GetRowSpan(y);
                for (int x = 0; x < Size; x++)
                {
                    Rgb24 p = row[x];
                    int idx = y * Size + x;
                    t[idx] = (p.R / 255f - Mean[0]) / Std[0];               // R 通道
                    t[plane + idx] = (p.G / 255f - Mean[1]) / Std[1];       // G
                    t[2 * plane + idx] = (p.B / 255f - Mean[2]) / Std[2];   // B
                }
            }
        });
        return t;
    }
}

/// M3 按圖搜圖·本機 ONNX CLIP 圖像編碼器(CPU 推理·無需 GPU·**零出網**)。MiMo 無 embeddings 端點故走本機。
/// 模型 = CLIP ViT-B/32 圖像編碼器匯出的 ONNX(512 維·部署提供)。輸入/輸出張量名可配(留空取模型首個)。
public sealed class OnnxClipEmbedder : IImageEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly ILogger<OnnxClipEmbedder> _log;

    public bool Enabled => true;
    public int Dim { get; }

    public OnnxClipEmbedder(string modelPath, string? inputName, string? outputName, int dim, ILogger<OnnxClipEmbedder> log)
    {
        _session = new InferenceSession(modelPath);
        _inputName = string.IsNullOrEmpty(inputName) ? _session.InputMetadata.Keys.First() : inputName!;
        // ★輸出選擇:CLIP vision_model 常有多輸出(last_hidden_state + pooler_output);取錯=拿到一大坨 patch 序列而非嵌入。
        //   留空則優先名字含 embed/pool 的輸出(image_embeds / pooler_output),再退首個。可用 embedOnnxOutput 顯式指定。
        _outputName = SelectOutputName(_session.OutputMetadata.Keys, outputName);
        Dim = dim;
        _log = log;
        _log.LogInformation("ONNX CLIP 嵌入器就绪 model={Path} in={In} out={Out}", modelPath, _inputName, _outputName);
    }

    public Task<float[]?> EmbedAsync(byte[] imageBytes, CancellationToken ct)
    {
        try
        {
            float[] chw = ClipPreprocess.ToTensor(imageBytes);
            var input = new DenseTensor<float>(chw, new[] { 1, 3, ClipPreprocess.Size, ClipPreprocess.Size });
            var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(feeds, new[] { _outputName });
            Tensor<float> t = results.First().AsTensor<float>();
            float[] vec = ExtractEmbedding(t.ToArray(), t.Dimensions.ToArray());
            if (vec.Length == 0) return Task.FromResult<float[]?>(null);
            VecMath.Normalize(vec);   // 單位化(檢索用餘弦,規範化無害)
            return Task.FromResult<float[]?>(vec);
        }
        catch (Exception ex) { _log.LogWarning(ex, "ONNX CLIP 推理失败 image={N}B", imageBytes.Length); return Task.FromResult<float[]?>(null); }
    }

    /// 選嵌入輸出名:顯式 override > 名含 "embed"(image_embeds) > 名含 "pool"(pooler_output) > 首個。
    internal static string SelectOutputName(IEnumerable<string> outputs, string? overrideName)
    {
        if (!string.IsNullOrEmpty(overrideName)) return overrideName!;
        var list = outputs.ToList();
        return list.FirstOrDefault(n => n.Contains("embed", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(n => n.Contains("pool", StringComparison.OrdinalIgnoreCase))
            ?? list.First();
    }

    /// 從輸出張量取嵌入向量(batch=1):pooled [1,D]→全 D;序列 [1,S,D]→取首位(CLS)前 D 個。
    /// 因行主序,[0,0,d] 恰是扁平緩衝前 D 個 → 統一取「前 last-dim 個」即可。
    internal static float[] ExtractEmbedding(float[] flat, int[] dims)
    {
        if (flat.Length == 0) return flat;
        int last = dims.Length > 0 ? dims[^1] : flat.Length;
        return last > 0 && last <= flat.Length ? flat[..last] : flat;
    }

    public void Dispose() => _session.Dispose();
}
