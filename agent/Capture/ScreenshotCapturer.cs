using System.Drawing;
using System.Drawing.Imaging;
using Horus.Agent.Config;
using Horus.Agent.Hardening;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image;

namespace Horus.Agent.Capture;

/// 抓屏 → 縮放到目標高度 → WebP 編碼 → pHash →(可選去重)→ 交上傳委託,回傳主屏 imageId。
/// 抓圖串行化(SemaphoreSlim),避免並行觸發時去重狀態競爭。
/// 多顯示器(BUG#4):遍歷 Screen.AllScreens 每張都抓一張(覆蓋"第二顯示器"盲區);主屏 imageId 作為事件關聯錨點。
public sealed class ScreenshotCapturer : IDisposable
{
    private readonly LiveConfig _live;                                     // 目標高度 / WebP 品質可熱更新
    private readonly Func<byte[], string, ulong, Task<string?>> _upload;   // (webp, trigger, phash) => imageId
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly TimeSpan _minGap = TimeSpan.FromMilliseconds(1500);   // 觸發型連發去抖
    private DateTime _lastUtc = DateTime.MinValue;
    // 每屏獨立去重狀態(按設備名 key,隨顯示器插拔動態):避免多屏互相干擾。
    private readonly Dictionary<string, (ulong Phash, bool Has)> _lastByScreen = new();

    /// M5 防遮蔽:每影格算出廉價 luma 統計餵給此回呼(Program 接 ScreenQuality 分類 → 疑似遮蔽則上報)。可空=不檢測。
    public Action<ScreenStats>? OnStats { get; set; }

    public ScreenshotCapturer(LiveConfig live, Func<byte[], string, ulong, Task<string?>> upload)
    {
        _live = live;
        _upload = upload;
    }

    /// dedupAgainstLast=true:對靜止畫面做 pHash 去重(隨機基線層傳 false——每張都要留)。
    /// 多顯示器:每張屏各抓一張並分別上傳;主屏(Primary)的 imageId 作為事件關聯錨點回傳。
    public async Task<string?> CaptureAsync(string trigger, bool dedupAgainstLast)
    {
        await _sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (dedupAgainstLast && DateTime.UtcNow - _lastUtc < _minGap) return null;
            _lastUtc = DateTime.UtcNow;

            // 多顯示器:每張屏都抓(覆蓋第二屏盲區 · BUG#4)。
            var screens = System.Windows.Forms.Screen.AllScreens;
            string? primaryId = null;
            bool statsReported = false;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                byte[] webp;
                ulong phash;
                using (Bitmap bmp = GrabScreen(screen))
                using (IsImage img = ToImageSharp(bmp))
                {
                    Downscale(img, _live.TargetHeight);
                    // M5:每影格算 luma 統計喂遮蔽檢測。僅主屏喂(保持原 M5 語義:單路遮蔽判定),檢測失敗不影響主採集。
                    if (OnStats is not null && !statsReported && screen.Primary)
                        try { OnStats(ComputeLumaStats(img)); statsReported = true; } catch { /* 遮蔽檢測異常吞掉 */ }
                    phash = PerceptualHash.DHash(img);

                    // 每屏獨立去重(靜止畫面跳過),避免多屏互相干擾;key=設備名(隨插拔動態)。
                    string key = screen.DeviceName;
                    _lastByScreen.TryGetValue(key, out var last);
                    if (dedupAgainstLast && last.Has && PerceptualHash.Hamming(phash, last.Phash) <= 3)
                        continue;   // 該屏靜止,跳過上傳(繼續抓其餘屏)
                    _lastByScreen[key] = (phash, true);

                    webp = EncodeWebp(img, _live.WebpQuality);
                }
                // 多屏時在 trigger 追加 :screen{i},伺服器端 isEvent 判定(StartsWith "event:")與基線抽樣不受影響。
                string? id = await _upload(webp, trigger + (screens.Length > 1 ? $":screen{i}" : ""), phash).ConfigureAwait(false);
                if (screen.Primary) primaryId = id;     // 主屏作為事件關聯錨點
                if (primaryId is null) primaryId = id;  // 無主屏時備援
            }
            return primaryId;
        }
        finally { _sem.Release(); }
    }

    private static Bitmap GrabScreen(System.Windows.Forms.Screen screen)
    {
        // 多顯示器:用各屏 Bounds(虛擬螢幕座標)抓取,覆蓋第二屏盲區(BUG#4)。
        var b = screen.Bounds;
        var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
        return bmp;
    }

    private static IsImage ToImageSharp(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        return IsImage.Load(ms);
    }

    private static void Downscale(IsImage img, int targetHeight)
    {
        if (img.Height <= targetHeight) return;
        int w = (int)Math.Round(img.Width * (double)targetHeight / img.Height);
        img.Mutate(x => x.Resize(w, targetHeight));
    }

    /// 廉價 luma 統計:降到 32×32 灰度算方差(近純色→方差≈0)。W/H 取真實截圖尺寸(判壞尺寸/過小)。
    private static ScreenStats ComputeLumaStats(IsImage img)
    {
        int w = img.Width, h = img.Height;
        using Image<L8> g = img.CloneAs<L8>();
        g.Mutate(x => x.Resize(32, 32));
        double sum = 0, sumSq = 0;
        int n = 0;
        g.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                Span<L8> row = acc.GetRowSpan(y);
                for (int i = 0; i < row.Length; i++) { double v = row[i].PackedValue; sum += v; sumSq += v * v; n++; }
            }
        });
        double mean = n > 0 ? sum / n : 0;
        double variance = n > 0 ? Math.Max(0, sumSq / n - mean * mean) : 0;
        return new ScreenStats(w, h, variance);
    }

    private static byte[] EncodeWebp(IsImage img, int quality)
    {
        using var ms = new MemoryStream();
        img.Save(ms, new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy });
        return ms.ToArray();
    }

    public void Dispose() => _sem.Dispose();
}
