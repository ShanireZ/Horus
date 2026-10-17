using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 擊鍵節奏旁路(HTTP POST /ingest/keystroke),來自判題網頁(經判題後端),不經 Agent。見 api-contract §2.2。
/// M1:寫入資料庫 + 基礎 risk 初判(整段粘貼 / 超人爆發 / 空窗後突現整段 → 可疑)。
/// M2:**工作階段鑑權**——判題後端持 KSK 對整條提交體簽名(X-Horus-KSig),擋同網學員機偽造/栽贓他人擊鍵。
public sealed class KeystrokeIngest(Db db, ServerConfig cfg)
{
    // 擊鍵體專用上限(遠小於圖片的全域 2MB):timeline 是 keydown 時間戳陣列,合法體通常幾十 KB。
    // 超大體先拒,免為明顯非法體緩衝 + 算 SHA256 驗簽(未鑑權者也能觸發)。
    private const int MaxBodyBytes = 512 * 1024;

    public async Task HandleAsync(HttpContext ctx)
    {
        // 共用 ingest body 讀取 + 顯式上限(與 ImageIngest 同 helper · 品質#5):Content-Length 超限在分配
        // MemoryStream 前即拒(未鑑權者也能觸發的緩衝放大),實測長度備援。
        var (body, statusCode, err) = await IngestBody.ReadWithLimitAsync(ctx.Request, MaxBodyBytes, allowEmpty: true);
        if (body is null)
        {
            ctx.Response.StatusCode = statusCode!.Value;
            await ctx.Response.WriteAsJsonAsync(new { error = err });
            return;
        }

        // 擊鍵鑑權:X-Horus-KSig = HMAC(KSK, "keystroke\n"+sha256(body))。任何篡改(含改 seatId 栽贓)都會破壞簽名。
        if (cfg.KeystrokeAuthEnabled)
        {
            string ksig = ctx.Request.Headers["X-Horus-KSig"].ToString();
            string want = Auth.KeystrokeSig(cfg.Ksk!, body);
            if (string.IsNullOrEmpty(ksig) || !Crypto.FixedTimeEquals(ksig, want))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "bad_ksig" });
                return;
            }
        }

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
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

        // 寫入資料庫 + 入可疑佇列在**同一寫鎖交易**內:避免二者分處兩個交易時,歸檔視窗卡在中間導致入隊一條
        // 指向已歸檔刪除樣本的孤兒 pending(與 EventIngest 同型修復)。sealed 檢查已在交易開頭。
        long id = db.Locked(conn =>
        {
            if (conn.IsExamSealed(examId)) return -1L;   // 歸檔中/已歸檔:短路不寫入資料庫(避免歸檔視窗 late-ingest)

            // 冪等寫入資料庫:同 (exam,seat,ts,submissionId) 已存在則不重插——防同網攻擊者嗅到 HTTP 明文後
            // **原樣重放**合法簽名體(簽名驗得過但無去重鍵)灌可疑佇列/DoS。簽名防偽造/栽贓,冪等防重放。
            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO keystroke_samples (exam_id,seat_id,submission_id,ts,timeline,features,risk)
                  SELECT @e,@s,@sub,@ts,@tl,@ft,@risk
                  WHERE NOT EXISTS (SELECT 1 FROM keystroke_samples
                                    WHERE exam_id=@e AND seat_id=@s AND ts=@ts
                                      AND COALESCE(submission_id,'')=COALESCE(@sub,''))",
                ("@e", examId), ("@s", seatId), ("@sub", submissionId), ("@ts", ts),
                ("@tl", timeline), ("@ft", featuresJson), ("@risk", risk));
            if (ins.ExecuteNonQuery() == 0) return -1L;   // 重放/重複:不另存、不重複入隊
            using SqliteCommand idc = conn.Cmd("SELECT last_insert_rowid()");
            long newId = Convert.ToInt64(idc.ExecuteScalar());

            // 達閾值 → 入可疑佇列(kind 依特徵細分),與寫入資料庫同一交易
            if (risk >= cfg.RiskThreshold)
            {
                string kind = features.ValueKind == JsonValueKind.Object &&
                              features.TryGetProperty("idleThenBlock", out JsonElement itb) &&
                              itb.ValueKind == JsonValueKind.True
                    ? "ide_plugin_suspect" : "large_paste";
                string refs = JsonSerializer.Serialize(new[] { $"keystroke:{newId}" });
                using SqliteCommand c = conn.Cmd(
                    @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs)
                      VALUES (@e,@s,@ts,@k,@sc,'pending',@refs)",
                    ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", risk), ("@refs", refs));
                c.ExecuteNonQuery();
            }
            return newId;
        });

        if (id < 0)   // 重放:冪等回傳,不入隊
        {
            await ctx.Response.WriteAsJsonAsync(new { stored = false, duplicate = true, risk });
            return;
        }

        await ctx.Response.WriteAsJsonAsync(new { stored = true, risk });
    }

    private static int RiskFrom(JsonElement features)
    {
        if (features.ValueKind != JsonValueKind.Object) return 0;
        bool idleThenBlock = features.TryGetProperty("idleThenBlock", out JsonElement itb) && itb.ValueKind == JsonValueKind.True;
        int pasteCount = features.TryGetProperty("pasteCount", out JsonElement pc) && pc.TryGetInt32(out int p) ? p : 0;
        double burst = features.TryGetProperty("maxBurstCharsPerSec", out JsonElement b) && b.TryGetDouble(out double bv) ? bv : 0;

        if (idleThenBlock) return RiskScores.KeystrokeIdleThenBlock;     // 空窗後突現整段程式碼 → 高風險
        if (pasteCount > 0) return RiskScores.KeystrokePaste;            // 粘貼
        if (burst > 120) return RiskScores.KeystrokeBurst;               // 超人輸入速度
        return 0;
    }

    private static string? Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}
