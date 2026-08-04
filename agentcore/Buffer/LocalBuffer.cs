using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Horus.Agent.Buffer;

/// 断网缓冲(至少一次投递的本地真相)。
/// 事件:每条 `seq\t信封JSON` 追加到 events.pending.jsonl;服务器**逐条 ack(seq)** 后精确删除该 seq
///   (不用范围 upto——共享 seq 空间有空洞,范围压实会误删从未送达的低 seq 证据)。
/// 图片:每张存 images/&lt;seq&gt;.webp + &lt;seq&gt;.meta(imageId\ttrigger\tphash);重连补传,成功即删。
/// 序号高水位持久化到 seq.state,进程重启后从其上继续,杜绝 seq 复用(否则新事件撞旧 seq 被服务器幂等吞掉)。
/// 重写用原子 File.Replace,崩溃只会留 .tmp,构造时恢复。
public sealed class LocalBuffer
{
    private readonly string _eventsFile;
    private readonly string _eventsTmp;
    private readonly string _imagesDir;
    private readonly string _seqStateFile;
    private readonly object _gate = new();
    private readonly HashSet<long> _acked = new();     // 已确认待压实的 seq(攒批,避免每 ack 全文件重写)
    private const int CompactThreshold = 64;

    public LocalBuffer(string dir)
    {
        Directory.CreateDirectory(dir);
        _eventsFile = Path.Combine(dir, "events.pending.jsonl");
        _eventsTmp = _eventsFile + ".tmp";
        _imagesDir = Path.Combine(dir, "images");
        _seqStateFile = Path.Combine(dir, "seq.state");
        Directory.CreateDirectory(_imagesDir);
        RecoverCrashedRewrite();
        CleanupOrphans();
    }

    /// 崩溃恢复:若主文件缺失而 .tmp 存在(原子替换未完成),提升 .tmp 为主文件,避免整批未确认事件成孤儿。
    private void RecoverCrashedRewrite()
    {
        lock (_gate)
            if (!File.Exists(_eventsFile) && File.Exists(_eventsTmp))
                File.Move(_eventsTmp, _eventsFile);
    }

    /// 清理孤儿图片文件:①.webp 无对应 .meta(写 webp 后、写 meta 前崩溃)——SnapshotPendingImages 以 meta 驱动,
    /// 无 meta 的 webp 永不被拾取也永不被 RemoveImage 删,长期断网 + 反复崩溃会单调累积占盘;
    /// ②.meta.tmp 残留(原子 meta 写未完成);③.meta 无对应 .webp(残留)。仅构造时(无并发写)运行。
    private void CleanupOrphans()
    {
        lock (_gate)
            foreach (string f in Directory.GetFiles(_imagesDir))   // GetFiles 已物化:可在迭代中安全删除
            {
                if (f.EndsWith(".meta.tmp", StringComparison.OrdinalIgnoreCase))
                    Try(() => File.Delete(f));                                   // 未完成的原子 meta 写残留
                else if (f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(f[..^5] + ".meta")) Try(() => File.Delete(f));   // .webp 无 .meta → 孤儿
                }
                else if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(f[..^5] + ".webp")) Try(() => File.Delete(f));   // .meta 无 .webp → 残留
                }
            }
    }

    /// 解析一行 "seq\t信封JSON":校验 seq 可解析 + json 是合法 JSON。崩溃/掉电撕裂的坏行返回 false,
    /// 读侧据此**整行丢弃** —— 既不把损坏 JSON 当事件续传(服务器 Parse 失败永不 ack → 卡死续传队列),
    /// 也不让半截行污染统计。坏行至多损失其自身那条证据,不拖累其它。
    private static bool TryParseLine(string line, out long seq, out string json)
    {
        seq = 0; json = "";
        int tab = line.IndexOf('\t');
        if (tab <= 0) return false;
        if (!long.TryParse(line.AsSpan(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out seq)) return false;
        json = line[(tab + 1)..];
        try { using var _ = JsonDocument.Parse(json); return true; }
        catch { return false; }
    }

    // ======== 事件 ========

    public Task EnqueueEventAsync(long seq, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(seq.ToString(CultureInfo.InvariantCulture) + "\t" + json + "\n");
        lock (_gate)
        {
            using var fs = new FileStream(_eventsFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (fs.Length > 0)
            {
                // 若上次追加被崩溃/掉电撕裂(末尾无换行),先补一个换行使本条独占一行 —— 杜绝坏的半截行与本条
                // 并成一行导致本条也一并损坏丢失(读侧 TryParseLine 会把那条坏半截行整行丢弃)。
                fs.Seek(-1, SeekOrigin.End);
                if (fs.ReadByte() != '\n') fs.WriteByte((byte)'\n');   // ReadByte 已把位置移到末尾 = 追加点
            }
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);   // 落盘(fsync):减少掉电丢失已写入的证据事件
        }
        return Task.CompletedTask;
    }

    /// 未确认事件快照(按 seq 升序),用于重连后续传。
    public IReadOnlyList<(long seq, string json)> SnapshotPendingEvents()
    {
        lock (_gate)
        {
            if (!File.Exists(_eventsFile)) return Array.Empty<(long, string)>();
            var list = new List<(long, string)>();
            foreach (string line in File.ReadLines(_eventsFile))
                if (TryParseLine(line, out long seq, out string json)    // 坏行(撕裂)整行跳过,不续传垃圾
                    && !_acked.Contains(seq))                            // 已确认待压实的不再续传
                    list.Add((seq, json));
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list;
        }
    }

    /// 服务器已持久化该 seq → 标记待删;攒够阈值再一次性压实(均摊,避免每 ack O(N) 全文件重写)。
    /// 对不存在/重复的 seq 是 O(1) 记账,不触发重写。崩溃时未压实的已确认事件会重传,服务器幂等去重。
    public void RemoveEvent(long seq)
    {
        lock (_gate)
        {
            _acked.Add(seq);
            if (_acked.Count >= CompactThreshold) CompactLocked();
        }
    }

    /// 立即压实(如关停时),把已确认 seq 从 pending 文件删除。
    public void Compact()
    {
        lock (_gate) CompactLocked();
    }

    /// 清空**待续传**的事件与图片,但保留 seq 高水位(seq.state 不动 —— seq 复用防护跨会话仍然成立)。
    /// 用于 OIDC 换场/重登:旧会话 K_sess 已失效,残留缓冲在新会话下必 bad_sig / identity_mismatch、
    /// 永不被 ack → 每次重连重放-被拒-占盘。这些帧本就无法再送达(会话已死),诚实丢弃。
    public void PurgeSession()
    {
        lock (_gate)
        {
            _acked.Clear();
            Try(() => File.Delete(_eventsFile));
            Try(() => File.Delete(_eventsTmp));
            foreach (string f in Directory.GetFiles(_imagesDir)) Try(() => File.Delete(f));
        }
    }

    private void CompactLocked()
    {
        if (_acked.Count == 0) return;
        RewriteEvents(s => !_acked.Contains(s));
        _acked.Clear();
    }

    /// 原子重写:写 tmp → File.Replace(原子替换)。崩溃只会留 .tmp,构造时 RecoverCrashedRewrite 恢复。
    private void RewriteEvents(Func<long, bool> keep)
    {
        if (!File.Exists(_eventsFile)) return;
        var kept = new List<string>();
        foreach (string line in File.ReadLines(_eventsFile))
            if (TryParseLine(line, out long seq, out _) && keep(seq))   // 顺带把坏行(撕裂)从缓冲物理剔除
                kept.Add(line);
        File.WriteAllLines(_eventsTmp, kept);
        File.Replace(_eventsTmp, _eventsFile, null);
    }

    // ======== 序号高水位(防重启复用) ========

    public long LoadSeqCeiling()
    {
        lock (_gate)
        {
            try { return File.Exists(_seqStateFile) && long.TryParse(File.ReadAllText(_seqStateFile), out long v) ? v : 0; }
            catch { return 0; }
        }
    }

    public void SaveSeqCeiling(long ceiling)
    {
        lock (_gate)
        {
            string tmp = _seqStateFile + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] b = Encoding.UTF8.GetBytes(ceiling.ToString(CultureInfo.InvariantCulture));
                fs.Write(b, 0, b.Length);
                fs.Flush(true);   // 落盘(fsync):否则掉电丢 ceiling 写 → 重启复用块内 seq(与事件缓冲同根因)
            }
            if (File.Exists(_seqStateFile)) File.Replace(tmp, _seqStateFile, null);
            else File.Move(tmp, _seqStateFile);
        }
    }

    /// 缓冲中最大 seq(事件 ∪ 图片),重启时把序号计数器抬到其上作双保险。
    /// 图片侧**只解析文件名里的 seq、不读文件体**(旧实现经 SnapshotPendingImages 把每张 webp 全量读入内存,
    /// 长断网积压成百上千张时启动内存峰值/延迟随之线性膨胀 —— 闭合第三轮 F6)。
    public long MaxBufferedSeq()
    {
        long max = 0;
        foreach ((long seq, _) in SnapshotPendingEvents()) if (seq > max) max = seq;
        lock (_gate)
            foreach (string webp in Directory.EnumerateFiles(_imagesDir, "*.webp"))
                if (long.TryParse(Path.GetFileNameWithoutExtension(webp), out long seq) && seq > max) max = seq;
        return max;
    }

    // ======== 图片 ========

    /// imageId = 客户端预生成 id(触发型抓图有值,重连补传用同一 id 保证事件关联;baseline 无值传 null)。
    public Task EnqueueImageAsync(long seq, string? imageId, string trigger, ulong phash, byte[] webp)
    {
        lock (_gate)
        {
            File.WriteAllBytes(Path.Combine(_imagesDir, seq + ".webp"), webp);
            // meta **原子后写**(tmp → rename):其存在即代表 webp 已完整落盘 + meta 自身完整;
            // 杜绝 File.WriteAllText 被崩溃/掉电撕裂成半截 meta 致 trigger/phash 失真(SnapshotPendingImages 以 meta 驱动)。
            string meta = Path.Combine(_imagesDir, seq + ".meta");
            string tmp = meta + ".tmp";
            File.WriteAllText(tmp, (imageId ?? "") + "\t" + trigger + "\t" + phash.ToString("x16"));
            if (File.Exists(meta)) File.Replace(tmp, meta, null); else File.Move(tmp, meta);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<(long seq, string? imageId, string trigger, ulong phash, byte[] webp)> SnapshotPendingImages()
    {
        lock (_gate)
        {
            var list = new List<(long, string?, string, ulong, byte[])>();
            foreach (string metaPath in Directory.EnumerateFiles(_imagesDir, "*.meta"))
            {
                string name = Path.GetFileNameWithoutExtension(metaPath);
                if (!long.TryParse(name, out long seq)) continue;
                string webpPath = Path.Combine(_imagesDir, seq + ".webp");
                if (!File.Exists(webpPath)) continue;
                string[] parts = File.ReadAllText(metaPath).Split('\t');
                if (parts.Length < 3) continue;   // 残缺 meta(理论上原子写后不会出现)→ 跳过,不误当 baseline / phash 归零
                string? imageId = parts[0].Length > 0 ? parts[0] : null;
                string trigger = parts[1];
                ulong phash = ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong p) ? p : 0;
                list.Add((seq, imageId, trigger, phash, File.ReadAllBytes(webpPath)));
            }
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list;
        }
    }

    public void RemoveImage(long seq)
    {
        lock (_gate)
        {
            Try(() => File.Delete(Path.Combine(_imagesDir, seq + ".webp")));
            Try(() => File.Delete(Path.Combine(_imagesDir, seq + ".meta")));
        }
    }

    private static void Try(Action a) { try { a(); } catch { /* ignore */ } }
}
