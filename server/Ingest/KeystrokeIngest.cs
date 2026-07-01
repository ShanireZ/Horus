using System.Text.Json;
using Honus.Server.Config;
using Honus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Honus.Server.Ingest;

/// 击键节奏旁路(HTTP POST /ingest/keystroke),来自判题网页前端,不经 Agent。见 api-contract §2.2。
/// M1:落库 + 基础 risk 初判(整段粘贴 / 超人爆发 / 空窗后突现整段 → 可疑)。
public sealed class KeystrokeIngest(Db db, ServerConfig cfg)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            root = doc.RootElement.Clone();
        }
        catch { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "bad_json" }); return; }

        string examId = Str(root, "examId") ?? "";
        string seatId = Str(root, "seatId") ?? "";
        string? submissionId = Str(root, "submissionId");
        double ts = root.TryGetProperty("ts", out JsonElement tse) && tse.TryGetDouble(out double t)
            ? t : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        string? timeline = root.TryGetProperty("timeline", out JsonElement tl) ? tl.GetRawText() : null;
        JsonElement features = root.TryGetProperty("features", out JsonElement f) ? f : default;
        string? featuresJson = features.ValueKind == JsonValueKind.Object ? features.GetRawText() : null;

        int risk = RiskFrom(features);

        long id = db.Locked(conn =>
        {
            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO keystroke_samples (exam_id,seat_id,submission_id,ts,timeline,features,risk)
                  VALUES (@e,@s,@sub,@ts,@tl,@ft,@risk)",
                ("@e", examId), ("@s", seatId), ("@sub", submissionId), ("@ts", ts),
                ("@tl", timeline), ("@ft", featuresJson), ("@risk", risk));
            ins.ExecuteNonQuery();
            using SqliteCommand idc = conn.Cmd("SELECT last_insert_rowid()");
            return Convert.ToInt64(idc.ExecuteScalar());
        });

        // 达阈值 → 入可疑队列(kind 依特征细分)
        if (risk >= cfg.RiskThreshold)
        {
            string kind = features.ValueKind == JsonValueKind.Object &&
                          features.TryGetProperty("idleThenBlock", out JsonElement itb) &&
                          itb.ValueKind == JsonValueKind.True
                ? "ide_plugin_suspect" : "large_paste";
            string refs = JsonSerializer.Serialize(new[] { $"keystroke:{id}" });
            db.Locked(conn =>
            {
                using SqliteCommand c = conn.Cmd(
                    @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs)
                      VALUES (@e,@s,@ts,@k,@sc,'pending',@refs)",
                    ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", risk), ("@refs", refs));
                c.ExecuteNonQuery();
            });
        }

        await ctx.Response.WriteAsJsonAsync(new { stored = true, risk });
    }

    private static int RiskFrom(JsonElement features)
    {
        if (features.ValueKind != JsonValueKind.Object) return 0;
        bool idleThenBlock = features.TryGetProperty("idleThenBlock", out JsonElement itb) && itb.ValueKind == JsonValueKind.True;
        int pasteCount = features.TryGetProperty("pasteCount", out JsonElement pc) && pc.TryGetInt32(out int p) ? p : 0;
        double burst = features.TryGetProperty("maxBurstCharsPerSec", out JsonElement b) && b.TryGetDouble(out double bv) ? bv : 0;

        if (idleThenBlock) return 70;               // 空窗后突现整段代码 → 高风险
        if (pasteCount > 0) return 60;              // 粘贴
        if (burst > 120) return 55;                 // 超人输入速度
        return 0;
    }

    private static string? Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}
