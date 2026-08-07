using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·S3:采集面 OIDC 登录端点(**不走 admin gate** —— 由一次性 code + PKCE 保护,同 /ingest/*)。
///   POST /oidc/exchange    —— Agent 用 loopback 收到的 code + PKCE verifier + 自己的 ECDH 公钥换取会话;
///                             examId 由服务端派发(当前活跃考试),seatId 由 OIDC 身份派生,响应携带二者供 Agent 采用。
///   GET  /oidc/active-exam —— 待命轮询:当前是否有活跃考试(Agent 常态待命,考试开始才弹登录、启采集)。
///   GET  /oidc/session     —— 会话探针(带 X-Horus-Session):会话是否仍有效 + 所绑考试状态
///                             (Agent 借此发现「离线期间被远程登出 / 考试已结束」,自行停采回待命)。
public static class OidcEndpoints
{
    public static void MapOidc(this WebApplication app)
    {
        OidcExchange? exchange = app.Services.GetService(typeof(OidcExchange)) as OidcExchange;
        Db db = app.Services.GetRequiredService<Db>();
        SessionStore sessions = app.Services.GetRequiredService<SessionStore>();

        app.MapPost("/oidc/exchange", async (HttpContext ctx) =>
        {
            if (exchange is null) return Results.Json(new { ok = false, error = "oidc_disabled" }, statusCode: 400);

            JsonNode? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
            catch (JsonException) { return Results.BadRequest(new { ok = false, error = "bad_json" }); }
            if (body is not JsonObject) return Results.BadRequest(new { ok = false, error = "bad_json" });

            string S(string k) => (string?)body[k] ?? "";
            var req = new OidcExchange.Request(
                Code: S("code"), CodeVerifier: S("codeVerifier"), RedirectUri: S("redirectUri"),
                Nonce: (string?)body["nonce"], AgentEcdhPub: S("agentEcdhPub"),
                AgentId: S("agentId"), MachineId: (string?)body["machineId"]);

            if (req.Code.Length == 0 || req.CodeVerifier.Length == 0 || req.AgentEcdhPub.Length == 0
                || req.AgentId.Length == 0)
                return Results.BadRequest(new { ok = false, error = "missing_fields" });

            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            OidcExchange.Result res = await exchange.ExchangeAsync(req, now, ctx.RequestAborted);
            if (!res.Ok || res.Session is null)
                return Results.Json(new { ok = false, error = res.Error }, statusCode: 401);

            HorusSession s = res.Session;
            return Results.Json(new
            {
                ok = true,
                sessionId = s.SessionId,
                serverEcdhPub = res.ServerEcdhPub,
                expiresAt = s.ExpiresAt,
                examId = s.ExamId,   // 服务端派发:Agent 采用此值填事件体(配置里已无 examId)
                seatId = s.SeatId,   // OIDC 身份派生(username):Agent 采用此值填事件体(配置里已无 seatId)
                // ★ 身份出口只剩三项(贝塔通 P81)。看板此前渲染的道号/境界/战力与
                //   监考员/学员标注均已随之移除 —— 那些是问天录的业务字段,不由身份中心分发。
                profile = new { sub = s.Sub, name = s.Name, username = s.Username },
            });
        });

        // 待命轮询(公开·无凭证):只暴露「有无活跃考试 + id/名称」,供未登录 Agent 决定何时弹 OIDC 登录。
        app.MapGet("/oidc/active-exam", () =>
        {
            ExamDispatch.ActiveExam? exam = ExamDispatch.ResolveActive(db);
            return exam is null
                ? Results.Json(new { active = false })
                : Results.Json(new { active = true, examId = exam.ExamId, name = exam.Name });
        });

        // 会话探针:sessionId 本身即凭证(不知者查不到任何信息)。valid=false 含「被远程登出/过期/不存在」。
        app.MapGet("/oidc/session", (HttpContext ctx) =>
        {
            string sid = ctx.Request.Headers["X-Horus-Session"].ToString();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            HorusSession? s = sessions.Get(sid, now);
            if (s is null) return Results.Json(new { valid = false });
            string? status = db.Read<string?>(conn =>
            {
                using SqliteCommand c = conn.Cmd("SELECT status FROM exams WHERE exam_id=@e", ("@e", s.ExamId));
                return c.ExecuteScalar() as string;
            });
            return Results.Json(new { valid = true, examId = s.ExamId, seatId = s.SeatId, examStatus = status ?? "unknown" });
        });
    }
}
