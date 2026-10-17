using System.Text.Json;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Jobs;

/// M3 歸檔 / 清理作業(architecture §13/§15)。每日(預設每 6h)掃描**結束超留存期(預設 30 天)**的考試:
///   1. 有 pending 未裁決 → **跳過**(不 purge 未決案的證據,留待人工。誠實:寧佔空間也不丟證據)。
///   2. 關鍵資料遷入 **archive 庫**:關鍵事件(有效風險≥閾值 或 被 suspicious_queue 引用)、其證據圖(移入冷存·改寫路徑)
///      + OCR/Logo 結果、裁決記錄(confirmed/dismissed)、考試中繼資料 + 彙總、每條關鍵事件的 hash_self/sig 錨點。
///   3. **就地清理** live:該考試全部事件 / 圖片(非關鍵圖刪檔案)/ OCR/Logo / 擊鍵 / 心跳 / 可疑佇列,
///      exams.status='archived'(留作墓碑);全部完成後 VACUUM 回收空間。
///
/// **冪等**:copy 用 INSERT OR IGNORE、檔案移動容忍已遷;若 copy 後崩潰、delete 前重跑,考試仍 'ended'、pending=0,
/// 會重新 copy(無副作用)+ 重新 delete → 收斂。證據在 delete 前已安全落 archive,故任何中斷都不丟關鍵資料。
/// 完整性說明(§13.2):清理非關鍵事件使原始整鏈斷裂 —— archive 保留每條關鍵事件的 hash_self/sig 作**獨立錨點**,
/// 複驗以"單條事件 ↔ 其 hash_self"為準,不再相依整鏈連續。
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
    private readonly object _runGate = new();   // 後台 timer 與手動觸發不併跑

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
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }   // 讓伺服器起穩再首掃
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

    /// 掃描並歸檔所有到齡考試。回傳本次報告。可由後台 timer 或維運端點(手動)呼叫;內部串行化。
    public Report RunOnce(double now, CancellationToken ct = default)
    {
        lock (_runGate)
        {
            double cutoff = now - _cfg.RetentionDays * 86400.0;
            var candidates = _db.Read(conn =>
            {
                var list = new List<string>();
                // 含 'archiving':上次崩在歸檔中途的考試(墓碑態)本次續跑收斂。
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
        // ---- 門禁 + 墓碑(原子):pending 檢查與 status→'archiving' 翻轉在**同一寫鎖 body** 內完成。
        // _db.Write 全程持 _writeGate,其它寫者(含 vision)不能交錯 → pending 檢查後到翻轉前無新 pending 混入。
        // 翻轉後所有 ingest 對本考試短路(IsExamSealed),消除"讀快照→DELETE WHERE exam_id"之間的 late-ingest 視窗。
        string gate = _db.Write(conn =>
        {
            long pending = Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='pending'", ("@e", examId));
            if (pending > 0) return "pending:" + pending;   // 不翻轉,留 'ended' 待人工裁決
            using SqliteCommand up = conn.Cmd(
                "UPDATE exams SET status='archiving' WHERE exam_id=@e AND status IN ('ended','archiving')", ("@e", examId));
            return up.ExecuteNonQuery() > 0 ? "ok" : "gone";   // gone = 已被並行處理 / 非 ended
        });
        if (gate.StartsWith("pending:", StringComparison.Ordinal))
        {
            long n = long.Parse(gate.AsSpan("pending:".Length));
            _log.LogWarning("考试 {Exam} 仍有 {N} 条待裁决,跳过归档(须先人工裁决)", examId, n);
            return new ExamResult(examId, "skipped", 0, 0, 0, 0, 0, $"pending={n}");
        }
        if (gate != "ok")
            return new ExamResult(examId, "skipped", 0, 0, 0, 0, 0, "not_ended");

        // ---- 讀取階段:確定關鍵集合 + 載入待複製資料(此刻 status='archiving',ingest 已短路,快照穩定)----
        ExamSnapshot snap = _db.Read(conn => LoadSnapshot(conn, examId));

        // ---- 複製階段:寫入 archive 庫(獨立檔案 + 交易)----
        using (var arc = OpenArchive())
        using (SqliteTransaction tx = arc.BeginTransaction())
        {
            WriteArchive(arc, tx, examId, now, snap);
            tx.Commit();
        }

        // ---- 清理階段(檔案先刪非關鍵,再 live DB 交易性刪除 + 置 archived)----
        foreach (ImageRow img in snap.AllImages)
            if (!snap.KeptImageIds.Contains(img.ImageId))
                _storage.DeleteLive(img.FilePath);

        int delEvents = 0, delImages = 0;
        _db.Write(conn =>
        {
            using SqliteTransaction tx = conn.BeginTransaction();
            // 子表先刪(images 被 ocr_results/logo_hits 以 FK 引用)
            Exec(conn, tx, "DELETE FROM logo_hits   WHERE image_id IN (SELECT image_id FROM images WHERE exam_id=@e)", ("@e", examId));
            Exec(conn, tx, "DELETE FROM ocr_results WHERE image_id IN (SELECT image_id FROM images WHERE exam_id=@e)", ("@e", examId));
            // image_embeddings 無 FK 到 images(裸 PK 表·派生檢索索引非取證資料不歸檔),須顯式清理,否則向量行懸掛指向已刪圖、永久滯留 live 庫。
            Exec(conn, tx, "DELETE FROM image_embeddings WHERE image_id IN (SELECT image_id FROM images WHERE exam_id=@e)", ("@e", examId));
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

    // ================= 讀取快照 =================

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
        // 考試中繼資料 + 彙總計數
        string? name = null; double? started = null, ended = null;
        using (SqliteCommand c = conn.Cmd("SELECT name,started_at,ended_at FROM exams WHERE exam_id=@e", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
            if (r.Read()) { name = r.GetString(0); started = r.IsDBNull(1) ? null : r.GetDouble(1); ended = r.IsDBNull(2) ? null : r.GetDouble(2); }

        int seatCount = (int)Count(conn, "SELECT COUNT(*) FROM seats WHERE exam_id=@e", ("@e", examId));
        int totalEvents = (int)Count(conn, "SELECT COUNT(*) FROM events WHERE exam_id=@e", ("@e", examId));
        int suspTotal = (int)Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e", ("@e", examId));
        int confirmed = (int)Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='confirmed'", ("@e", examId));
        int dismissed = (int)Count(conn, "SELECT COUNT(*) FROM suspicious_queue WHERE exam_id=@e AND status='dismissed'", ("@e", examId));

        // suspicious_queue 全量:解析 refs → 被引用的 event/image/keystroke id;並收集已裁決記錄
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

        // 關鍵事件 = 有效風險≥閾值 ∪ 被 refs 引用。一次拉全量事件在記憶體裡篩(事件是小中繼資料)。
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

        // 全部圖片(用於清理決策)+ 關鍵圖片 = is_evidence ∪ 被 refs 引用 ∪ 關鍵事件的 evidence_image_id
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
        // refs 可能引用已被刪/不存在的 image → 只保留真實存在的
        keptImageIds.IntersectWith(keptImages.Keys);

        // 關鍵擊鍵樣本 = 被裁決 refs 引用(keystroke:N) ∪ risk>=閾值。confirmed 裁決的唯一證據常是擊鍵時間線,
        // 必須隨裁決歸檔,否則清理階段刪掉後定罪證據永久丟失(閉合稽核 C1)。
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

    // ================= 寫 archive =================

    private void WriteArchive(SqliteConnection arc, SqliteTransaction tx, string examId, double now, ExamSnapshot s)
    {
        // 考試 + 彙總
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

        // 關鍵事件(保留 hash_self/sig 錨點)
        foreach (EventRow ev in s.CriticalEvents)
            Exec(arc, tx,
                @"INSERT INTO archive_events (id,exam_id,seat_id,agent_id,machine_id,seq,ts,type,payload,risk,server_risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (@id,@e,@seat,@agent,@machine,@seq,@ts,@type,@payload,@risk,@srisk,@ev,@hp,@hs,@sig)
                  ON CONFLICT(id) DO NOTHING",
                // risk 存**原始 agent 自報值**(canonicalCore 籤的正是它)——歸檔後憑 machine_id + 原始 risk + payload 可獨立逐位元組復算 hash_self。
                // server_risk 另存旁註:有效風險 max(risk,server_risk) 用於 LoadSnapshot 關鍵性判定但**不入錨點**(否則 server_risk>risk 時錨點不可複驗);
                // 留 server_risk 列使歸檔庫仍能查證"伺服器為何判高危/多少分"(閉合 risk=0/server_risk=80 事件歸檔後無從溯源)。
                ("@id", ev.Id), ("@e", examId), ("@seat", ev.SeatId), ("@agent", ev.AgentId), ("@machine", ev.MachineId), ("@seq", ev.Seq),
                ("@ts", ev.Ts), ("@type", ev.Type), ("@payload", ev.Payload), ("@risk", ev.Risk), ("@srisk", ev.ServerRisk),
                ("@ev", ev.EvidenceImageId), ("@hp", ev.HashPrev), ("@hs", ev.HashSelf), ("@sig", ev.Sig));

        // 關鍵圖片:移入冷存(改寫 file_path)+ 其 OCR/Logo
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

        // 裁決記錄
        foreach (AdjRow a in s.Adjudications)
            Exec(arc, tx,
                @"INSERT INTO archive_adjudications (id,exam_id,seat_id,ts,kind,score,status,refs,reviewer,decided_at,note)
                  VALUES (@id,@e,@seat,@ts,@k,@sc,@st,@refs,@rv,@da,@note)
                  ON CONFLICT(id) DO NOTHING",
                ("@id", a.Id), ("@e", examId), ("@seat", a.SeatId), ("@ts", a.Ts), ("@k", a.Kind), ("@sc", a.Score),
                ("@st", a.Status), ("@refs", a.Refs), ("@rv", a.Reviewer), ("@da", a.DecidedAt), ("@note", a.Note));

        // 關鍵擊鍵樣本(隨裁決歸檔 —— 定罪證據)
        foreach (KeystrokeRow k in s.KeptKeystrokes)
            Exec(arc, tx,
                @"INSERT INTO archive_keystroke_samples (id,exam_id,seat_id,submission_id,ts,timeline,features,risk)
                  VALUES (@id,@e,@seat,@sub,@ts,@tl,@ft,@risk)
                  ON CONFLICT(id) DO NOTHING",
                ("@id", k.Id), ("@e", examId), ("@seat", k.SeatId), ("@sub", k.SubmissionId), ("@ts", k.Ts),
                ("@tl", k.Timeline), ("@ft", k.Features), ("@risk", k.Risk));
    }

    /// 從 live 讀取該圖 OCR 結果並寫入 archive(在 live 唯讀連接讀、archive 寫連接寫)。
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

    /// refs JSON 陣列(如 ["event:12","image:img_ab","keystroke:7"])→ 拆出 event/image/keystroke id。
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
        catch { /* 壞 refs 忽略 */ }
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
