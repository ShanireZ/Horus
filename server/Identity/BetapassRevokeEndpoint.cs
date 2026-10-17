using System.Text.Json;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 貝塔通**撤權通知**的接收端(其 P44 / P69,契約見 BetaPass `docs/rp-contract.md`)。
///
/// ⏳★★ **本端目前不會被呼叫**(貝塔通 **P101**,owner 2026-08-11):Horus Server 是區域網路機、
///   **沒有可撥位址**(★ 出站≠可入站),因此**有意不登記 `revoke_callback_url`**。
///   ★ 實作與兩道防線(驗籤 + 「權杖 `sub` == 訊息 `sub`」)**照舊保留** ——
///   走拉取模型那天要用同一套驗籤與處置,刪了將來還得重寫。
///   ★ **殘餘風險由三道門兜住**:撤權後最長 ≤ absolute(6 小時),且**再也登不進來**(授權階段就被拒)。
///   判據全文見 <see cref="Config.ServerConfig.BetapassRevokePushExpected"/>。
///
/// 貝塔通側憑證失效時主動 POST 過來,訊息:
/// `{ jti, sub, platform, client_id, reason }`,`Authorization: Bearer <PS256 JWT>`。
///
/// ★★ **這是唯一的登出傳播路徑**(貝塔通 P22 不做通用登出廣播)。不接等於
/// 「權限撤了 / 密碼改了,人還在裡面待著」—— 本機工作階段在自己有效期內不回查 IdP,
/// 所以**本機工作階段時長就是「撤銷後最長還能用多久」的上界**。
///
/// 三條契約硬要求,逐條落在下面:
/// ① **驗籤(含 `aud` 是不是自己)** —— 不驗就等於開了一個「誰都能踢人下線」的端點;
/// ② **按 `jti` 冪等** —— 貝塔通的 HTTP 超時只有 5 秒,處理成功但花了 6 秒的一發會被判失敗
///    並重投,同一個 `jti` 必然來第二次。重複時回 2xx 即可,不要回報錯誤;
/// ③ **清掉該 `sub` 的本機工作階段,然後回 2xx**。
///
/// ★ **不得按 `reason` 決定要不要清** —— 三種 reason 處置完全相同,區分只服務提示語;
///   **認不出的新值也要照清**,否則貝塔通那邊加一種 reason 就等於給老 RP 開了一個靜默旁路。
///
/// ★★ **Horus 有兩個用戶端、分屬兩個平台**(貝塔通 P83):`horus-client` → 平台 `horus`(採集面),
///   `horus-dashboard` → 平台 `horus-admin`(監考臺)。**按權杖的 `aud` 分別處置** ——
///   撤掉某人的監考員資格不該把他作為考生的採集工作階段也斷掉,反之亦然。
///   這正是拆平台換來的精確性,別圖省事兩邊一起清。
public static class BetapassRevokeEndpoint
{
    /// 判成功的口徑是 2xx。**404 / 405 與 5xx 一樣會被貝塔通重投** ——
    /// 「RP 還沒實作這個端點」正是最需要重投的情形,所以這個路由必須始終掛上:
    /// 未啟用 OIDC 時也回 2xx 之外的明確狀態,而不是讓它落到 404 去被無限重投。
    ///
    /// ★★ **重投走完不再進終態**(貝塔通 P93,2026-08-10 取消 `dead`):階梯是
    ///   **5min ×3 → 15min → 30min → 1h,共 6 次重投**,走完轉「等待探活」掛起,
    ///   等 <see cref="BetapassHealthEndpoint"/> 探到本站回來之後**從頭再投一遍,直至成功**。
    ///   對本端的兩條實際影響:
    ///   ① 同一條通知會被重投**很多次** —— `jti` 冪等臺賬正好覆蓋,**不要動它**;
    ///   ② 本站長時間下線再回來時會**一次性收到一批積壓的通知** ——
    ///      ★ **不得因為「這條太舊了」而丟棄**,舊不代表已經處理過。
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
                // 兩條連線路徑都沒開 OIDC:這臺機器根本不該收到通知。回 410 而不是 404 ——
                // 404 會被貝塔通當成「端點還沒上線」而一路重投,410 是「別再投了」。
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            string? bearer = ReadBearer(ctx.Request.Headers.Authorization.ToString());
            if (bearer is null) return Results.Unauthorized();

            Notice notice;
            try
            {
                notice = ctx.RequestServices.GetRequiredService<BetapassRevokeVerifier>().Verify(bearer);
            }
            catch (OidcValidationException ex)
            {
                log.LogWarning("撤权通知验签失败:{Msg}", ex.Message);
                return Results.Unauthorized();
            }

            // 訊息與權杖必須一條條對上 —— 權杖是權威,訊息只是便於閱讀的副本。
            // 對不上說明有人拿一枚合法權杖套了別人的訊息。
            var body = await JsonSerializer.DeserializeAsync<JsonElement?>(ctx.Request.Body);
            if (body is null || !BodyMatches(body.Value, notice))
                return Results.BadRequest(new { error = "body_does_not_match_token" });

            var db = ctx.RequestServices.GetRequiredService<Db>();
            var sessions = ctx.RequestServices.GetRequiredService<SessionStore>();
            var adminSessions = ctx.RequestServices.GetRequiredService<AdminSessionStore>();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            // ② 冪等:同一個 jti 再來一次,原樣回上次的答案,**不重複清、不回報錯誤**。
            int? prior = LookupPrior(db, notice.Jti);
            if (prior is not null)
                return Results.Ok(new { duplicate = true, revoked_sessions = prior.Value });

            // ③ 清工作階段。★ 按 aud 分別處置(見類註解):撤監考臺不動採集面,反之亦然。
            //    ★★ **`reason` 只被帶下去當提示語,不參與「要不要清」的判斷** ——
            //    四種處置完全相同,**認不出的新值也照清**。這兩件事在這一行裡是分開的:
            //    分支只看 `ClientId`,`Reason` 只往 RevokeBySub 的留痕裡走。
            int revoked = string.Equals(notice.ClientId, cfg.OidcDashboardClientId, StringComparison.Ordinal)
                ? adminSessions.RevokeBySub(notice.Sub, notice.Reason, now)
                : sessions.RevokeBySub(notice.Sub, notice.Reason, now);

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

    internal static bool BodyMatches(JsonElement body, Notice n)
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

    internal static string? Opt(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out JsonElement e)
           && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
