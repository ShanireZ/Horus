using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Horus.Agent.Buffer;

/// 斷網緩衝(至少一次投遞的本機真相)。
/// 事件:每條 `seq\t信封JSON` 追加到 events.pending.jsonl;伺服器**逐條 ack(seq)** 後精確刪除該 seq
///   (不用範圍 upto——共享 seq 空間有空洞,範圍壓實會誤刪從未送達的低 seq 證據)。
/// 圖片:每張存 images/&lt;seq&gt;.webp + &lt;seq&gt;.meta(imageId\ttrigger\tphash);重連補傳,成功即刪。
/// 序號高水位持久化到 seq.state,處理程序重新啟動後從其上繼續,杜絕 seq 重複使用(否則新事件撞舊 seq 被伺服器冪等吞掉)。
/// 重寫用原子 File.Replace,崩潰只會留 .tmp,建構時恢復。
public sealed class LocalBuffer
{
    private readonly string _eventsFile;
    private readonly string _eventsTmp;
    private readonly string _imagesDir;
    private readonly string _seqStateFile;
    private readonly object _gate = new();
    private readonly HashSet<long> _acked = new();     // 已確認待壓實的 seq(攢批,避免每 ack 全檔案重寫)
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

    /// 崩潰恢復:若主檔案缺失而 .tmp 存在(原子替換未完成),提升 .tmp 為主檔案,避免整批未確認事件成孤兒。
    private void RecoverCrashedRewrite()
    {
        lock (_gate)
            if (!File.Exists(_eventsFile) && File.Exists(_eventsTmp))
                File.Move(_eventsTmp, _eventsFile);
    }

    /// 清理孤兒圖片檔案:①.webp 無對應 .meta(寫 webp 後、寫 meta 前崩潰)——SnapshotPendingImages 以 meta 驅動,
    /// 無 meta 的 webp 永不被拾取也永不被 RemoveImage 刪,長期斷網 + 反覆崩潰會單調累積佔盤;
    /// ②.meta.tmp 殘留(原子 meta 寫未完成);③.meta 無對應 .webp(殘留)。僅建構時(無並行寫)執行。
    private void CleanupOrphans()
    {
        lock (_gate)
            foreach (string f in Directory.GetFiles(_imagesDir))   // GetFiles 已物化:可在迭代中安全刪除
            {
                if (f.EndsWith(".meta.tmp", StringComparison.OrdinalIgnoreCase))
                    Try(() => File.Delete(f));                                   // 未完成的原子 meta 寫殘留
                else if (f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(f[..^5] + ".meta")) Try(() => File.Delete(f));   // .webp 無 .meta → 孤兒
                }
                else if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(f[..^5] + ".webp")) Try(() => File.Delete(f));   // .meta 無 .webp → 殘留
                }
            }
    }

    /// 解析一行 "seq\t信封JSON":檢核 seq 可解析 + json 是合法 JSON。崩潰/掉電撕裂的壞行回傳 false,
    /// 讀側據此**整行丟棄** —— 既不把損壞 JSON 當事件續傳(伺服器 Parse 失敗永不 ack → 卡死續傳佇列),
    /// 也不讓半截行汙染統計。壞行至多損失其自身那條證據,不拖累其它。
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
                // 若上次追加被崩潰/掉電撕裂(末尾無換行),先補一個換行使本條獨佔一行 —— 杜絕壞的半截行與本條
                // 併成一行導致本條也一併損壞丟失(讀側 TryParseLine 會把那條壞半截行整行丟棄)。
                fs.Seek(-1, SeekOrigin.End);
                if (fs.ReadByte() != '\n') fs.WriteByte((byte)'\n');   // ReadByte 已把位置移到末尾 = 追加點
            }
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);   // 寫入磁碟(fsync):減少掉電丟失已寫入的證據事件
        }
        return Task.CompletedTask;
    }

    /// 未確認事件快照(按 seq 升序),用於重連後續傳。
    public IReadOnlyList<(long seq, string json)> SnapshotPendingEvents()
    {
        lock (_gate)
        {
            if (!File.Exists(_eventsFile)) return Array.Empty<(long, string)>();
            var list = new List<(long, string)>();
            foreach (string line in File.ReadLines(_eventsFile))
                if (TryParseLine(line, out long seq, out string json)    // 壞行(撕裂)整行跳過,不續傳垃圾
                    && !_acked.Contains(seq))                            // 已確認待壓實的不再續傳
                    list.Add((seq, json));
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list;
        }
    }

    /// 伺服器已持久化該 seq → 標記待刪;攢夠閾值再一次性壓實(均攤,避免每 ack O(N) 全檔案重寫)。
    /// 對不存在/重複的 seq 是 O(1) 記賬,不觸發重寫。崩潰時未壓實的已確認事件會重傳,伺服器冪等去重。
    public void RemoveEvent(long seq)
    {
        lock (_gate)
        {
            _acked.Add(seq);
            if (_acked.Count >= CompactThreshold) CompactLocked();
        }
    }

    /// 立即壓實(如關停時),把已確認 seq 從 pending 檔案刪除。
    public void Compact()
    {
        lock (_gate) CompactLocked();
    }

    /// 清空**待續傳**的事件與圖片,但保留 seq 高水位(seq.state 不動 —— seq 重複使用防護跨工作階段仍然成立)。
    /// 用於 OIDC 換場/重登:舊工作階段 K_sess 已失效,殘留緩衝在新工作階段下必 bad_sig / identity_mismatch、
    /// 永不被 ack → 每次重連重放-被拒-佔盤。這些影格本就無法再送達(工作階段已死),誠實丟棄。
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

    /// 原子重寫:寫 tmp → File.Replace(原子替換)。崩潰只會留 .tmp,建構時 RecoverCrashedRewrite 恢復。
    private void RewriteEvents(Func<long, bool> keep)
    {
        if (!File.Exists(_eventsFile)) return;
        var kept = new List<string>();
        foreach (string line in File.ReadLines(_eventsFile))
            if (TryParseLine(line, out long seq, out _) && keep(seq))   // 順帶把壞行(撕裂)從緩衝物理剔除
                kept.Add(line);
        File.WriteAllLines(_eventsTmp, kept);
        File.Replace(_eventsTmp, _eventsFile, null);
    }

    // ======== 序號高水位(防重新啟動重複使用) ========

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
                fs.Flush(true);   // 寫入磁碟(fsync):否則掉電丟 ceiling 寫 → 重新啟動重複使用塊內 seq(與事件緩衝同根因)
            }
            if (File.Exists(_seqStateFile)) File.Replace(tmp, _seqStateFile, null);
            else File.Move(tmp, _seqStateFile);
        }
    }

    /// 緩衝中最大 seq(事件 ∪ 圖片),重新啟動時把序號計數器抬到其上作雙保險。
    /// 圖片側**只解析檔案名裡的 seq、不讀檔案體**(舊實作經 SnapshotPendingImages 把每張 webp 全量讀入記憶體,
    /// 長斷網積壓成百上千張時啟動記憶體峰值/延遲隨之線性膨脹 —— 閉合第三輪 F6)。
    public long MaxBufferedSeq()
    {
        long max = 0;
        foreach ((long seq, _) in SnapshotPendingEvents()) if (seq > max) max = seq;
        lock (_gate)
            foreach (string webp in Directory.EnumerateFiles(_imagesDir, "*.webp"))
                if (long.TryParse(Path.GetFileNameWithoutExtension(webp), out long seq) && seq > max) max = seq;
        return max;
    }

    // ======== 圖片 ========

    /// imageId = 用戶端預生成 id(觸發型抓圖有值,重連補傳用同一 id 保證事件關聯;baseline 無值傳 null)。
    public Task EnqueueImageAsync(long seq, string? imageId, string trigger, ulong phash, byte[] webp)
    {
        lock (_gate)
        {
            File.WriteAllBytes(Path.Combine(_imagesDir, seq + ".webp"), webp);
            // meta **原子後寫**(tmp → rename):其存在即代表 webp 已完整寫入磁碟 + meta 自身完整;
            // 杜絕 File.WriteAllText 被崩潰/掉電撕裂成半截 meta 致 trigger/phash 失真(SnapshotPendingImages 以 meta 驅動)。
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
                if (parts.Length < 3) continue;   // 殘缺 meta(理論上原子寫後不會出現)→ 跳過,不誤當 baseline / phash 歸零
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
