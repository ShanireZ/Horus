using Horus.Server.Config;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Server.Analysis.Vision;

/// §5 送雲前的派生處理 —— **降取樣**(壓 token/成本、少送無關像素)+ **剝離中繼資料**(EXIF/XMP/ICC 不隨派生圖出網),
/// 再重編碼 WebP。**原圖位元組唯讀,只送本方法產出的派生位元組**。
///
/// 注(owner 決策·2026-07-02):**不再做打碼身分 / 裁剪**(按考場 UI 逐一配矩形的維運負擔 > 收益,且供應商=小米 MiMo 境內雲·PIPL 無跨境)。
/// 只保留解析度無關的降取樣 + 中繼資料剝離。
///
/// `mustStrip`(送雲分析器 = true)決定隱私鐵律的強度:
///   • mustStrip=true(聯網):**始終**解碼 + 剝中繼資料 + 重編碼(即便 `visionMaxEdge≤0` 不縮放也剝),解碼失敗**回傳 null**
///     由呼叫方跳過 —— 絕不把帶 EXIF/XMP/ICC 的原位元組送出區域網路(§5「原圖永不出網」)。
///   • mustStrip=false(本機/mock·不出網):`visionMaxEdge≤0` 直通原位元組(零開銷·mock 測試用假圖位元組保留標記),
///     解碼失敗也直通原位元組(不出網,無洩漏面)。
public static class VisionImagePrep
{
    /// 解碼像素上限(縱深防解碼炸彈):≈ 4096×4096,遠超 1080p 採集(1920×1080≈207 萬)。超此判失敗不全解碼。
    private const long MaxDecodePixels = 4096L * 4096L;

    /// 回傳派生位元組;見類註解關於 `mustStrip` 的兩條路徑。
    public static byte[]? Prepare(byte[] original, ServerConfig cfg, bool mustStrip = false)
    {
        int maxEdge = cfg.VisionMaxEdge;
        // 不出網(mock/整合測試)且不降取樣 → 直通不解碼(零開銷)。聯網分析器絕不走此直通,必落到下方解碼+剝離。
        if (!mustStrip && maxEdge <= 0) return original;

        try
        {
            using var inMs = new MemoryStream(original);
            // F10 縱深:2MB 體上限只約束**壓縮後**位元組;高壓縮比圖(大面積純色)可在 <2MB 內編碼巨幅點陣圖,
            // 解碼即 4 位元組/px 鉅額分配(20000×20000≈1.6GB)。先 Identify 唯讀頭探尺寸(不解碼像素),超上限直接判失敗,
            // 不進全解碼。上限 MaxDecodePixels(≈ 4096×4096,遠超 1080p 採集)。
            ImageInfo info = Image.Identify(inMs);
            if ((long)info.Width * info.Height > MaxDecodePixels)
                return mustStrip ? null : original;   // 聯網:寧跳過不洩原圖;本機/mock:直通(不出網,無洩漏面)
            inMs.Position = 0;
            using Image<Rgba32> image = Image.Load<Rgba32>(inMs);

            // 剝離中繼資料:派生圖只含像素,不隨源圖 EXIF/XMP/IPTC/ICC 出網(#15)。
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;

            if (maxEdge > 0 && Math.Max(image.Width, image.Height) > maxEdge)   // 配了上限且長邊超限才降取樣
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
            // 聯網分析器:無法安全派生 → 回傳 null,呼叫方跳過該圖(fail-safe,絕不洩未剝中繼資料的原圖)。
            // 本機/mock:直通原位元組(不出網,無洩漏面,保持既有行為 + mock 假圖位元組可達分析器)。
            return mustStrip ? null : original;
        }
    }
}
