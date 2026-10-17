using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Analysis.Vision;

/// 非同步視覺分析後台服務:圖入庫後入隊,後台執行緒逐張送 IVisionAnalyzer 判定 —— **不佔 ingest 熱路徑**
/// (視覺呼叫可能幾秒)。判定命中 → 落 ocr_results + 該圖標證據 + 引用它的事件抬 server_risk + 入可疑佇列。
/// 未設定分析器(cfg 關)時整鏈 no-op,採集完全不受影響。
///
/// **不丟證據**:入隊用**原子佔位**(uploaded_to_ocr 0→1 搶到才分析)杜絕重複分析/重複入隊;佇列滿 / 伺服器重新啟動丟記憶體佇列時,
/// **補償重掃**(VisionBackstopMinutes)週期性拾回 uploaded_to_ocr=0 的觸發型證據圖重新入隊 —— 觸發型證據不會被靜默丟分析。
public sealed class VisionAnalysisService : BackgroundService
{
    private sealed record ImgMeta(string Exam, string Seat, string Trigger, string File);

    private readonly Db _db;
    private readonly Storage _storage;
    private readonly ServerConfig _cfg;
    private readonly ILogger<VisionAnalysisService> _log;
    private readonly IVisionAnalyzer? _analyzer;
    private readonly Channel<string> _queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    // 在途去重:已入隊尚未分析完的 imageId。防 ingest 首送與 backstop 反覆把同一張仍在佇列裡的圖重複入隊(佔滿有界佇列、
    // 把新圖擠掉)。原子佔位(uploaded_to_ocr 0→1)保證不重複**分析**,此集合保證不重複**入隊**。
    private readonly ConcurrentDictionary<string, byte> _inflight = new();
    private long _rejected;   // 佇列滿時被拒的入隊數(補償重掃會拾回);週期性告警

    public VisionAnalysisService(Db db, Storage storage, ServerConfig cfg, IServiceProvider sp, ILogger<VisionAnalysisService> log)
    {
        _db = db; _storage = storage; _cfg = cfg; _log = log;
        _analyzer = sp.GetService<IVisionAnalyzer>();   // 視覺關時未註冊 → null → 整鏈 no-op
    }

    public bool Enabled => _analyzer is not null;

    /// 入隊待分析圖(§5 最小化:只送需要文字/語義判定的圖)。視覺關或空 id → no-op。
    /// 佇列滿時 TryWrite 回傳 false(不阻塞 ingest 熱路徑):計數告警,由補償重掃拾回,不靜默丟證據。
    public void Enqueue(string imageId)
    {
        if (_analyzer is null || string.IsNullOrEmpty(imageId)) return;
        if (!_inflight.TryAdd(imageId, 0)) return;   // 已在佇列/分析中 → 不重複入隊
        if (!_queue.Writer.TryWrite(imageId)) { _inflight.TryRemove(imageId, out _); Interlocked.Increment(ref _rejected); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_analyzer is null) return;
        // 效能#1:有限並行。預設 concurrency=1 完全保持原串行語義(不壓視覺端點);>1 時一次批量拉取 N 張並行送端點,
        // 縮短單場大批量等待。各圖分析相互獨立(原子認領 + 入隊去重保證不重複分析/入隊),並行不破壞正確性。
        int concurrency = Math.Clamp(_cfg.VisionConcurrency, 1, 8);
        _log.LogInformation("视觉分析已启用 engine={Engine} 阈值={Th} 并发={C}", _analyzer.Engine, _cfg.VisionConfidenceThreshold, concurrency);
        Task backstop = RunBackstopAsync(ct);
        try
        {
            var batch = new List<string>(concurrency);
            while (await _queue.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < concurrency && _queue.Reader.TryRead(out string? id) && id is not null) batch.Add(id);
                if (batch.Count == 0) break;
                await Task.WhenAll(batch.Select(id => AnalyzeWrappedAsync(id, ct)));
            }
        }
        catch (OperationCanceledException) { /* 停機 */ }
        try { await backstop; } catch { /* 停機 */ }
    }

    /// 單圖分析包裝:統一異常備援 + 出隊釋放在途標記(佔位仍防重複分析)。
    private async Task AnalyzeWrappedAsync(string imageId, CancellationToken ct)
    {
        try { await AnalyzeOneAsync(imageId, ct); }
        catch (OperationCanceledException) { /* 取消:外層循環將退出 */ }
        catch (Exception ex) { _log.LogError(ex, "视觉分析异常 image={Image}", imageId); }
        finally { _inflight.TryRemove(imageId, out _); }
    }

    /// 補償重掃:週期性拾回 uploaded_to_ocr=0 的**觸發型**證據圖(被佇列拒收 / 伺服器重新啟動丟記憶體佇列的)重新入隊。
    /// 原子佔位保證重入隊冪等(正在分析的會佔位失敗,不重複分析)。VisionBackstopMinutes≤0 關閉。
    private async Task RunBackstopAsync(CancellationToken ct)
    {
        if (_cfg.VisionBackstopMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_cfg.VisionBackstopMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long dropped = Interlocked.Exchange(ref _rejected, 0);
                if (dropped > 0) _log.LogWarning("视觉队列曾满,{N} 张图被拒入队(将由补偿重扫拾回)", dropped);

                // trigger 過濾須與 ImageIngest 的入隊判據一致:開了基線分析則重掃也納入基線圖(否則被拒/丟隊的基線圖永不拾回,
                // _rejected 告警對基線成虛假承諾);預設只重掃觸發型證據圖(§5 最小化)。
                string triggerFilter = _cfg.VisionAnalyzeBaseline ? "" : " AND i.trigger LIKE 'event:%'";
                var ids = _db.Read(conn =>
                {
                    var list = new List<string>();
                    // 拾回 analysis_state=0(未終結:臨時雲失敗/被拒隊/重新啟動丟隊)且 attempts 未超上限的圖(閉合 F1)。
                    // 排除歸檔中/已歸檔考試的圖(不給歸檔視窗塞新分析結果 → 避免孤兒 pending)。
                    using SqliteCommand c = conn.Cmd(
                        @"SELECT i.image_id FROM images i JOIN exams e ON i.exam_id=e.exam_id
                          WHERE i.analysis_state=0 AND i.analysis_attempts<@max" + triggerFilter + @"
                            AND e.status NOT IN ('archiving','archived')
                          ORDER BY i.recv_ts LIMIT 500", ("@max", _cfg.VisionMaxAttempts));
                    using SqliteDataReader r = c.ExecuteReader();
                    while (r.Read()) list.Add(r.GetString(0));
                    return list;
                });
                int requeued = 0;
                foreach (string id in ids)
                {
                    if (!_inflight.TryAdd(id, 0)) continue;                 // 仍在佇列/分析中 → 不重複入隊
                    if (_queue.Writer.TryWrite(id)) requeued++;
                    else { _inflight.TryRemove(id, out _); break; }         // 滿了下輪再拾
                }
                if (requeued > 0) _log.LogInformation("视觉补偿重扫:拾回 {N} 张未分析触发型证据图", requeued);
            }
        }
        catch (OperationCanceledException) { /* 停機 */ }
    }

    private async Task AnalyzeOneAsync(string imageId, CancellationToken ct)
    {
        // **原子認領**:取中繼資料 → 查考試未封存 → UPDATE analysis_attempts+1 搶到(rowcount=1·attempts<上限·未終結)才分析 —— 同一寫鎖 body 內,
        // 杜絕"讀→分析→寫"跨 await 的 TOCTOU(重複分析 / 重複入可疑佇列),並對歸檔中/已歸檔考試短路(不塞孤兒分析)。
        // **認領不置 uploaded_to_ocr**(那是"真出網"隱私稽核,僅 SendsOffNetwork 分析器真送出才置)——閂鎖改用 analysis_state/attempts,
        // 使臨時雲失敗(v=null 不終結)可由補償重掃按 attempts<上限 重試,不再永久漏析(閉合 F1/F2)。
        ImgMeta? meta = _db.Write<ImgMeta?>(conn =>
        {
            ImgMeta? m = null;
            using (SqliteCommand c = conn.Cmd(
                "SELECT exam_id,seat_id,trigger,file_path FROM images WHERE image_id=@id", ("@id", imageId)))
            using (SqliteDataReader r = c.ExecuteReader())
                if (r.Read()) m = new ImgMeta(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
            if (m is null || conn.IsExamSealed(m.Exam)) return null;
            using SqliteCommand claim = conn.Cmd(
                "UPDATE images SET analysis_attempts=analysis_attempts+1 WHERE image_id=@id AND analysis_state=0 AND analysis_attempts<@max",
                ("@id", imageId), ("@max", _cfg.VisionMaxAttempts));
            return claim.ExecuteNonQuery() > 0 ? m : null;   // 搶到才分析;搶不到=已終結/佔用/超上限
        });
        if (meta is null) return;

        string? full = _storage.Resolve(meta.File);
        if (full is null || !File.Exists(full)) { MarkTerminal(imageId); return; }   // 檔案已歸檔/清理:確定態 → 終結,不重掃
        byte[] original = await File.ReadAllBytesAsync(full, ct);

        // §5 送雲前降取樣 + 剝離中繼資料,只送派生位元組(原圖位元組唯讀、永不出網)。
        // 聯網分析器(SendsOffNetwork=true):無法安全派生(解碼失敗)→ Prepare 返 null → 終結跳過(確定態·重掃也解不開),絕不洩未剝中繼資料的原圖。
        byte[]? derived = VisionImagePrep.Prepare(original, _cfg, _analyzer!.SendsOffNetwork);
        if (derived is null)
        {
            _log.LogWarning("送云图无法安全派生(解码失败),跳过分析以免泄原图 image={Image}", imageId);
            MarkTerminal(imageId);   // 派生失敗是確定態(壞圖每次都解不開)→ 終結,免補償重掃死循環
            return;
        }

        // 隱私稽核:僅**聯網分析器**在真正送出位元組前置 uploaded_to_ocr=1(mock/本機不出網恆 0,不再撒謊)。
        if (_analyzer!.SendsOffNetwork)
            _db.Write(conn => { using SqliteCommand u = conn.Cmd(
                "UPDATE images SET uploaded_to_ocr=1 WHERE image_id=@id", ("@id", imageId)); u.ExecuteNonQuery(); });

        VisionVerdict? v = await _analyzer!.AnalyzeAsync(
            derived, new VisionContext(meta.Exam, meta.Seat, imageId, meta.Trigger), ct);
        if (v is null)
        {
            // 臨時失敗(超時/5xx/網路/空 content/解析失敗):未達上限**不終結**(analysis_state 保持 0)→ 補償重掃按 attempts<上限 重試(閉合 F1)。
            // 已達上限:置終態(state=1)放棄 —— 否則永久卡 state=0 殭屍態(claim/backstop 都按 attempts<上限 過濾,既不重試也不終結)。
            // 與確定態失敗一致:終態但無 ocr_results 行 → 看板/稽核可據"state=1 且無 ocr_results"區分"放棄"與"已析乾淨"。
            int abandoned = _db.Write(conn =>
            {
                using SqliteCommand u = conn.Cmd(
                    "UPDATE images SET analysis_state=1 WHERE image_id=@id AND analysis_state=0 AND analysis_attempts>=@max",
                    ("@id", imageId), ("@max", _cfg.VisionMaxAttempts));
                return u.ExecuteNonQuery();
            });
            if (abandoned > 0)
                _log.LogWarning("视觉分析连续 {N} 次临时失败,放弃 image={Image}(仍留作证据·无视觉判定)", _cfg.VisionMaxAttempts, imageId);
            else
                _log.LogWarning("视觉分析未得结果(临时失败),保留待补偿重扫 image={Image}", imageId);
            return;
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _db.Write(conn =>
        {
            // 分析期間(await 雲呼叫)考試可能已進入歸檔:此刻圖行可能已被刪,寫結果會 FK 失敗 / 造孤兒 pending → 短路(不終結,歸檔已接管)。
            if (conn.IsExamSealed(meta.Exam)) return;

            // 得到判定 → 終結(成功態,不再重掃)。
            using (SqliteCommand fin = conn.Cmd("UPDATE images SET analysis_state=1 WHERE image_id=@id", ("@id", imageId)))
                fin.ExecuteNonQuery();

            // 落 ocr_results(engine/text/hits/confidence);同圖冪等
            using (SqliteCommand ins = conn.Cmd(
                @"INSERT INTO ocr_results (image_id,engine,text,hits,confidence,created_at)
                  VALUES (@id,@eng,@txt,@hits,@conf,@ts) ON CONFLICT(image_id) DO NOTHING",
                ("@id", imageId), ("@eng", _analyzer!.Engine), ("@txt", v.Text),
                ("@hits", JsonSerializer.Serialize(v.Hits)), ("@conf", (double)v.Confidence), ("@ts", now)))
                ins.ExecuteNonQuery();

            if (!v.Suspicious || v.Confidence < _cfg.VisionConfidenceThreshold) return;

            // 命中 → 該圖標證據;引用它的觸發型事件抬 server_risk(與中繼資料信號取 max);入可疑佇列
            using (SqliteCommand ev = conn.Cmd("UPDATE images SET is_evidence=1 WHERE image_id=@id", ("@id", imageId)))
                ev.ExecuteNonQuery();

            long? eventId = null;
            using (SqliteCommand fe = conn.Cmd("SELECT id FROM events WHERE evidence_image_id=@id LIMIT 1", ("@id", imageId)))
            {
                object? o = fe.ExecuteScalar();
                if (o is not null and not DBNull) eventId = Convert.ToInt64(o);
            }
            if (eventId is not null)
                using (SqliteCommand br = conn.Cmd(
                    "UPDATE events SET server_risk=MAX(COALESCE(server_risk,0),@c) WHERE id=@eid",
                    ("@c", v.Confidence), ("@eid", eventId.Value)))
                    br.ExecuteNonQuery();

            // F4 去重:若該事件已有 pending 佇列項(ingest 期中繼資料信號入的),不再另插一條視覺重複項——
            // 而是把視覺證據併入該行 note 並把 score 抬到 max(避免同一事同圖兩條 pending、人工裁決兩次、看板計數翻倍)。
            if (eventId is not null)
            {
                long? existing = null;
                using (SqliteCommand fx = conn.Cmd(
                    @"SELECT id FROM suspicious_queue WHERE exam_id=@e AND status='pending' AND refs LIKE @pat LIMIT 1",
                    ("@e", meta.Exam), ("@pat", $"%\"event:{eventId.Value}\"%")))
                {
                    object? o = fx.ExecuteScalar();
                    if (o is not null and not DBNull) existing = Convert.ToInt64(o);
                }
                if (existing is not null)
                {
                    using SqliteCommand up = conn.Cmd(
                        @"UPDATE suspicious_queue SET score=MAX(score,@sc), note=COALESCE(note,'')||@vn WHERE id=@id",
                        ("@sc", v.Confidence), ("@vn", " | vision:" + v.Evidence), ("@id", existing.Value));
                    up.ExecuteNonQuery();
                    return;   // 已合併進既有 pending,不另插
                }
            }

            var refs = new List<string> { $"image:{imageId}" };
            if (eventId is not null) refs.Add($"event:{eventId.Value}");
            using (SqliteCommand q = conn.Cmd(
                @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs,note)
                  VALUES (@e,@s,@ts,@k,@sc,'pending',@refs,@note)",
                ("@e", meta.Exam), ("@s", meta.Seat), ("@ts", now),
                ("@k", v.Kind()), ("@sc", v.Confidence), ("@refs", JsonSerializer.Serialize(refs)),
                ("@note", "vision:" + v.Evidence)))
                q.ExecuteNonQuery();
        });
    }

    /// 把圖標為已終結(analysis_state=1):確定態失敗(檔案缺失 / 派生失敗)不由補償重掃重試。
    private void MarkTerminal(string imageId)
        => _db.Write(conn => { using SqliteCommand c = conn.Cmd(
            "UPDATE images SET analysis_state=1 WHERE image_id=@id", ("@id", imageId)); c.ExecuteNonQuery(); });
}
