using System.Net.WebSockets;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Analysis;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 事件通道(WebSocket /ingest/events)。见 api-contract §1。
/// 握手校验 X-Horus-Auth;每事件校验 sig;幂等落库(agent_id,seq,type);risk≥阈值入可疑队列。
/// M4:鉴权密钥可为共享 PSK 或 OIDC 会话 K_sess(见 IngestAuth);OIDC 路径强制事件体身份==会话身份(闭合 A1/A2)。
public sealed class EventIngest(Db db, ServerConfig cfg, AgentHub hub, SessionStore sessions, ILogger<EventIngest> log)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

        string examId = ctx.Request.Query["examId"].ToString();
        string seatId = ctx.Request.Query["seatId"].ToString();
        string agentId = ctx.Request.Query["agentId"].ToString();

        // 握手鉴权(见 §1.1)。M4:先解析鉴权上下文(PSK ↔ OIDC 会话共存)。
        string sessionId = ctx.Request.Headers["X-Horus-Session"].ToString();
        IngestAuth.Resolved auth = IngestAuth.Resolve(cfg, sessions, sessionId, examId, seatId, agentId, Now());
        if (!auth.Ok)
        {
            ctx.Response.StatusCode = 401;
            log.LogWarning("事件握手鉴权拒绝 agent={Agent} seat={Seat} 原因={Err}", agentId, seatId, auth.Error);
            return;
        }
        if (auth.Key is not null)   // 用 PSK 或会话 K_sess 验握手
        {
            string got = ctx.Request.Headers["X-Horus-Auth"].ToString();
            string want = Auth.Handshake(auth.Key, examId, seatId, agentId);
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

        // 闭合全场登出竞态:握手期(IngestAuth.Resolve 已读取会话)与 hub.Register 之间存在窗口——若此刻发生 /logout,
        // RevokeByExam(DELETE 会话)恒先于 PushSessionRevokedAsync(遍历 _conns 强断)。故本连接若在 abort 遍历时尚未登记,
        // 会被漏断;而收帧循环只用握手期缓存的 auth,从不复查会话。登记后立即复查一次 OIDC 会话仍在:
        // 因 DELETE 先于 abort,凡"错过 abort"的连接必然登记于 DELETE 之后 → 此复查必发现会话已吊销 → 主动断开。
        if (auth.Session is not null && sessions.Get(sessionId, Now()) is null)
        {
            hub.Unregister(agentId, conn);
            log.LogInformation("Agent 登记后发现会话已吊销/过期,断开 exam={Exam} seat={Seat} agent={Agent}", examId, seatId, agentId);
            try { ws.Abort(); } catch { /* 已断,忽略 */ }
            return;
        }

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
                            case "event":    await OnEventAsync(conn, root, auth.Key, auth.Session, ct); break;
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
        (long maxSeq, string? examStatus) = db.Locked(c2 =>
        {
            using SqliteCommand c = c2.Cmd("SELECT COALESCE(MAX(seq),0) FROM events WHERE agent_id=@a", ("@a", agentId));
            long ms = Convert.ToInt64(c.ExecuteScalar());
            using SqliteCommand st = c2.Cmd("SELECT status FROM exams WHERE exam_id=@e", ("@e", conn.ExamId));
            return (ms, st.ExecuteScalar() as string);
        });
        await conn.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "hello_ack", maxSeq }), ct);

        // 该考试若已设配置,连上即推一次,使新连 / 重连的 Agent 拿到当前配置
        string? cfgJson = hub.GetConfig(conn.ExamId);
        if (cfgJson is not null)
            await conn.SendAsync(AgentHub.BuildConfigFrame(cfgJson), ct);

        // 考试派发:Agent 离线期间考试被结束/归档 → 重连 hello 时补发 exam_ended(先让上面的续传窗口存在:
        // Agent 收到后先排空缓冲再停采回待命,end 时刻附近的证据不丢)。无考试记录(联调/psk 未建考试)不发。
        if (examStatus is not null && examStatus != "active")
            await conn.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "exam_ended", examId = conn.ExamId }), ct);
    }

    private async Task OnEventAsync(AgentHub.Conn link, JsonElement frame, byte[]? authKey, HorusSession? session, CancellationToken ct)
    {
        if (!frame.TryGetProperty("event", out JsonElement e) || e.ValueKind != JsonValueKind.Object) return;

        // BUG#6:信封外层 seq 与内层 @event.seq 冗余(线协议)。Agent 用内层 e.Seq 签名,故**以内层为权威**;
        // 若外层与内层不一致(协议被中间改写 / 版本错配)记告警,但不阻断(仍按内层权威 seq 验签与落库)。
        long seq = e.TryGetProperty("seq", out JsonElement eSeqEl) && eSeqEl.TryGetInt64(out long eSeq)
            ? eSeq
            : (frame.TryGetProperty("seq", out JsonElement sq) && sq.TryGetInt64(out long s) ? s : 0);
        if (frame.TryGetProperty("seq", out JsonElement outerSeq) && outerSeq.TryGetInt64(out long outerS) && outerS != seq)
            log.LogWarning("信封外层 seq({Outer}) 与内层 seq({Inner}) 不一致(以内层为准)", outerS, seq);
        string? sig = Str(frame, "sig");

        string examId = Str(e, "examId") ?? "";
        string seatId = Str(e, "seatId") ?? "";
        string agentId = Str(e, "agentId") ?? "";
        string machineId = Str(e, "machineId") ?? "";

        // M4·闭合 A1:OIDC 路径下,事件体自报身份**必须 == 会话绑定身份**,否则是拿自己会话给他人栽赃 —— 拒收。
        // (K_sess 只证明「是本会话所签」,不证明「身份为真」;身份真伪靠此强制 + OIDC 认证。)seq 空间归属本会话 agent → 闭合 A2。
        if (session is not null && !session.IdentityMatches(examId, seatId, agentId))
        {
            await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "identity_mismatch", seq }), ct);
            log.LogWarning("事件身份与会话不符(疑跨身份栽赃) session={Seat} bodyAgent={Agent}", session.SeatId, agentId);
            return;
        }
        double ts = e.TryGetProperty("ts", out JsonElement tse) && tse.TryGetDouble(out double t) ? t : 0;
        string typeStr = Str(e, "type") ?? "";
        int risk = e.TryGetProperty("risk", out JsonElement rke) && rke.TryGetInt32(out int rr) ? rr : 0;
        string? evidenceImageId = Str(e, "evidenceImageId");
        string? hashPrev = Str(e, "hashPrev");
        string? hashSelf = Str(e, "hashSelf");
        string payloadRaw = e.TryGetProperty("payload", out JsonElement pe) ? pe.GetRawText() : "{}";

        // 验签:sig = HMAC(key, hashSelf + "\n" + seq)。key = PSK 或 OIDC 会话 K_sess(见 IngestAuth)。仅依赖 hashSelf 字符串。
        if (authKey is not null)
        {
            string want = EventCanonical.Sig(authKey, hashSelf ?? "", seq);
            if (sig is null || !Crypto.FixedTimeEquals(sig, want))
            {
                await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "bad_sig", seq }), ct);
                log.LogWarning("事件验签失败 agent={Agent} seq={Seq}", agentId, seq);
                return;
            }

            // 完整性复验(M3·闭合 §10.1「服务器不重算 canonical」):sig 只证明「知道 PSK 且承诺了 hashSelf+seq」,
            // 本身**不保证 hashSelf 绑定 payload**。此处从**原始 payload 文本 + 落库字段**逐字节复算 canonicalCore、
            // 复算 hashSelf,要求与自报 hashSelf 一致 —— 使 hashSelf/sig 成为**真正锚定 payload/字段**的取证锚点。
            // 任何 payload / 字段与 hashSelf 不符(自报错乱或传输中被非 PSK 方篡改) → 拒收,不落库。
            // 注:持 PSK 学员机仍可自洽地伪造 payload+hashSelf(结构性残留,靠截图/视觉/人工兜底,见 §10.1),
            //     此步保证的是「锚点确实承诺其 payload」+「链后审计可发现落库后改动/删增」。
            if (!EventCanonical.VerifyHashSelf(hashPrev ?? "GENESIS", examId, seatId, agentId, machineId,
                    ts, typeStr, payloadRaw, risk, evidenceImageId, seq, hashSelf))
            {
                await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "error", code = "bad_hash", seq }), ct);
                log.LogWarning("事件哈希复验失败(hashSelf 不承诺其 payload) agent={Agent} seq={Seq}", agentId, seq);
                return;
            }
        }

        double recvTs = Now();

        // 服务器侧风险复判(**不信任 Agent 自报 risk**):凭独立黑名单 + 该考试已下发白名单重算。
        // 有效风险 = max(agentRisk, serverRisk);持 PSK 学员机把「访问 AI 站」签成 risk=0 也压不住入队。
        // 策略取 AgentHub **已解析缓存**(下发时重建),免每事件热路径重复 JsonDocument.Parse + 建 HashSet。
        SignalType sigType = ParseType(typeStr);
        JsonElement payloadEl = e.TryGetProperty("payload", out JsonElement pEl) ? pEl : default;
        RiskModel.Policy policy = hub.GetPolicy(examId);
        int serverRisk = RiskModel.Derive(sigType, payloadEl, policy.Hosts, policy.Procs, policy.PasteThreshold);
        int effRisk = Math.Max(risk, serverRisk);

        // 落库 + 入可疑队列在**同一写锁事务**内完成:避免二者分处两个事务时,崩溃/归档窗口卡在中间导致
        // 高危事件已落库却漏入队(复传因 ON CONFLICT DO NOTHING 不再补入队),或入队一条指向已归档删除事件的孤儿 pending。
        db.Locked(conn =>
        {
            // 归档中/已归档考试:短路不落库(避免"读快照→DELETE"窗口内的 late-ingest 被无锚点删)。仍会 ack 使 Agent 停发。
            if (conn.IsExamSealed(examId)) return;

            using SqliteCommand ins = conn.Cmd(
                @"INSERT INTO events (exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,server_risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (@exam,@seat,@agent,@machine,@seq,@ts,@recv,@type,@payload,@risk,@srisk,@ev,@hp,@hs,@sig)
                  ON CONFLICT(agent_id,seq) DO NOTHING",
                ("@exam", examId), ("@seat", seatId), ("@agent", agentId), ("@machine", machineId), ("@seq", seq),
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

            // 心跳写在线表:不论新旧都刷新;写后**裁剪**只保留该 (exam,seat,agent) 最新 ts 的一行 ——
            // 在线判定只需最新一条,故无须留历史;同时防持 PSK 者用不断变化的 ts 无界追加撑爆表(replay 的旧 ts 会被裁掉)。
            if (typeStr == "heartbeat")
            {
                string status = TryGetPayloadStr(payloadRaw, "status") ?? "alive";
                using (SqliteCommand hb = conn.Cmd(
                    "INSERT INTO agent_heartbeats (agent_id,exam_id,seat_id,ts,status) VALUES (@a,@e,@s,@ts,@st) ON CONFLICT(exam_id,seat_id,agent_id,ts) DO UPDATE SET status=@st",
                    ("@a", agentId), ("@e", examId), ("@s", seatId), ("@ts", ts), ("@st", status)))
                    hb.ExecuteNonQuery();
                using (SqliteCommand prune = conn.Cmd(
                    @"DELETE FROM agent_heartbeats WHERE exam_id=@e AND seat_id=@s AND agent_id=@a
                        AND ts < (SELECT MAX(ts) FROM agent_heartbeats WHERE exam_id=@e AND seat_id=@s AND agent_id=@a)",
                    ("@e", examId), ("@s", seatId), ("@a", agentId)))
                    prune.ExecuteNonQuery();
            }

            // 只对**新落库**事件入可疑队列(避免重传重复入队);用有效风险判阈值,url_unreadable 无视阈值。
            // agentRisk 低于阈值但 serverRisk 顶上去 → 记 agent_risk_understated,是篡改逃逸的取证信号。
            // M5 健康信号(source='health')无论风险分都入「采集健康」面板(纯提示,不污染作弊裁决率)。
            string healthKind = Suspicion.KindFor(sigType, payloadEl);
            bool isHealth = Suspicion.SourceForKind(healthKind) == "health";
            if (id is not null && typeStr != "heartbeat" &&
                (effRisk >= cfg.RiskThreshold || IsForcedReview(typeStr, payloadRaw) || isHealth))
            {
                string? tamperNote = !isHealth && serverRisk >= cfg.RiskThreshold && risk < cfg.RiskThreshold
                    ? $"agent_risk_understated agent={risk} server={serverRisk}" : null;
                EnqueueSuspicious(conn, examId, seatId, ts, typeStr, effRisk, id.Value, evidenceImageId, payloadRaw, tamperNote);
            }
        });

        // ---- 三道门的采集面写入口(贝塔通 P88–P92)----
        // ★ **复用既有上行通道**:Agent 本来就在持续上传事件流,不必另开一个心跳端点
        //   (其 rp-contract 对桌面客户端明写可以复用)。
        // ★★ `active` **由 Agent 自己定义**(采集机上有无用户活动),不是浏览器那套 —— 它更准。
        //   ★ **缺 `active` 字段时按 false 算**:那说明采集端版本比服务器旧,是部署事故。
        //   按 true 算能让它继续跑,但代价是 idle 那道门对那批机器**静默失效**,
        //   而失效这件事没有任何症状。宁可让它以「学生 30 分钟后被踢」的形态暴露出来。
        // ★ 放在写事务**之外**:Heartbeat 自己要取写锁,套在里面就是重入。
        if (typeStr == "heartbeat" && session is not null)
            sessions.Heartbeat(session.SessionId, TryGetPayloadBool(payloadRaw, "active") ?? false, Now());

        // 逐条 ack 本条 seq(不用范围 upto):即使 seq 空间有空洞,也不会误删从未送达的低 seq 事件
        await link.SendAsync(JsonSerializer.Serialize(new { v = 1, type = "ack", seq }), ct);
    }

    /// 从 payload 原文里取一个布尔;不存在 / 类型不符返回 null(由调用方决定缺省语义)。
    private static bool? TryGetPayloadBool(string payloadRaw, string key)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(payloadRaw);
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty(key, out JsonElement v))
                return v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
        }
        catch (JsonException) { /* payload 非法 JSON:当作没这个字段 */ }
        return null;
    }

    /// 抓不到 URL 的降级信号 = 强制人工复核(无视风险阈值),否则该兜底链断在最后一步。
    private static bool IsForcedReview(string typeStr, string payloadRaw)
        => typeStr == "browser_url" && TryGetPayloadStr(payloadRaw, "note") == "url_unreadable";

    /// 入可疑队列。**在调用方的写锁事务内**执行(与事件落库同一事务),故传入 conn 而非另开 db.Locked。
    private static void EnqueueSuspicious(SqliteConnection conn, string examId, string seatId, double ts, string typeStr,
        int score, long eventId, string? evidenceImageId, string payloadRaw, string? note)
    {
        SignalType type = ParseType(typeStr);
        JsonElement payload;
        try { using var pd = JsonDocument.Parse(payloadRaw); payload = pd.RootElement.Clone(); }
        catch { payload = default; }

        string kind = Suspicion.KindFor(type, payload);
        string source = Suspicion.SourceForKind(kind);   // M5 健康信号→health(只读面板)；其余→suspicion(可裁决)
        var refs = new List<string> { $"event:{eventId}" };
        if (evidenceImageId is not null) refs.Add($"image:{evidenceImageId}");
        string refsJson = JsonSerializer.Serialize(refs);

        using SqliteCommand c = conn.Cmd(
            @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs,note,source)
              VALUES (@e,@s,@ts,@k,@sc,'pending',@refs,@note,@src)",
            ("@e", examId), ("@s", seatId), ("@ts", ts), ("@k", kind), ("@sc", score),
            ("@refs", refsJson), ("@note", note), ("@src", source));
        c.ExecuteNonQuery();
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
        // M5 采集端硬化健康信号
        "watchdog_restart" => SignalType.WatchdogRestart,
        "suspected_suspend" => SignalType.SuspectedSuspend,
        "screenshot_obscured" => SignalType.ScreenshotObscured,
        "capability_degraded" => SignalType.CapabilityDegraded,
        _ => SignalType.WindowFocus,
    };
}
