using System.Text;

namespace Honus.Server.Data;

/// 截图原图存文件系统:images/&lt;exam&gt;/&lt;seat&gt;/&lt;imageId&gt;.webp。DB 只存相对路径指针。
/// **原图永不出网**(architecture §5)。
public sealed class Storage
{
    private readonly string _root;

    public Storage(string dataDir)
    {
        _root = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(Path.Combine(_root, "images"));
    }

    /// 相对路径(存 DB)。exam/seat 做路径安全净化,防目录穿越。
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

    /// 由相对路径还原绝对路径以读取原图。返回 null 表示越界(拒绝服务)。
    public string? Resolve(string relPath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relPath));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
