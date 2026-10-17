using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·S3:採集面 OIDC 登入端點(**不走 admin gate** —— 由一次性 code + PKCE 保護,同 /ingest/*)。
///   POST /oidc/exchange    —— Agent 用 loopback 收到的 code + PKCE verifier + 自己的 ECDH 公鑰換取工作階段;
///                             examId 由伺服器端派發(當前活躍考試),seatId 由 OIDC 身分派生,回應攜帶二者供 Agent 採用。
///   GET  /oidc/active-exam —— 待命輪詢:當前是否有活躍考試(Agent 常態待命,考試開始才彈登入、啟採集)。
///   GET  /oidc/session     —— 工作階段探針(帶 X-Horus-Session):工作階段是否仍有效 + 所綁考試狀態
///                             (Agent 藉此發現「離線期間被遠程登出 / 考試已結束」,自行停採回待命)。
public static class OidcEndpoints
{
    public static void MapOidc(this WebApplication app)
    {
        OidcExchange? exchange = app.Services.GetService(typeof(OidcExchange)) as OidcExchange;
        Db db = app.Services.GetRequiredService<Db>();
        SessionStore sessions = app.Services.GetRequiredService<SessionStore>();
        Horus.Server.Config.ServerConfig cfg = app.Services.GetRequiredService<Horus.Server.Config.ServerConfig>();

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
                // 開考預檢(貝塔通 P91):把「一場考試預計多久」下發給 Agent,讓它自己拿
                // expiresAt 算夠不夠。★ 原設計是靠 IdP 下發「你的工作階段還剩多久」的 claim,
                //   取消 SSO 後作廢 —— 判據改由 Horus 自己給,兩個數都在自己手裡。
                expectedExamMinutes = cfg.ExpectedExamMinutes,
                examId = s.ExamId,   // 伺服器端派發:Agent 採用此值填事件體(設定裡已無 examId)
                seatId = s.SeatId,   // OIDC 身分派生(username):Agent 採用此值填事件體(設定裡已無 seatId)
                // ★ 身分出口只剩三項(貝塔通 P81)。看板此前繪製的道號/境界/戰力與
                //   監考員/學員標註均已隨之移除 —— 那些是問天錄的業務欄位,不由身分中心散布。
                profile = new { sub = s.Sub, name = s.Name, username = s.Username },
            });
        });

        // 待命輪詢(公開·無憑證):只暴露「有無活躍考試 + id/名稱」,供未登入 Agent 決定何時彈 OIDC 登入。
        app.MapGet("/oidc/active-exam", () =>
        {
            ExamDispatch.ActiveExam? exam = ExamDispatch.ResolveActive(db);
            return exam is null
                ? Results.Json(new { active = false })
                : Results.Json(new { active = true, examId = exam.ExamId, name = exam.Name });
        });

        // 工作階段探針:sessionId 本身即憑證(不知者查不到任何資訊)。valid=false 含「被遠程登出/過期/不存在」。
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
