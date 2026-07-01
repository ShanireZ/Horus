using Honus.Contracts;
using Honus.Server.Config;
using Honus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Honus.Server.Ingest;

/// 图片通道(HTTP POST /ingest/images)。见 api-contract §2.1。
/// 校验 X-Honus-Sig;pHash 去重;原图存盘;DB 存指针。
public sealed class ImageIngest(Db db, Storage storage, ServerConfig cfg, ILogger<ImageIngest> log)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        HttpRequest req = ctx.Request;
        string examId = req.Headers["X-Honus-Exam"].ToString();
        string seatId = req.Headers["X-Honus-Seat"].ToString();
        string agentId = req.Headers["X-Honus-Agent"].ToString();
        string seqStr = req.Headers["X-Honus-Seq"].ToString();
        string trigger = req.Headers["X-Honus-Trigger"].ToString();
        string phash = req.Headers["X-Honus-Phash"].ToString();
        string tsStr = req.Headers["X-Honus-Ts"].ToString();
        string sig = req.Headers["X-Honus-Sig"].ToString();

        if (string.IsNullOrEmpty(examId) || string.IsNullOrEmpty(seatId) || string.IsNullOrEmpty(agentId))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "missing_headers" });
            return;
        }

        byte[] body;
        using (var ms = new MemoryStream())
        {
            await req.Body.CopyToAsync(ms);
            body = ms.ToArray();
        }

        long.TryParse(seqStr, out long seq);
        double.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture, out double ts);

        // 验签(见 §2.1):HMAC(PSK, canonicalHeaders + "\n" + sha256(body))
        if (cfg.AuthEnabled)
        {
            string canon = Auth.ImageCanonicalHeaders(examId, seatId, agentId, seq, trigger, phash, tsStr);
            string want = Auth.ImageSig(cfg.Psk!, canon, body);
            if (string.IsNullOrEmpty(sig) || !Crypto.FixedTimeEquals(sig, want))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "bad_sig" });
                log.LogWarning("图片验签失败 seat={Seat} seq={Seq}", seatId, seq);
                return;
            }
        }

        // pHash 去重(同座位相同 phash 视为近重复,不另存原图)
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

        string imageId = "img_" + Guid.NewGuid().ToString("N");
        string relPath = await storage.SaveWebpAsync(examId, seatId, imageId, body);
        double recvTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        db.Locked(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO images (image_id,exam_id,seat_id,agent_id,ts,recv_ts,trigger,phash,file_path,format,bytes,uploaded_to_ocr,is_evidence)
                  VALUES (@id,@e,@s,@a,@ts,@recv,@trig,@ph,@fp,'webp',@bytes,0,0)",
                ("@id", imageId), ("@e", examId), ("@s", seatId), ("@a", agentId),
                ("@ts", ts), ("@recv", recvTs), ("@trig", trigger), ("@ph", phash),
                ("@fp", relPath), ("@bytes", (long)body.Length));
            c.ExecuteNonQuery();
        });

        await ctx.Response.WriteAsJsonAsync(new { stored = true, imageId, duplicate = false, ocrQueued = false });
    }
}
