using System.Net.WebSockets;
using System.Text.Json;
using Honus.Contracts;
using Honus.Server.Analysis;
using Honus.Server.Config;
using Honus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Honus.Server.Ingest;

/// 事件通道(WebSocket /ingest/events)。见 api-contract §1。
/// 握手校验 X-Honus-Auth;每事件校验 sig;幂等落库(agent_id,seq,type);risk≥阈值入可疑队列。
public sealed class EventIngest(Db db, ServerConfig cfg, ILogger<EventIngest> log)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

        string examId = ctx.Request.Query["examId"].ToString();
        string seatId = ctx.Request.Query["seatId"].ToString();
        string agentId = ctx.Request.Query["agentId"].ToString();

        // 握手鉴权(见 §1.1)
        if (cfg.AuthEnabled)
        {
            string got = ctx.Request.Headers["X-Honus-Auth"].ToString();
            string want = Auth.Handshake(cfg.Psk!, examId, seatId, agentId);
            if (!Crypto.FixedTimeEquals(got, want))
            {
                ctx.Response.StatusCode = 401;
                log.LogWarning("事件握手鉴权失败 agent={Agent} seat={Seat}", agentId, seatId);
                return;
            }
        }

        using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
        CancellationToken ct = ctx.RequestAborted;
        log.LogInformation("Agent 连接 exam={Exam} seat={Seat} agent={Agent}", examId, seatId, agentId);

        while (ws.State == WebSocketState.Open)
        {
            string? msg = await WsUtil.ReceiveTextAsync(ws, ct);
            if (msg is null) break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(msg); }
            catch { continue; }   // 非法 JSON,忽略

            using (doc)
            {
                JsonElement root = doc.RootElement;
                string frameType = Str(root, "type") ?? "";
                try
                {
                    switch (frameType)
                    {
                        case "hello":    await OnHelloAsync(ws, agentId, ct); break;
                        case "event":    await OnEventAsync(ws, root, ct); break;
                        case "ping":     await WsUtil.SendTextAsync(ws, "{\"v\":1,\"type\":\"pong\"}", ct); break;
                        case "pong":     break;
                        default:         break;
                    }
                }
                catch (Exception ex) { log.LogError(ex, "处理帧异常 type={Type}", frameType); }
            }
        }
        log.LogInformation("Agent 断开 seat={Seat} agent={Agent}", seatId, agentId);
    }

    private async Task OnHelloAsync(WebSocket ws, string agentId, CancellationToken ct)
    {
        long maxSeq = db.Locked(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT COALESCE(MAX(seq),0) FROM events WHERE agent_id=@a", ("@a", agentId));
            return Convert.ToInt64(c.ExecuteScalar());
        });
        await WsUtil.SendTextAsync(ws, JsonSerializer.Serialize(new { v = 1, type = "hello_ack", maxSeq }), ct);
    }

    private async Task OnEventAsync(WebSocket ws, JsonElement frame, CancellationToken ct)
    {
        if (!frame.TryGetProperty("event", out JsonElement e) || e.ValueKind != JsonValueKind.Object) return;

        long seq = frame.TryGetProperty("seq", out JsonElement sq) && sq.TryGetInt64(out long s) ? s : LongProp(e, "seq");
        string? sig = Str(frame, "sig");

        string examId = Str(e, "examId") ?? "";
        string seatId = Str(e, "seatId") ?? "";
        string agentId = Str(e, "agentId") ?? "";
        string machineId = Str(e, "machineId") ?? "";
        double ts = e.TryGetProperty("ts", out JsonElement tse) && tse.TryGetDouble(out double t) ? t : 0;
        string typeStr = Str(e, "type") ?? "";
        int risk = e.TryGetProperty("risk", out JsonElement rke) && rke.TryGetInt32(out int rr) ? rr : 0;
        string? evidenceImageId = Str(e, "evidenceImageId");
        string? hashPrev = Str(e, "hashPrev");
        string? hashSelf = Str(e, "hashSelf");
        string payloadRaw = e.TryGetProperty("payload", out JsonElement pe) ? pe.GetRawText() : "{}";

        // 验签:sig = HMAC(PSK, hashSelf + "\n" + seq)。仅依赖 hashSelf 字符串,无需重算 canonical。
        if (cfg.AuthEnabled)
        {
            string want = EventCanonical.Sig(cfg.Psk!, hashSelf ?? "", seq);
            if (sig is null || !Crypto.FixedTimeEquals(sig, want))
            {
                await WsUtil.SendTextAsync(ws, JsonSerializer.Serialize(new { v = 1, type = "error", code = "bad_sig", seq }), ct);
                log.LogWarning("事件验签失败 agent={Agent} seq={Seq}", agentId, seq);
                return;
            }
        }

        double recvTs = Now();

        (long? newId, long uptoSeq) = db.Locked(conn =>
        {
            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO events (exam_id,seat_id,agent_id,seq,ts,recv_ts,type,payload,risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (@exam,@seat,@agent,@seq,@ts,@recv,@type,@payload,@risk,@ev,@hp,@hs,@sig)
                  ON CONFLICT(agent_id,seq,type) DO NOTHING",
                ("@exam", examId), ("@seat", seatId), ("@agent", agentId), ("@seq", seq),
                ("@ts", ts), ("@recv", recvTs), ("@type", typeStr), ("@payload", payloadRaw),
                ("@risk", risk), ("@ev", evidenceImageId), ("@hp", hashPrev), ("@hs", hashSelf), ("@sig", sig));
            int changed = ins.ExecuteNonQuery();

            long? id = null;
            if (changed > 0)
            {
                using SqliteCommand idc = conn.Cmd("SELECT last_insert_rowid()");
                id = Convert.ToInt64(idc.ExecuteScalar());

                // 触发型抓图 → 标记证据图
                if (evidenceImageId is not null)
                {
                    using SqliteCommand mk = conn.Cmd("UPDATE images SET is_evidence=1 WHERE image_id=@id", ("@id", evidenceImageId));
                    mk.ExecuteNonQuery();
                }

                // 心跳 → 写在线表
                if (typeStr == "heartbeat")
                {
                    string status = TryGetPayloadStr(payloadRaw, "status") ?? "alive";
                    using SqliteCommand hb = conn.Cmd(
                        "INSERT OR IGNORE INTO agent_heartbeats (agent_id,exam_id,seat_id,ts,status) VALUES (@a,@e,@s,@ts,@st)",
                        ("@a", agentId), ("@e", examId), ("@s", seatId), ("@ts", ts), ("@st", status));
                    hb.ExecuteNonQuery();
                }
            }

            using SqliteCommand mc = conn.Cmd("SELECT COALESCE(MAX(seq),0) FROM events WHERE agent_id=@a", ("@a", agentId));
            long upto = Convert.ToInt64(mc.ExecuteScalar());
            return (id, upto);
        });

        // 只对新落库、且达阈值的事件入可疑队列(避免重传重复入队)
        if (newId is not null && risk >= cfg.RiskThreshold && typeStr != "heartbeat")
            EnqueueSuspicious(examId, seatId, ts, typeStr, risk, newId.Value, evidenceImageId, payloadRaw);

        await WsUtil.SendTextAsync(ws, JsonSerializer.Serialize(new { v = 1, type = "ack", upto = uptoSeq }), ct);
    }

    private void EnqueueSuspicious(string examId, string seatId, double ts, string typeStr,
        int risk, long eventId, string? evidenceImageId, string payloadRaw)
    {
        SignalType type = ParseType(typeStr);
        JsonElement payload;
        try { using var pd = JsonDocument.Parse(payloadRaw); payload = pd.RootElement.Clone(); }
        catch { payload = default; }

        string kind = Suspicion.KindFor(type, payload);
        var refs = new List<string> { $"event:{eventId}" };
        if (evidenceImageId is not null) refs.Add($"image:{evidenceImageId}");
        string refsJson = JsonSerializer.Serialize(refs);

        db.Locked(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs)
                  VALUES (@e,@s,@ts,@k,@sc,'pending',@refs)",
                ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", risk), ("@refs", refsJson));
            c.ExecuteNonQuery();
        });
    }

    // ---- 小工具 ----
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string? Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

    private static long LongProp(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out JsonElement e) && e.TryGetInt64(out long v) ? v : 0;

    private static string? TryGetPayloadStr(string payloadRaw, string prop)
    {
        try { using var d = JsonDocument.Parse(payloadRaw); return Str(d.RootElement, prop); }
        catch { return null; }
    }

    private static SignalType ParseType(string s) => s switch
    {
        "window_focus" => SignalType.WindowFocus,
        "browser_url" => SignalType.BrowserUrl,
        "process_start" => SignalType.ProcessStart,
        "process_exit" => SignalType.ProcessExit,
        "clipboard" => SignalType.Clipboard,
        "alt_tab_burst" => SignalType.AltTabBurst,
        "usb" => SignalType.Usb,
        "screenshot" => SignalType.Screenshot,
        "heartbeat" => SignalType.Heartbeat,
        _ => SignalType.WindowFocus,
    };
}
