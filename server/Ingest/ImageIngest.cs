using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 圖片通道(HTTP POST /ingest/images)。見 api-contract §2.1。
/// 檢核 X-Horus-Sig;pHash 去重;原圖存盤;DB 存指針;新圖入隊非同步視覺分析(L2)。
/// M4:驗籤金鑰可為 PSK 或 OIDC 工作階段 K_sess(X-Horus-Session);OIDC 路徑強制頭部身分==工作階段身分。
public sealed class ImageIngest(Db db, Storage storage, ServerConfig cfg, VisionAnalysisService vision, SessionStore sessions, ILogger<ImageIngest> log)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        HttpRequest req = ctx.Request;
        string examId = req.Headers["X-Horus-Exam"].ToString();
        string seatId = req.Headers["X-Horus-Seat"].ToString();
        string agentId = req.Headers["X-Horus-Agent"].ToString();
        string seqStr = req.Headers["X-Horus-Seq"].ToString();
        string trigger = req.Headers["X-Horus-Trigger"].ToString();
        string phash = req.Headers["X-Horus-Phash"].ToString();
        string tsStr = req.Headers["X-Horus-Ts"].ToString();
        string sig = req.Headers["X-Horus-Sig"].ToString();
        string clientId = req.Headers["X-Horus-Image-Id"].ToString();   // 用戶端預生成 id(納入簽名)

        if (string.IsNullOrEmpty(examId) || string.IsNullOrEmpty(seatId) || string.IsNullOrEmpty(agentId))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "missing_headers" });
            return;
        }
        if (clientId.Length > 0 && !IsValidImageId(clientId))   // 畸形 client id 先拒(驗籤前),收斂 canonical 輸入
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad_image_id" });
            return;
        }

        // 顯式上限(獨立於 Kestrel 全域 2MB):未鑑權者觸發的緩衝放大也受約束;超限在分配 MemoryStream 前按
        // Content-Length 拒(=BUG#1 修復 + 與 KeystrokeIngest 一致 · 品質#5 共用 helper)。
        const int MaxImageBodyBytes = 2 * 1024 * 1024;
        var (body, statusCode, err) = await IngestBody.ReadWithLimitAsync(req, MaxImageBodyBytes, allowEmpty: false);
        if (body is null)
        {
            ctx.Response.StatusCode = statusCode!.Value;
            await ctx.Response.WriteAsJsonAsync(new { error = err });
            return;
        }

        long.TryParse(seqStr, out long seq);
        double.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture, out double ts);

        // M4:解析鑑權(PSK ↔ OIDC 工作階段)。OIDC 路徑下頭部身分須 == 工作階段身分(否則拒·閉合 A1)。
        double authNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        IngestAuth.Resolved auth = IngestAuth.Resolve(cfg, sessions, req.Headers["X-Horus-Session"].ToString(), examId, seatId, agentId, authNow);
        if (!auth.Ok)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = auth.Error });
            return;
        }

        // 驗籤(見 §2.1):HMAC(key, canonicalHeaders + "\n" + sha256(body));key = PSK 或工作階段 K_sess;canonical 含 imageId 防篡改
        if (auth.Key is not null)
        {
            string canon = Auth.ImageCanonicalHeaders(examId, seatId, agentId, seq, trigger, phash, tsStr, clientId);
            string want = Auth.ImageSig(auth.Key, canon, body);
            if (string.IsNullOrEmpty(sig) || !Crypto.FixedTimeEquals(sig, want))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "bad_sig" });
                log.LogWarning("图片验签失败 seat={Seat} seq={Seq}", seatId, seq);
                return;
            }
        }

        // 歸檔中/已歸檔考試:短路不寫入資料庫(避免歸檔視窗 late-ingest 被無錨點刪 + 歸檔後重建孤兒圖行)。
        if (db.Read(conn => conn.IsExamSealed(examId)))
        {
            await ctx.Response.WriteAsJsonAsync(new { stored = false, error = "exam_sealed" });
            return;
        }

        // 用戶端預生成 imageId(觸發型抓圖):冪等沿用,以保證"事件 ↔ 證據圖"關聯跨斷線不斷。
        bool hasClientId = IsValidImageId(clientId);

        string imageId;
        if (hasClientId)
        {
            bool exists = db.Locked(conn =>
            {
                using SqliteCommand c = conn.Cmd("SELECT 1 FROM images WHERE image_id=@id LIMIT 1", ("@id", clientId));
                return c.ExecuteScalar() is not null;
            });
            if (exists)   // 重傳(續傳)→ 冪等,不另存
            {
                await ctx.Response.WriteAsJsonAsync(new { stored = false, imageId = clientId, duplicate = true, ocrQueued = false });
                return;
            }
            imageId = clientId;   // 沿用用戶端 id;**跳過 pHash 去重**以尊重事件關聯
        }
        else
        {
            // 無用戶端 id(如 baseline):pHash 去重 + 伺服器分配
            if (cfg.DedupImagesByPhash && !string.IsNullOrEmpty(phash))
            {
                string? dupId = db.Locked(conn =>
                {
                    using SqliteCommand c = conn.Cmd(
                        "SELECT image_id FROM images WHERE exam_id=@e AND seat_id=@s AND phash=@p LIMIT 1",
                        ("@e", examId), ("@s", seatId), ("@p", phash));
                    return c.ExecuteScalar() as string;
                });
                if (dupId is not null)
                {
                    await ctx.Response.WriteAsJsonAsync(new { stored = false, imageId = dupId, duplicate = true, ocrQueued = false });
                    return;
                }
            }
            imageId = "img_" + Guid.NewGuid().ToString("N");
        }

        string relPath = await storage.SaveWebpAsync(examId, seatId, imageId, body);
        double recvTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        // §5 視覺分析判據:觸發型必送;隨機基線僅當開了基線分析**且命中抽樣**(1/N·確定性按 imageId)才送。
        bool isEvent = trigger.StartsWith("event:", StringComparison.Ordinal);
        bool analyze = vision.Enabled && (isEvent || (cfg.VisionAnalyzeBaseline && BaselineSampleHit(imageId, cfg.VisionBaselineSampleRate)));
        // 開了基線分析但本張未抽中 → 寫入資料庫即標 analysis_state=1 終結,補償重掃不再拾回(否則每輪重掃都會翻檢未抽中基線)。
        bool baselineSampledOut = vision.Enabled && !isEvent && cfg.VisionAnalyzeBaseline && !analyze;

        bool sealedInLock = db.Locked(conn =>
        {
            // late-ingest 隔離:上方 :70 的 sealed 預檢在**唯讀連接**(快速拒絕),此處在**寫鎖交易內**權威複檢——
            // 預檢通過後、拿寫鎖前歸檔作業可能已翻轉 archiving 並清理該考試,若此處不查會把孤兒圖行插進已歸檔考試
            // (既不再歸檔也不清理)。與 EventIngest/KeystrokeIngest 的鎖內檢查對齊。
            if (conn.IsExamSealed(examId)) return true;

            using (SqliteCommand c = conn.Cmd(
                @"INSERT INTO images (image_id,exam_id,seat_id,agent_id,ts,recv_ts,trigger,phash,file_path,format,bytes,uploaded_to_ocr,is_evidence,analysis_state)
                  VALUES (@id,@e,@s,@a,@ts,@recv,@trig,@ph,@fp,'webp',@bytes,0,0,@astate)
                  ON CONFLICT(image_id) DO NOTHING",
                ("@id", imageId), ("@e", examId), ("@s", seatId), ("@a", agentId),
                ("@ts", ts), ("@recv", recvTs), ("@trig", trigger), ("@ph", phash),
                ("@fp", relPath), ("@bytes", (long)body.Length), ("@astate", baselineSampledOut ? 1 : 0)))
                c.ExecuteNonQuery();

            // 反向補標:僅觸發型抓圖(有用戶端 id)才可能被事件引用;baseline 圖跳過,免每張全表查 events。
            if (hasClientId)
            {
                using SqliteCommand mk = conn.Cmd(
                    "UPDATE images SET is_evidence=1 WHERE image_id=@id AND EXISTS (SELECT 1 FROM events WHERE evidence_image_id=@id)",
                    ("@id", imageId));
                mk.ExecuteNonQuery();
            }
            return false;
        });

        if (sealedInLock)   // 寫鎖內發現考試已封存:回復已寫入磁碟的孤兒 webp(DB 行未插),按 exam_sealed 拒絕
        {
            storage.DeleteLive(relPath);
            await ctx.Response.WriteAsJsonAsync(new { stored = false, error = "exam_sealed" });
            return;
        }

        // 送非同步視覺分析(§5 最小化:觸發型必送;隨機基線按抽樣)。視覺關時 no-op、不佔本請求延遲。
        if (analyze) vision.Enqueue(imageId);

        await ctx.Response.WriteAsJsonAsync(new { stored = true, imageId, duplicate = false, ocrQueued = analyze });
    }

    /// 基線抽樣命中判據:1/N 確定性抽樣(N≤1 恆真=全分析)。用**穩定 FNV-1a 雜湊**(非 string.GetHashCode·
    /// 後者處理程序內隨機化,重新啟動後同一圖抽樣結果會翻轉 → 補償重掃與入隊判據不一致)保證跨重新啟動一致。
    internal static bool BaselineSampleHit(string imageId, int sampleRate)
    {
        if (sampleRate <= 1) return true;
        uint h = 2166136261u;
        foreach (char ch in imageId) { h ^= ch; h *= 16777619u; }
        return h % (uint)sampleRate == 0;
    }

    /// 用戶端 id 格式檢核(防路徑注入):img_ + 至多 64 位字母數字。
    private static bool IsValidImageId(string? id)
    {
        if (id is null || id.Length is < 5 or > 68 || !id.StartsWith("img_", StringComparison.Ordinal)) return false;
        for (int i = 4; i < id.Length; i++)
            if (!char.IsLetterOrDigit(id[i])) return false;
        return true;
    }
}
