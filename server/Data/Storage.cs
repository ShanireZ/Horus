using System.Text;

namespace Horus.Server.Data;

/// 截圖原圖存檔案系統:images/&lt;exam&gt;/&lt;seat&gt;/&lt;imageId&gt;.webp。DB 只存相對路徑指針。
/// **原圖永不出網**(architecture §5)。
public sealed class Storage
{
    private readonly string _root;

    public Storage(string dataDir)
    {
        _root = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(Path.Combine(_root, "images"));
    }

    /// 資料根(絕對路徑)。歸檔作業據此定位 archive 庫檔案與冷存目錄。
    public string Root => _root;

    /// 相對路徑(存 DB)。exam/seat 做路徑安全淨化,防目錄穿越。
    public static string RelPath(string examId, string seatId, string imageId)
        => $"images/{Safe(examId)}/{Safe(seatId)}/{imageId}.webp";

    public async Task<string> SaveWebpAsync(string examId, string seatId, string imageId, byte[] bytes)
    {
        string rel = RelPath(examId, seatId, imageId);
        string full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes);
        return rel;
    }

    /// 由相對路徑還原絕對路徑以讀取原圖。回傳 null 表示越界(拒絕服務)。
    public string? Resolve(string relPath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relPath));
        // 邊界須帶路徑分隔符,否則 "C:\data" 會誤匹配 "C:\data-evil\..."
        string rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // ---- M3 歸檔:證據圖遷冷存 / 清理非關鍵圖 ----

    /// 歸檔冷存相對路徑:archive/&lt;exam&gt;/&lt;seat&gt;/&lt;imageId&gt;.webp。
    public static string ArchiveRelPath(string examId, string seatId, string imageId)
        => $"archive/{Safe(examId)}/{Safe(seatId)}/{imageId}.webp";

    /// 證據圖遷入冷存,回傳新相對路徑。**冪等**:源在則移動;源已不在但目標已存在(重跑)→ 直接回傳;
    /// 源與目標都在(重跑殘留)→ 刪源保目標。目錄自動建立。
    public string MoveToArchive(string liveRel, string examId, string seatId, string imageId)
    {
        string archiveRel = ArchiveRelPath(examId, seatId, imageId);
        string dstFull = Path.Combine(_root, archiveRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
        string? srcFull = Resolve(liveRel);
        bool srcOk = srcFull is not null && File.Exists(srcFull);
        bool dstOk = File.Exists(dstFull);
        if (srcOk && !dstOk) File.Move(srcFull!, dstFull);
        else if (srcOk && dstOk) File.Delete(srcFull!);   // 目標已在(重跑),刪殘留源
        return archiveRel;
    }

    /// 刪除 live 原圖(清理非關鍵)。越界 / 不存在均靜默忽略。
    public void DeleteLive(string liveRel)
    {
        string? f = Resolve(liveRel);
        if (f is not null && File.Exists(f)) File.Delete(f);
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
