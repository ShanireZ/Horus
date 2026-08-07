using System.Text.Json;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 贝塔通**撤权通知**的接收端(其 P44 / P69,契约见 BetaPass `docs/rp-contract.md`)。
///
/// 贝塔通侧凭据失效时主动 POST 过来,报文:
/// `{ jti, sub, platform, client_id, reason }`,`Authorization: Bearer <PS256 JWT>`。
///
/// ★★ **这是唯一的登出传播路径**(贝塔通 P22 不做通用登出广播)。不接等于
/// 「权限撤了 / 密码改了,人还在里面待着」—— 本地会话在自己有效期内不回查 IdP,
/// 所以**本地会话时长就是「撤销后最长还能用多久」的上界**。
///
/// 三条契约硬要求,逐条落在下面:
/// ① **验签(含 `aud` 是不是自己)** —— 不验就等于开了一个「谁都能踢人下线」的端点;
/// ② **按 `jti` 幂等** —— 贝塔通的 HTTP 超时只有 5 秒,处理成功但花了 6 秒的一发会被判失败
///    并重投,同一个 `jti` 必然来第二次。重复时回 2xx 即可,不要报错;
/// ③ **清掉该 `sub` 的本地会话,然后回 2xx**。
///
/// ★ **不得按 `reason` 决定要不要清** —— 三种 reason 处置完全相同,区分只服务提示语;
///   **认不出的新值也要照清**,否则贝塔通那边加一种 reason 就等于给老 RP 开了一个静默旁路。
///
/// ★★ **Horus 有两个客户端、分属两个平台**(贝塔通 P83):`horus-client` → 平台 `horus`(采集面),
///   `horus-dashboard` → 平台 `horus-admin`(监考台)。**按令牌的 `aud` 分别处置** ——
///   撤掉某人的监考员资格不该把他作为考生的采集会话也断掉,反之亦然。
///   这正是拆平台换来的精确性,别图省事两边一起清。
public static class BetapassRevokeEndpoint
{
    /// 判成功的口径是 2xx。**404 / 405 与 5xx 一样会被贝塔通重投** ——
    /// 「RP 还没实现这个端点」正是最需要重投的情形,所以这个路由必须始终挂上:
    /// 未启用 OIDC 时也回 2xx 之外的明确状态,而不是让它落到 404 去被无限重投。
    public const string Path = "/internal/revoke";

    public sealed record Notice(string Jti, string Sub, string ClientId, string Reason);

    public static void Map(WebApplication app)
    {
        app.MapPost(Path, async (HttpContext ctx) =>
        {
            ServerConfig cfg = ctx.RequestServices.GetRequiredService<ServerConfig>();
            ILogger log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BetapassRevoke");

            if (!cfg.OidcEnabled && !cfg.DashboardOidcEnabled)
            {
                // 两条链路都没开 OIDC:这台机器根本不该收到通知。回 410 而不是 404 ——
                // 404 会被贝塔通当成「端点还没上线」而重投 12 次,410 是「别再投了」。
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            string? bearer = ReadBearer(ctx.Request.Headers.Authorization.ToString());
            if (bearer is null) return Results.Unauthorized();

            Notice notice;
            try
            {
                notice = await VerifyAsync(ctx, cfg, bearer);
            }
            catch (OidcValidationException ex)
            {
                log.LogWarning("撤权通知验签失败:{Msg}", ex.Message);
                return Results.Unauthorized();
            }

            // 报文与令牌必须一条条对上 —— 令牌是权威,报文只是便于阅读的副本。
            // 对不上说明有人拿一枚合法令牌套了别人的报文。
            var body = await JsonSerializer.DeserializeAsync<JsonElement?>(ctx.Request.Body);
            if (body is null || !BodyMatches(body.Value, notice))
                return Results.BadRequest(new { error = "body_does_not_match_token" });

            var db = ctx.RequestServices.GetRequiredService<Db>();
            var sessions = ctx.RequestServices.GetRequiredService<SessionStore>();
            var adminSessions = ctx.RequestServices.GetRequiredService<AdminSessionStore>();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            // ② 幂等:同一个 jti 再来一次,原样回上次的答案,**不重复清、不报错**。
            int? prior = LookupPrior(db, notice.Jti);
            if (prior is not null)
                return Results.Ok(new { duplicate = true, revoked_sessions = prior.Value });

            // ③ 清会话。★ 按 aud 分别处置(见类注释):撤监考台不动采集面,反之亦然。
            //    ★ 不看 reason —— 三种处置相同,认不出的新值也照清。
            int revoked = string.Equals(notice.ClientId, cfg.OidcDashboardClientId, StringComparison.Ordinal)
                ? adminSessions.RevokeBySub(notice.Sub)
                : sessions.RevokeBySub(notice.Sub);

            Record(db, notice, now, revoked);
            log.LogInformation("撤权通知已处理 sub={Sub} client={Client} reason={Reason} 清会话={N}",
                notice.Sub, notice.ClientId, notice.Reason, revoked);
            return Results.Ok(new { duplicate = false, revoked_sessions = revoked });
        });
    }

    private static string? ReadBearer(string header)
    {
        string[] parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    /// 验签 + 取 claims。
    /// ★ 与验 id_token **共用同一套公钥**(贝塔通就是同一套签名密钥),所以复用
    ///   <see cref="OidcTokenValidator"/> —— 零新增密钥材料,轮转时这条链路自动跟着走。
    /// ★ `aud` 必须是**本机登记的两个 client_id 之一**;不是就拒。这一条就是「谁都能踢人下线」
    ///   与「只有贝塔通能踢」的全部差别。
    private static Task<Notice> VerifyAsync(HttpContext ctx, ServerConfig cfg, string token)
    {
        string[] candidates = [.. new[] { cfg.OidcClientId, cfg.OidcDashboardClientId }
            .Where(one => !string.IsNullOrEmpty(one)).Select(one => one!)];
        if (candidates.Length == 0) throw new OidcValidationException("本机没有登记任何 client_id");

        string jwks = ctx.RequestServices.GetRequiredService<BetapassJwks>().Json;
        foreach (string aud in candidates)
        {
            try
            {
                // 撤权令牌**没有 nonce**(不是登录流程),因此这里传 null 跳过 nonce 校验;
                // iss / aud / exp / 签名 / alg 五项一个不少。
                var v = new OidcTokenValidator(jwks, cfg.OidcIssuer!, aud);
                JsonElement payload = v.ValidatePayload(token, expectedNonce: null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                string jti = Req(payload, "jti");
                string sub = Req(payload, "sub");
                // 令牌里的 `purpose` 与报文里的 `reason` 同源,用哪个都行(契约明写)。
                string reason = Opt(payload, "purpose") ?? "";
                return Task.FromResult(new Notice(jti, sub, aud, reason));
            }
            catch (OidcValidationException)
            {
                // aud 不是这一个,试下一个。★ 全部试完仍不过才算失败。
            }
        }
        throw new OidcValidationException("撤权令牌的 aud 不是本机任何一个 client_id");
    }

    private static bool BodyMatches(JsonElement body, Notice n)
        => body.ValueKind == JsonValueKind.Object
           && Opt(body, "jti") == n.Jti
           && Opt(body, "sub") == n.Sub
           && Opt(body, "client_id") == n.ClientId;

    private static int? LookupPrior(Db db, string jti)
        => db.Read<int?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "SELECT revoked_count FROM betapass_revocations WHERE jti=@j", ("@j", jti));
            object? v = c.ExecuteScalar();
            return v is null or DBNull ? null : Convert.ToInt32(v);
        });

    private static void Record(Db db, Notice n, double now, int revoked)
        => db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT OR IGNORE INTO betapass_revocations (jti,sub,client_id,reason,received_at,revoked_count)
                  VALUES (@j,@s,@c,@r,@t,@n)",
                ("@j", n.Jti), ("@s", n.Sub), ("@c", n.ClientId), ("@r", n.Reason), ("@t", now), ("@n", revoked));
            c.ExecuteNonQuery();
        });

    private static string Req(JsonElement o, string k)
        => Opt(o, k) ?? throw new OidcValidationException($"撤权令牌缺 {k}");

    private static string? Opt(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out JsonElement e)
           && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
