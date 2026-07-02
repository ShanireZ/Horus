using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horus.Server.Identity;

/// M4·S3:采集面 OIDC 登录端点(**不走 admin gate** —— 由一次性 code + PKCE 保护,同 /ingest/*)。
///   POST /oidc/exchange —— Agent 用 loopback 收到的 code + PKCE verifier + 自己的 ECDH 公钥换取会话。
public static class OidcEndpoints
{
    public static void MapOidc(this WebApplication app)
    {
        OidcExchange? exchange = app.Services.GetService(typeof(OidcExchange)) as OidcExchange;

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
                ExamId: S("examId"), SeatId: S("seatId"), AgentId: S("agentId"), MachineId: (string?)body["machineId"]);

            if (req.Code.Length == 0 || req.CodeVerifier.Length == 0 || req.AgentEcdhPub.Length == 0
                || req.ExamId.Length == 0 || req.SeatId.Length == 0 || req.AgentId.Length == 0)
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
                profile = new
                {
                    sub = s.Sub, username = s.Username, nickname = s.Nickname, daoName = s.DaoName,
                    avatar = s.Avatar, realm = s.Realm, realmLevel = s.RealmLevel, combatPower = s.CombatPower,
                },
            });
        });
    }
}
