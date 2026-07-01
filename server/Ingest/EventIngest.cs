using System.Net.WebSockets;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Analysis;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 事件通道(WebSocket /ingest/events)。见 api-contract §1。
/// 握手校验 X-Horus-Auth;每事件校验 sig;幂等落库(agent_id,seq,type);risk≥阈值入可疑队列。
public sealed class EventIngest(Db db, ServerConfig cfg, AgentHub hub, ILogger<EventIngest> log)
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
            string got = ctx.Request.Headers["X-Horus-Auth"].ToString();
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
        AgentHub.Conn conn = hub.Register(agentId, examId, ws);   // 登记在线连接(供 config_update 下推)
        log.LogInformation("Agent 连接 exam={Exam} seat={Seat} agent={Agent}", examId, seatId, agentId);

        try
        {
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
                            case "hello":    await OnHelloAsync(conn, agentId, ct); break;
                            case "event":    await OnEventAsync(conn, root, ct); break;
                            case "ping":     await conn.SendAsync("{\"v\":1,\"type\":\"pong\"}", ct); break;
                            case "pong":     break;
                            default:         break;
                        }
                    }
                    catch (Exception ex) { log.LogError(ex, "处理帧异常 type={Type}", frameType); }
                }
            }
        }
        finally { hub.Unregister(agentId, conn); }

        // 完成关闭握手(对端主动关闭时回一帧 Close),避免客户端 WS 报异常关闭。
        try
        {
            if (ws.State is WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* 连接已断,忽略 */ }

        log.LogInformation("Agent 断开 seat={Seat} agent={Agent}", seatId, agentId);
    }

    private async Task OnHelloAsync(AgentHub.Conn conn, string agentId, CancellationToken ct)
    {
        long maxSeq = db.Locked(c2 =>
        {
            using SqliteCommand c = c2.Cmd("SELECT COALESCE(MAX(seq),0) FROM events WHERE agent_id=@a", ("@a", agentId));
            return Convert.ToInt64(c.ExecuteScalar());
        });
        await conn.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "hello_ack", maxSeq }), ct);

        // 该考试若已设配置,连上即推一次,使新连 / 重连的 Agent 拿到当前配置
        string? cfgJson = hub.GetConfig(conn.ExamId);
        if (cfgJson is not null)
            await conn.SendAsync(AgentHub.BuildConfigFrame(cfgJson), ct);
    }

    private async Task OnEventAsync(AgentHub.Conn link, JsonElement frame, CancellationToken ct)
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
                await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "bad_sig", seq }), ct);
                log.LogWarning("事件验签失败 agent={Agent} seq={Seq}", agentId, seq);
                return;
            }
        }

        double recvTs = Now();

        // 服务器侧风险复判(**不信任 Agent 自报 risk**):凭独立黑名单 + 该考试已下发白名单重算。
        // 有效风险 = max(agentRisk, serverRisk);持 PSK 学员机把「访问 AI 站」签成 risk=0 也压不住入队。
        SignalType sigType = ParseType(typeStr);
        JsonElement payloadEl = e.TryGetProperty("payload", out JsonElement pEl) ? pEl : default;
        var (wlHosts, wlProcs, pasteThreshold) = RiskModel.PolicyFrom(hub.GetConfig(examId));
        int serverRisk = RiskModel.Derive(sigType, payloadEl, wlHosts, wlProcs, pasteThreshold);
        int effRisk = Math.Max(risk, serverRisk);

        long? newId = db.Locked(conn =>
        {
            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO events (exam_id,seat_id,agent_id,seq,ts,recv_ts,type,payload,risk,server_risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (@exam,@seat,@agent,@seq,@ts,@recv,@type,@payload,@risk,@srisk,@ev,@hp,@hs,@sig)
                  ON CONFLICT(agent_id,seq) DO NOTHING",
                ("@exam", examId), ("@seat", seatId), ("@agent", agentId), ("@seq", seq),
                ("@ts", ts), ("@recv", recvTs), ("@type", typeStr), ("@payload", payloadRaw),
                ("@risk", risk), ("@srisk", serverRisk), ("@ev", evidenceImageId), ("@hp", hashPrev), ("@hs", hashSelf), ("@sig", sig));
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
            }

            // 心跳写在线表:不论新旧都刷新(重传旧心跳写旧 ts 无害,不拉低 MAX(ts) 的新鲜度)
            if (typeStr == "heartbeat")
            {
                string status = TryGetPayloadStr(payloadRaw, "status") ?? "alive";
                using SqliteCommand hb = conn.Cmd(
                    "INSERT INTO agent_heartbeats (agent_id,exam_id,seat_id,ts,status) VALUES (@a,@e,@s,@ts,@st) ON CONFLICT(agent_id,ts) DO UPDATE SET status=@st",
                    ("@a", agentId), ("@e", examId), ("@s", seatId), ("@ts", ts), ("@st", status));
                hb.ExecuteNonQuery();
            }

            return id;
        });

        // 只对新落库事件入可疑队列(避免重传重复入队);用**有效风险**判阈值,browser_unreadable 无视阈值。
        // agentRisk 低于阈值但 serverRisk 顶上去 → 记 agent_risk_understated,是篡改逃逸的取证信号。
        if (newId is not null && typeStr != "heartbeat" &&
            (effRisk >= cfg.RiskThreshold || IsForcedReview(typeStr, payloadRaw)))
        {
            string? tamperNote = serverRisk >= cfg.RiskThreshold && risk < cfg.RiskThreshold
                ? $"agent_risk_understated agent={risk} server={serverRisk}" : null;
            EnqueueSuspicious(examId, seatId, ts, typeStr, effRisk, newId.Value, evidenceImageId, payloadRaw, tamperNote);
        }

        // 逐条 ack 本条 seq(不用范围 upto):即使 seq 空间有空洞,也不会误删从未送达的低 seq 事件
        await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "ack", seq }), ct);
    }

    /// 抓不到 URL 的降级信号 = 强制人工复核(无视风险阈值),否则该兜底链断在最后一步。
    private static bool IsForcedReview(string typeStr, string payloadRaw)
        => typeStr == "browser_url" && TryGetPayloadStr(payloadRaw, "note") == "url_unreadable";

    private void EnqueueSuspicious(string examId, string seatId, double ts, string typeStr,
        int score, long eventId, string? evidenceImageId, string payloadRaw, string? note)
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
                @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs,note)
                  VALUES (@e,@s,@ts,@k,@sc,'pending',@refs,@note)",
                ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", score),
                ("@refs", refsJson), ("@note", note));
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
