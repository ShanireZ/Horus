using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 图片通道(HTTP POST /ingest/images)。见 api-contract §2.1。
/// 校验 X-Horus-Sig;pHash 去重;原图存盘;DB 存指针;新图入队异步视觉分析(L2)。
public sealed class ImageIngest(Db db, Storage storage, ServerConfig cfg, VisionAnalysisService vision, ILogger<ImageIngest> log)
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
        string clientId = req.Headers["X-Horus-Image-Id"].ToString();   // 客户端预生成 id(纳入签名)

        if (string.IsNullOrEmpty(examId) || string.IsNullOrEmpty(seatId) || string.IsNullOrEmpty(agentId))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "missing_headers" });
            return;
        }
        if (clientId.Length > 0 && !IsValidImageId(clientId))   // 畸形 client id 先拒(验签前),收敛 canonical 输入
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "bad_image_id" });
            return;
        }

        byte[] body;
        using (var ms = new MemoryStream())
        {
            await req.Body.CopyToAsync(ms);
            body = ms.ToArray();
        }
        if (body.Length == 0)   // 空 body 不落库(否则存一张 0 字节坏图)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "empty_body" });
            return;
        }

        long.TryParse(seqStr, out long seq);
        double.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture, out double ts);

        // 验签(见 §2.1):HMAC(PSK, canonicalHeaders + "\n" + sha256(body));canonical 含 imageId 防篡改
        if (cfg.AuthEnabled)
        {
            string canon = Auth.ImageCanonicalHeaders(examId, seatId, agentId, seq, trigger, phash, tsStr, clientId);
            string want = Auth.ImageSig(cfg.Psk!, canon, body);
            if (string.IsNullOrEmpty(sig) || !Crypto.FixedTimeEquals(sig, want))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "bad_sig" });
                log.LogWarning("图片验签失败 seat={Seat} seq={Seq}", seatId, seq);
                return;
            }
        }

        // 归档中/已归档考试:短路不落库(避免归档窗口 late-ingest 被无锚点删 + 归档后重建孤儿图行)。
        if (db.Read(conn => conn.IsExamSealed(examId)))
        {
            await ctx.Response.WriteAsJsonAsync(new { stored = false, error = "exam_sealed" });
            return;
        }

        // 客户端预生成 imageId(触发型抓图):幂等沿用,以保证"事件 ↔ 证据图"关联跨断线不断。
        bool hasClientId = IsValidImageId(clientId);

        string imageId;
        if (hasClientId)
        {
            bool exists = db.Locked(conn =>
            {
                using SqliteCommand c = conn.Cmd("SELECT 1 FROM images WHERE image_id=@id LIMIT 1", ("@id", clientId));
                return c.ExecuteScalar() is not null;
            });
            if (exists)   // 重传(续传)→ 幂等,不另存
            {
                await ctx.Response.WriteAsJsonAsync(new { stored = false, imageId = clientId, duplicate = true, ocrQueued = false });
                return;
            }
            imageId = clientId;   // 沿用客户端 id;**跳过 pHash 去重**以尊重事件关联
        }
        else
        {
            // 无客户端 id(如 baseline):pHash 去重 + 服务器分配
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

        bool sealedInLock = db.Locked(conn =>
        {
            // late-ingest 隔离:上方 :70 的 sealed 预检在**只读连接**(快速拒绝),此处在**写锁事务内**权威复检——
            // 预检通过后、拿写锁前归档作业可能已翻转 archiving 并清理该考试,若此处不查会把孤儿图行插进已归档考试
            // (既不再归档也不清理)。与 EventIngest/KeystrokeIngest 的锁内检查对齐。
            if (conn.IsExamSealed(examId)) return true;

            using (SqliteCommand c = conn.Cmd(
                @"INSERT INTO images (image_id,exam_id,seat_id,agent_id,ts,recv_ts,trigger,phash,file_path,format,bytes,uploaded_to_ocr,is_evidence)
                  VALUES (@id,@e,@s,@a,@ts,@recv,@trig,@ph,@fp,'webp',@bytes,0,0)
                  ON CONFLICT(image_id) DO NOTHING",
                ("@id", imageId), ("@e", examId), ("@s", seatId), ("@a", agentId),
                ("@ts", ts), ("@recv", recvTs), ("@trig", trigger), ("@ph", phash),
                ("@fp", relPath), ("@bytes", (long)body.Length)))
                c.ExecuteNonQuery();

            // 反向补标:仅触发型抓图(有客户端 id)才可能被事件引用;baseline 图跳过,免每张全表查 events。
            if (hasClientId)
            {
                using SqliteCommand mk = conn.Cmd(
                    "UPDATE images SET is_evidence=1 WHERE image_id=@id AND EXISTS (SELECT 1 FROM events WHERE evidence_image_id=@id)",
                    ("@id", imageId));
                mk.ExecuteNonQuery();
            }
            return false;
        });

        if (sealedInLock)   // 写锁内发现考试已封存:回滚已落盘的孤儿 webp(DB 行未插),按 exam_sealed 拒绝
        {
            storage.DeleteLive(relPath);
            await ctx.Response.WriteAsJsonAsync(new { stored = false, error = "exam_sealed" });
            return;
        }

        // 送异步视觉分析(§5 最小化:触发型必送;随机基线按配置抽样)。视觉关时 no-op、不占本请求延迟。
        bool ocrQueued = vision.Enabled
            && (trigger.StartsWith("event:", StringComparison.Ordinal) || cfg.VisionAnalyzeBaseline);
        if (ocrQueued) vision.Enqueue(imageId);

        await ctx.Response.WriteAsJsonAsync(new { stored = true, imageId, duplicate = false, ocrQueued });
    }

    /// 客户端 id 格式校验(防路径注入):img_ + 至多 64 位字母数字。
    private static bool IsValidImageId(string? id)
    {
        if (id is null || id.Length is < 5 or > 68 || !id.StartsWith("img_", StringComparison.Ordinal)) return false;
        for (int i = 4; i < id.Length; i++)
            if (!char.IsLetterOrDigit(id[i])) return false;
        return true;
    }
}
