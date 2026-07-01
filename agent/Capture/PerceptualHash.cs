using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Honus.Agent.Capture;

/// dHash:缩放到 9×8 灰度,逐行比较相邻像素 → 64 位指纹。汉明距离衡量相似度。
public static class PerceptualHash
{
    public static ulong DHash(SixLabors.ImageSharp.Image source)   // 显式限定:UseWindowsForms 注入的 System.Drawing.Image 造成歧义
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
