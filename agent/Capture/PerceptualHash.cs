using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Horus.Agent.Capture;

/// dHash:縮放到 9×8 灰度,逐行比較相鄰像素 → 64 位指紋。漢明距離衡量相似度。
public static class PerceptualHash
{
    public static ulong DHash(SixLabors.ImageSharp.Image source)   // 顯式限定:UseWindowsForms 注入的 System.Drawing.Image 造成歧義
    {
        using Image<L8> img = source.CloneAs<L8>();
        img.Mutate(x => x.Resize(9, 8));

        ulong hash = 0UL;
        int bit = 0;
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < 8; y++)
            {
                Span<L8> row = accessor.GetRowSpan(y);
                for (int x = 0; x < 8; x++)
                {
                    if (row[x].PackedValue < row[x + 1].PackedValue)
                        hash |= 1UL << bit;
                    bit++;
                }
            }
        });
        return hash;
    }

    public static int Hamming(ulong a, ulong b)
        => System.Numerics.BitOperations.PopCount(a ^ b);
}
