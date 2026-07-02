using Horus.Server.Config;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Server.Analysis.Vision;

/// §5 送云前的派生处理 —— **降采样**(压 token/成本、少送无关像素)+ **剥离元数据**(EXIF/XMP/ICC 不随派生图出网),
/// 再重编码 WebP。**原图字节只读,只送本方法产出的派生字节**。
///
/// 注(owner 决策·2026-07-02):**不再做打码身份 / 裁剪**(按考场 UI 逐一配矩形的运维负担 > 收益,且供应商=小米 MiMo 境内云·PIPL 无跨境)。
/// 只保留分辨率无关的降采样 + 元数据剥离。
///
/// `mustStrip`(送云分析器 = true)决定隐私铁律的强度:
///   • mustStrip=true(联网):**始终**解码 + 剥元数据 + 重编码(即便 `visionMaxEdge≤0` 不缩放也剥),解码失败**返回 null**
///     由调用方跳过 —— 绝不把带 EXIF/XMP/ICC 的原字节送出局域网(§5「原图永不出网」)。
///   • mustStrip=false(本地/mock·不出网):`visionMaxEdge≤0` 直通原字节(零开销·mock 测试用假图字节保留标记),
///     解码失败也直通原字节(不出网,无泄漏面)。
public static class VisionImagePrep
{
    /// 返回派生字节;见类注释关于 `mustStrip` 的两条路径。
    public static byte[]? Prepare(byte[] original, ServerConfig cfg, bool mustStrip = false)
    {
        int maxEdge = cfg.VisionMaxEdge;
        // 不出网(mock/联调)且不降采样 → 直通不解码(零开销)。联网分析器绝不走此直通,必落到下方解码+剥离。
        if (!mustStrip && maxEdge <= 0) return original;

        try
        {
            using var inMs = new MemoryStream(original);
            using Image<Rgba32> image = Image.Load<Rgba32>(inMs);

            // 剥离元数据:派生图只含像素,不随源图 EXIF/XMP/IPTC/ICC 出网(#15)。
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;

            if (maxEdge > 0 && Math.Max(image.Width, image.Height) > maxEdge)   // 配了上限且长边超限才降采样
                image.Mutate(m => m.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(maxEdge, maxEdge),
                }));

            using var outMs = new MemoryStream();
            image.SaveAsWebp(outMs, new WebpEncoder { Quality = 75 });
            return outMs.ToArray();
        }
        catch
        {
            // 联网分析器:无法安全派生 → 返回 null,调用方跳过该图(fail-safe,绝不泄未剥元数据的原图)。
            // 本地/mock:直通原字节(不出网,无泄漏面,保持既有行为 + mock 假图字节可达分析器)。
            return mustStrip ? null : original;
        }
    }
}
