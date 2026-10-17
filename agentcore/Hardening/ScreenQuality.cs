namespace Horus.Agent.Hardening;

/// M5 防遮蔽:從**廉價像素統計**判斷截圖是否被遮蔽(黑屏 / 純色覆蓋 / DRM 保護視窗黑影格 / 尺寸異常)。
/// **分類器與採集解耦**:Agent 端從 Bitmap 算出 luma 方差 + 尺寸喂進來,此處純邏輯判定 → 可單測。
/// 哲學:只上報"疑似遮蔽",不阻斷;第二顯示器/虛擬桌面仍是結構盲區(靠物理監考)。
public enum ObscureReason
{
    None,
    BadSize,     // 寬/高 ≤ 0(截圖失敗)
    TooSmall,    // 遠小於預期解析度(疑似截到極小視窗 / 採集異常)
    Solid,       // luma 方差≈0 → 近純色(全黑 / 單色覆蓋 / DRM 黑影格)
    LowEntropy,  // luma 方差很低 → 畫面幾乎無內容(疑似大面積遮擋)
}

/// 一張截圖的廉價統計量(0–255 luma 尺度)。DistinctBuckets = 下取樣直方圖非空桶數(可選·0 表示未提供)。
public readonly record struct ScreenStats(int Width, int Height, double LumaVariance, int DistinctBuckets = 0);

/// 判定閾值(可從設定熱更新)。預設保守:只穩抓"近純色 / 壞尺寸 / 過小",避免誤傷深色 IDE(有文字→方差不低)。
/// **引用類型 record**(非 record struct):`new()` 會走主建構函式預設值;record struct 的 `new()` 是零值會繞過預設參數。
public sealed record ScreenQualityThresholds(
    double SolidVariance = 2.0,     // ≤ 此值視為純色
    double LowEntropyVariance = 8.0, // < 此值視為低熵(仍很保守·深色帶程式碼的截圖方差遠高於此)
    int MinEdge = 240);              // 寬或高 < 此值視為過小

public static class ScreenQuality
{
    public static readonly ScreenQualityThresholds Default = new();

    /// 判定遮蔽原因;None = 正常。
    public static ObscureReason Classify(ScreenStats s, ScreenQualityThresholds? t = null)
    {
        ScreenQualityThresholds th = t ?? Default;
        if (s.Width <= 0 || s.Height <= 0) return ObscureReason.BadSize;
        if (s.Width < th.MinEdge || s.Height < th.MinEdge) return ObscureReason.TooSmall;
        if (s.LumaVariance <= th.SolidVariance) return ObscureReason.Solid;
        if (s.LumaVariance < th.LowEntropyVariance) return ObscureReason.LowEntropy;
        return ObscureReason.None;
    }

    public static bool IsObscured(ScreenStats s, ScreenQualityThresholds? t = null)
        => Classify(s, t) != ObscureReason.None;

    /// 契約用小寫標籤(進事件 payload.reason)。
    public static string ReasonLabel(ObscureReason r) => r switch
    {
        ObscureReason.BadSize => "bad_size",
        ObscureReason.TooSmall => "too_small",
        ObscureReason.Solid => "solid",
        ObscureReason.LowEntropy => "low_entropy",
        _ => "none",
    };
}
