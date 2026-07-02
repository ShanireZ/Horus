using System.Text.Json;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Jobs;

/// M3 归档 / 清理作业(architecture §13/§15)。每日(默认每 6h)扫描**结束超留存期(默认 30 天)**的考试:
///   1. 有 pending 未裁决 → **跳过**(不 purge 未决案的证据,留待人工。诚实:宁占空间也不丢证据)。
///   2. 关键数据迁入 **archive 库**:关键事件(有效风险≥阈值 或 被 suspicious_queue 引用)、其证据图(移入冷存·改写路径)
///      + OCR/Logo 结果、裁决记录(confirmed/dismissed)、考试元数据 + 汇总、每条关键事件的 hash_self/sig 锚点。
///   3. **就地清理** live:该考试全部事件 / 图片(非关键图删文件)/ OCR/Logo / 击键 / 心跳 / 可疑队列,
///      exams.status='archived'(留作墓碑);全部完成后 VACUUM 回收空间。
///
/// **幂等**:copy 用 INSERT OR IGNORE、文件移动容忍已迁;若 copy 后崩溃、delete 前重跑,考试仍 'ended'、pending=0,
/// 会重新 copy(无副作用)+ 重新 delete → 收敛。证据在 delete 前已安全落 archive,故任何中断都不丢关键数据。
/// 完整性说明(§13.2):清理非关键事件使原始整链断裂 —— archive 保留每条关键事件的 hash_self/sig 作**独立锚点**,
/// 复验以"单条事件 ↔ 其 hash_self"为准,不再依赖整链连续。
public sealed class ArchiveService : BackgroundService
{
    public sealed record ExamResult(
        string ExamId, string Outcome, int Events, int Images, int Adjudications,
        int DeletedEvents, int DeletedImages, string? Detail);

    public sealed record Report(double Now, int Scanned, int Archived, int Skipped, IReadOnlyList<ExamResult> Exams);

    private readonly Db _db;
    private readonly Storage _storage;
    private readonly ServerConfig _cfg;
    private readonly ILogger<ArchiveService> _log;
    private readonly string _archivePath;
    private readonly object _runGate = new();   // 后台 timer 与手动触发不并跑

    public ArchiveService(Db db, Storage storage, ServerConfig cfg, ILogger<ArchiveService> log)
    {
        _db = db; _storage = storage; _cfg = cfg; _log = log;
        _archivePath = Path.IsPathRooted(cfg.ArchiveDbPath)
            ? cfg.ArchiveDbPath
            : Path.Combine(storage.Root, cfg.ArchiveDbPath);
    }

    public string ArchivePath => _archivePath;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.ArchiveEnabled || _cfg.ArchiveScanIntervalHours <= 0) return;
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }   // 让服务器起稳再首扫
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(_cfg.ArchiveScanIntervalHours));
        do
        {
            try { RunOnce(Now(), ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "归档作业异常"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// 扫描并归档所有到龄考试。返回本次报告。可由后台 timer 或运维端点(手动)调用;内部串行化。
    public Report RunOnce(double now, CancellationToken ct = default)
    {
        lock (_runGate)
        {
            double cutoff = now - _cfg.RetentionDays * 86400.0;
            var candidates = _db.Read(conn =>
            {
                var list = new List<string>();
                // 含 'archiving':上次崩在归档中途的考试(墓碑态)本次续跑收敛。
                using SqliteCommand c = conn.Cmd(
                    "SELECT exam_id FROM exams WHERE status IN ('ended','archiving') AND ended_at IS NOT NULL AND ended_at<@cut ORDER BY ended_at",
                    ("@cut", cutoff));
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(0));
                return list;
            });

            var results = new List<ExamResult>();
            int archived = 0, skipped = 0;
            foreach (string examId in candidates)
            {
                ct.ThrowIfCancellationRequested();
                ExamResult res;
                try { res = ArchiveOneExam(examId, now); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "归档考试 {Exam} 失败", examId);
                    res = new ExamResult(examId, "error", 0, 0, 0, 0, 0, ex.Message);
                }
                results.Add(res);
                if (res.Outcome == "archived") archived++;
                else if (res.Outcome == "skipped") skipped++;
            }

            if (archived > 0)
            {
                try { _db.Write(conn => { using SqliteCommand v = conn.Cmd("VACUUM"); v.ExecuteNonQuery(); }); }
                catch (Exception ex) { _log.LogWarning(ex, "VACUUM 失败(不影响归档正确性,仅空间未回收)"); }
            }
            if (candidates.Count > 0)
                _log.LogInformation("归档扫描完成:候选 {N} 归档 {A} 跳过 {S}", candidates.Count, archived, skipped);
            return new Report(now, candidates.Count, archived, skipped, results);
        }
    }

    private ExamResult ArchiveOneExam(string examId, double now)
    {
        // ---- 门禁 + 墓碑(原子):pending 检查与 status→'archiving' 翻转在**同一写锁 body** 内完成。
        // _db.Write 全程持 _writeGate,其它写者(含 vision)不能交错 → pending 检查后到翻转前无新 pending 混入。
        // 翻转后所有 ingest 对本考试短路(IsExamSealed),消除"读快照→DELETE WHERE exam_id"之间的 late-ingest 窗口。
        string gate = _db.Write(conn =>
        {
            long pending = Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='pending'", ("@e", examId));
            if (pending > 0) return "pending:" + pending;   // 不翻转,留 'ended' 待人工裁决
            using SqliteCommand up = conn.Cmd(
                "UPDATE exams SET status='archiving' WHERE exam_id=@e AND status IN ('ended','archiving')", ("@e", examId));
            return up.ExecuteNonQuery() > 0 ? "ok" : "gone";   // gone = 已被并发处理 / 非 ended
        });
        if (gate.StartsWith("pending:", StringComparison.Ordinal))
        {
            long n = long.Parse(gate.AsSpan("pending:".Length));
            _log.LogWarning("考试 {Exam} 仍有 {N} 条待裁决,跳过归档(须先人工裁决)", examId, n);
            return new ExamResult(examId, "skipped", 0, 0, 0, 0, 0, $"pending={n}");
        }
        if (gate != "ok")
            return new ExamResult(examId, "skipped", 0, 0, 0, 0, 0, "not_ended");

        // ---- 读取阶段:确定关键集合 + 载入待复制数据(此刻 status='archiving',ingest 已短路,快照稳定)----
        ExamSnapshot snap = _db.Read(conn => LoadSnapshot(conn, examId));

        // ---- 复制阶段:写入 archive 库(独立文件 + 事务)----
        using (var arc = OpenArchive())
        using (SqliteTransaction tx = arc.BeginTransaction())
        {
            WriteArchive(arc, tx, examId, now, snap);
            tx.Commit();
        }

        // ---- 清理阶段(文件先删非关键,再 live DB 事务性删除 + 置 archived)----
        foreach (ImageRow img in snap.AllImages)
            if (!snap.KeptImageIds.Contains(img.ImageId))
                _storage.DeleteLive(img.FilePath);

        int delEvents = 0, delImages = 0;
        _db.Write(conn =>
        {
            using SqliteTransaction tx = conn.BeginTransaction();
            // 子表先删(images 被 ocr_results/logo_hits 以 FK 引用)
            Exec(conn, tx, "DELETE FROM logo_hits   WHERE image_id IN (SELECT image_id FROM images WHERE exam_id=@e)", ("@e", examId));
            Exec(conn, tx, "DELETE FROM ocr_results WHERE image_id IN (SELECT image_id FROM images WHERE exam_id=@e)", ("@e", examId));
            delImages = Exec(conn, tx, "DELETE FROM images            WHERE exam_id=@e", ("@e", examId));
            delEvents = Exec(conn, tx, "DELETE FROM events            WHERE exam_id=@e", ("@e", examId));
            Exec(conn, tx, "DELETE FROM keystroke_samples WHERE exam_id=@e", ("@e", examId));
            Exec(conn, tx, "DELETE FROM agent_heartbeats  WHERE exam_id=@e", ("@e", examId));
            Exec(conn, tx, "DELETE FROM suspicious_queue  WHERE exam_id=@e", ("@e", examId));
            Exec(conn, tx, "UPDATE exams SET status='archived' WHERE exam_id=@e", ("@e", examId));
            tx.Commit();
        });

        _log.LogInformation("已归档考试 {Exam}:关键事件 {CE} 证据图 {CI} 裁决 {CA};清理事件 {DE} 图片 {DI}",
            examId, snap.CriticalEvents.Count, snap.KeptImageIds.Count, snap.Adjudications.Count, delEvents, delImages);
        return new ExamResult(examId, "archived",
            snap.CriticalEvents.Count, snap.KeptImageIds.Count, snap.Adjudications.Count, delEvents, delImages, null);
    }

    // ================= 读取快照 =================

    private sealed record EventRow(long Id, string SeatId, string? AgentId, string? MachineId, long Seq, double Ts, string Type,
        string Payload, int Risk, int ServerRisk, string? EvidenceImageId, string? HashPrev, string? HashSelf, string? Sig);
    private sealed record ImageRow(string ImageId, string SeatId, double Ts, string? Trigger, string? Phash,
        string FilePath, int? Width, int? Height, string? Format, long? Bytes);
    private sealed record AdjRow(long Id, string SeatId, double Ts, string Kind, int Score, string Status,
        string? Refs, string? Reviewer, double? DecidedAt, string? Note);
    private sealed record KeystrokeRow(long Id, string SeatId, string? SubmissionId, double Ts,
        string? Timeline, string? Features, int Risk);
    private sealed record ExamSnapshot(
        string? Name, double? StartedAt, double? EndedAt, int SeatCount, int TotalEvents,
        int SuspTotal, int Confirmed, int Dismissed,
        List<EventRow> CriticalEvents, List<ImageRow> AllImages, HashSet<string> KeptImageIds,
        Dictionary<string, ImageRow> KeptImages, List<AdjRow> Adjudications, List<KeystrokeRow> KeptKeystrokes);

    private ExamSnapshot LoadSnapshot(SqliteConnection conn, string examId)
    {
        // 考试元数据 + 汇总计数
        string? name = null; double? started = null, ended = null;
        using (SqliteCommand c = conn.Cmd("SELECT name,started_at,ended_at FROM exams WHERE exam_id=@e", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
            if (r.Read()) { name = r.GetString(0); started = r.IsDBNull(1) ? null : r.GetDouble(1); ended = r.IsDBNull(2) ? null : r.GetDouble(2); }

        int seatCount = (int)Count(conn, "SELECT COUNT(*) FROM seats WHERE exam_id=@e", ("@e", examId));
        int totalEvents = (int)Count(conn, "SELECT COUNT(*) FROM events WHERE exam_id=@e", ("@e", examId));
        int suspTotal = (int)Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e", ("@e", examId));
        int confirmed = (int)Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='confirmed'", ("@e", examId));
        int dismissed = (int)Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='dismissed'", ("@e", examId));

        // suspicious_queue 全量:解析 refs → 被引用的 event/image/keystroke id;并收集已裁决记录
        var refEventIds = new HashSet<long>();
        var refImageIds = new HashSet<string>();
        var refKeystrokeIds = new HashSet<long>();
        var adjudications = new List<AdjRow>();
        using (SqliteCommand c = conn.Cmd(
            "SELECT id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note FROM suspicious_queue WHERE exam_id=@e", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
            while (r.Read())
            {
                string? refs = r.IsDBNull(6) ? null : r.GetString(6);
                ParseRefs(refs, refEventIds, refImageIds, refKeystrokeIds);
                string status = r.GetString(5);
                if (status is "confirmed" or "dismissed")
                    adjudications.Add(new AdjRow(
                        r.GetInt64(0), r.GetString(1), r.GetDouble(2), r.GetString(3), r.GetInt32(4), status,
                        refs, r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetDouble(8),
                        r.IsDBNull(9) ? null : r.GetString(9)));
            }

        // 关键事件 = 有效风险≥阈值 ∪ 被 refs 引用。一次拉全量事件在内存里筛(事件是小元数据)。
        var criticalEvents = new List<EventRow>();
        var criticalEvidenceImgIds = new HashSet<string>();
        using (SqliteCommand c = conn.Cmd(
            @"SELECT id,seat_id,agent_id,machine_id,seq,ts,type,payload,risk,COALESCE(server_risk,0),evidence_image_id,hash_prev,hash_self,sig
              FROM events WHERE exam_id=@e", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
            while (r.Read())
            {
                var ev = new EventRow(
                    r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetDouble(5),
                    r.GetString(6), r.IsDBNull(7) ? "{}" : r.GetString(7), r.GetInt32(8), r.GetInt32(9),
                    r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13));
                bool critical = Math.Max(ev.Risk, ev.ServerRisk) >= _cfg.ArchiveCriticalRisk || refEventIds.Contains(ev.Id);
                if (critical)
                {
                    criticalEvents.Add(ev);
                    if (ev.EvidenceImageId is not null) criticalEvidenceImgIds.Add(ev.EvidenceImageId);
                }
            }

        // 全部图片(用于清理决策)+ 关键图片 = is_evidence ∪ 被 refs 引用 ∪ 关键事件的 evidence_image_id
        var allImages = new List<ImageRow>();
        var evidenceFlag = new HashSet<string>();
        using (SqliteCommand c = conn.Cmd(
            @"SELECT image_id,seat_id,ts,trigger,phash,file_path,width,height,format,bytes,is_evidence
              FROM images WHERE exam_id=@e", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
            while (r.Read())
            {
                var im = new ImageRow(
                    r.GetString(0), r.GetString(1), r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9));
                allImages.Add(im);
                if (r.GetInt32(10) != 0) evidenceFlag.Add(im.ImageId);
            }

        var keptImageIds = new HashSet<string>(evidenceFlag);
        keptImageIds.UnionWith(refImageIds);
        keptImageIds.UnionWith(criticalEvidenceImgIds);
        var keptImages = allImages.Where(i => keptImageIds.Contains(i.ImageId))
                                  .ToDictionary(i => i.ImageId, i => i);
        // refs 可能引用已被删/不存在的 image → 只保留真实存在的
        keptImageIds.IntersectWith(keptImages.Keys);

        // 关键击键样本 = 被裁决 refs 引用(keystroke:N) ∪ risk>=阈值。confirmed 裁决的唯一证据常是击键时间线,
        // 必须随裁决归档,否则清理阶段删掉后定罪证据永久丢失(闭合审计 C1)。
        var keptKeystrokes = new List<KeystrokeRow>();
        using (SqliteCommand c = conn.Cmd(
            "SELECT id,seat_id,submission_id,ts,timeline,features,risk FROM keystroke_samples WHERE exam_id=@e", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
            while (r.Read())
            {
                long kid = r.GetInt64(0);
                int krisk = r.GetInt32(6);
                if (krisk >= _cfg.ArchiveCriticalRisk || refKeystrokeIds.Contains(kid))
                    keptKeystrokes.Add(new KeystrokeRow(
                        kid, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDouble(3),
                        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), krisk));
            }

        return new ExamSnapshot(name, started, ended, seatCount, totalEvents, suspTotal, confirmed, dismissed,
            criticalEvents, allImages, keptImageIds, keptImages, adjudications, keptKeystrokes);
    }

    // ================= 写 archive =================

    private void WriteArchive(SqliteConnection arc, SqliteTransaction tx, string examId, double now, ExamSnapshot s)
    {
        // 考试 + 汇总
        string summary = JsonSerializer.Serialize(new
        {
            seatCount = s.SeatCount, totalEvents = s.TotalEvents,
            criticalEvents = s.CriticalEvents.Count, keptImages = s.KeptImageIds.Count,
            keptKeystrokes = s.KeptKeystrokes.Count,
            suspiciousTotal = s.SuspTotal, confirmed = s.Confirmed, dismissed = s.Dismissed,
        });
        Exec(arc, tx,
            @"INSERT INTO archive_exams (exam_id,name,started_at,ended_at,archived_at,summary)
              VALUES (@e,@n,@st,@en,@at,@sum)
              ON CONFLICT(exam_id) DO UPDATE SET archived_at=@at, summary=@sum",
            ("@e", examId), ("@n", s.Name), ("@st", s.StartedAt), ("@en", s.EndedAt), ("@at", now), ("@sum", summary));

        // 关键事件(保留 hash_self/sig 锚点)
        foreach (EventRow ev in s.CriticalEvents)
            Exec(arc, tx,
                @"INSERT INTO archive_events (id,exam_id,seat_id,agent_id,machine_id,seq,ts,type,payload,risk,server_risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (@id,@e,@seat,@agent,@machine,@seq,@ts,@type,@payload,@risk,@srisk,@ev,@hp,@hs,@sig)
                  ON CONFLICT(id) DO NOTHING",
                // risk 存**原始 agent 自报值**(canonicalCore 签的正是它)——归档后凭 machine_id + 原始 risk + payload 可独立逐字节复算 hash_self。
                // server_risk 另存旁注:有效风险 max(risk,server_risk) 用于 LoadSnapshot 关键性判定但**不入锚点**(否则 server_risk>risk 时锚点不可复验);
                // 留 server_risk 列使归档库仍能查证"服务器为何判高危/多少分"(闭合 risk=0/server_risk=80 事件归档后无从溯源)。
                ("@id", ev.Id), ("@e", examId), ("@seat", ev.SeatId), ("@agent", ev.AgentId), ("@machine", ev.MachineId), ("@seq", ev.Seq),
                ("@ts", ev.Ts), ("@type", ev.Type), ("@payload", ev.Payload), ("@risk", ev.Risk), ("@srisk", ev.ServerRisk),
                ("@ev", ev.EvidenceImageId), ("@hp", ev.HashPrev), ("@hs", ev.HashSelf), ("@sig", ev.Sig));

        // 关键图片:移入冷存(改写 file_path)+ 其 OCR/Logo
        foreach (ImageRow im in s.KeptImages.Values)
        {
            string coldRel = _storage.MoveToArchive(im.FilePath, examId, im.SeatId, im.ImageId);
            Exec(arc, tx,
                @"INSERT INTO archive_images (image_id,exam_id,seat_id,ts,trigger,phash,file_path,width,height,format,bytes)
                  VALUES (@id,@e,@seat,@ts,@trg,@ph,@fp,@w,@h,@fmt,@by)
                  ON CONFLICT(image_id) DO NOTHING",
                ("@id", im.ImageId), ("@e", examId), ("@seat", im.SeatId), ("@ts", im.Ts), ("@trg", im.Trigger),
                ("@ph", im.Phash), ("@fp", coldRel), ("@w", im.Width), ("@h", im.Height), ("@fmt", im.Format), ("@by", im.Bytes));

            CopyOcr(arc, tx, im.ImageId);
            CopyLogo(arc, tx, im.ImageId);
        }

        // 裁决记录
        foreach (AdjRow a in s.Adjudications)
            Exec(arc, tx,
                @"INSERT INTO archive_adjudications (id,exam_id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note)
                  VALUES (@id,@e,@seat,@ts,@k,@sc,@st,@refs,@rv,@da,@note)
                  ON CONFLICT(id) DO NOTHING",
                ("@id", a.Id), ("@e", examId), ("@seat", a.SeatId), ("@ts", a.Ts), ("@k", a.Kind), ("@sc", a.Score),
                ("@st", a.Status), ("@refs", a.Refs), ("@rv", a.Reviewer), ("@da", a.DecidedAt), ("@note", a.Note));

        // 关键击键样本(随裁决归档 —— 定罪证据)
        foreach (KeystrokeRow k in s.KeptKeystrokes)
            Exec(arc, tx,
                @"INSERT INTO archive_keystroke_samples (id,exam_id,seat_id,submission_id,ts,timeline,features,risk)
                  VALUES (@id,@e,@seat,@sub,@ts,@tl,@ft,@risk)
                  ON CONFLICT(id) DO NOTHING",
                ("@id", k.Id), ("@e", examId), ("@seat", k.SeatId), ("@sub", k.SubmissionId), ("@ts", k.Ts),
                ("@tl", k.Timeline), ("@ft", k.Features), ("@risk", k.Risk));
    }

    /// 从 live 读取该图 OCR 结果并写入 archive(在 live 只读连接读、archive 写连接写)。
    private void CopyOcr(SqliteConnection arc, SqliteTransaction tx, string imageId)
    {
        (string eng, string? txt, string? hits, double? conf, double created)? row = _db.Read<(string, string?, string?, double?, double)?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "SELECT engine,text,hits,confidence,created_at FROM ocr_results WHERE image_id=@id", ("@id", imageId));
            using SqliteDataReader r = c.ExecuteReader();
            return r.Read()
                ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                   r.IsDBNull(3) ? null : r.GetDouble(3), r.GetDouble(4))
                : null;
        });
        if (row is null) return;
        Exec(arc, tx,
            @"INSERT INTO archive_ocr_results (image_id,engine,text,hits,confidence,created_at)
              VALUES (@id,@eng,@txt,@hits,@conf,@ct) ON CONFLICT(image_id) DO NOTHING",
            ("@id", imageId), ("@eng", row.Value.eng), ("@txt", row.Value.txt), ("@hits", row.Value.hits),
            ("@conf", row.Value.conf), ("@ct", row.Value.created));
    }

    private void CopyLogo(SqliteConnection arc, SqliteTransaction tx, string imageId)
    {
        var rows = _db.Read(conn =>
        {
            var list = new List<(long id, string label, double? score, string? bbox, double created)>();
            using SqliteCommand c = conn.Cmd(
                "SELECT id,label,score,bbox,created_at FROM logo_hits WHERE image_id=@id", ("@id", imageId));
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetDouble(2),
                          r.IsDBNull(3) ? null : r.GetString(3), r.GetDouble(4)));
            return list;
        });
        foreach (var lg in rows)
            Exec(arc, tx,
                @"INSERT INTO archive_logo_hits (id,image_id,label,score,bbox,created_at)
                  VALUES (@id,@im,@lb,@sc,@bb,@ct) ON CONFLICT(id) DO NOTHING",
                ("@id", lg.id), ("@im", imageId), ("@lb", lg.label), ("@sc", lg.score), ("@bb", lg.bbox), ("@ct", lg.created));
    }

    // ================= 小工具 =================

    private SqliteConnection OpenArchive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_archivePath)!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _archivePath,
            Pooling = false,
        }.ToString());
        conn.Open();
        using (SqliteCommand p = conn.CreateCommand())
        {
            p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            p.ExecuteNonQuery();
        }
        Schema.ApplyArchive(conn);
        return conn;
    }

    /// refs JSON 数组(如 ["event:12","image:img_ab","keystroke:7"])→ 拆出 event/image/keystroke id。
    private static void ParseRefs(string? refsJson, HashSet<long> eventIds, HashSet<string> imageIds, HashSet<long> keystrokeIds)
    {
        if (string.IsNullOrEmpty(refsJson)) return;
        try
        {
            using JsonDocument d = JsonDocument.Parse(refsJson);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement el in d.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                string s = el.GetString() ?? "";
                int colon = s.IndexOf(':');
                if (colon <= 0) continue;
                string kind = s[..colon], id = s[(colon + 1)..];
                if (kind == "event" && long.TryParse(id, out long eid)) eventIds.Add(eid);
                else if (kind == "image" && id.Length > 0) imageIds.Add(id);
                else if (kind == "keystroke" && long.TryParse(id, out long kid)) keystrokeIds.Add(kid);
            }
        }
        catch { /* 坏 refs 忽略 */ }
    }

    private static int Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object?)[] ps)
    {
        using SqliteCommand c = conn.Cmd(sql, ps);
        c.Transaction = tx;
        return c.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection conn, string sql, params (string, object?)[] ps)
    {
        using SqliteCommand c = conn.Cmd(sql, ps);
        object? v = c.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
