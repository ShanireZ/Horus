using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 贝塔通**存活探测**的接收端(其 P94,契约见 BetaPass `docs/rp-contract.md`「`GET /internal/health`:必办项」)。
///
/// 贝塔通**每 5 分钟**打一次;**连续 3 次失败(15 分钟)即判本站离线**,并**暂停对本站的撤权重投**,
/// 探到回来之后才从头重投一遍。
///
/// ★★★ **「不实现它 = 撤权通知永远送不到」这条理由已经不成立,别再照它推**(2026-08-11 实测)。
///   对侧 `5bb16b6` 之后 `health/probe.ts` 只在**连不上**时判离线,**任何 HTTP 应答都算在线** ——
///   包括根本没实现这个路由时框架回的 404。其 `rp-contract.md` 里那句(以及本仓此前的注释)
///   都还停在旧判据上。
///   ★★ **教训比这条事实本身更值钱**:把「为什么这么做」挂在**对侧某个行为**上,
///   对侧一改,理由就烂掉而结论看起来还是对的 —— 而**烂掉的理由比错的结论更难发现,
///   因为没有任何门会去验理由**。下面三条是重写过的、不挂在对侧当前实现上的理由。
///
/// **那为什么仍然要实现它**:
/// ① **契约把它列为必办项**(P94) —— 合规本身就是理由,不需要再借一个后果来加码;
/// ② ★ **对侧的判据已经改过一次,就可能改回去**。真改回去的那一刻,没实现的站
///    **立刻**失效,而失效**没有任何症状**(「权限撤了、人还在里面」不会被谁报上来)。
///    实现它的成本近乎零,而这个不对称是实打实的;
/// ③ 实现了对侧后台才是干净的「在线」;没实现是「在线但 `HTTP 404`」——
///    ★ 那正是他们用来发现「这家 RP 压根没实现探活」的信号(见其 `probe.ts` 对 `error` 的说明)。
///
/// 三条硬要求,逐条落在下面:
/// ① **必须验签**(与 `/internal/revoke` 同一套:拉 `/jwks`、按 `kid` 取公钥、`PS256`、`aud` 是不是自己)。
///    不验就等于把「贝塔通接了哪些站」这份接入拓扑白送给任何能打到这个地址的人。
/// ② 回 **204 无响应体**。
///    ⚠ **判据以对侧代码为准,不以其契约文本为准**:`docs/rp-contract.md` 里
///    「任意 2xx 即算在线 / 非 2xx 会被判离线」那类说法**反复与 `health/probe.ts` 对不上**
///    (2026-08-11 一天之内在三处各出现一次,修了两处又在新写的一节里复发)。
///    ★ 对本端的实现没有影响(成功本来就回 204),但**别照那句去推断非 2xx 的后果**。
///    ★★ 要确认当下口径就跑这一条,别读文件也别读契约:
///      `git -C ../BetaPass show HEAD:src/identity/health/probe.ts | grep res.ok`
/// ③ ★ **不查库、不做重活** —— 它每 5 分钟就来一次,而它要回答的问题只有「这个进程还在不在」。
///    （连会话表都不碰:会话是不是空的与「本站在不在线」无关。）
///
/// ★ **为什么不复用 `/internal/revoke` 当探活**:那个端点会**真的清会话**,
///   拿它探活等于每 5 分钟把全体用户从各站点踢一次。这是贝塔通专门开一个新端点的原因。
public static class BetapassHealthEndpoint
{
    /// ★ 地址由贝塔通按**登记的 `revoke_callback_url` 同源**推导(其 `health/probe.ts`),
    ///   所以路径固定为 `/internal/health`,不能改名 —— 除非在后台单独登记 `health_check_url` 覆盖。
    public const string Path = "/internal/health";

    public static void Map(WebApplication app)
    {
        app.MapGet(Path, (HttpContext ctx) =>
        {
            ServerConfig cfg = ctx.RequestServices.GetRequiredService<ServerConfig>();

            // 两条链路都没开 OIDC:本机根本不是贝塔通的 RP,也没有可验签的公钥。
            // 回 410 与 `/internal/revoke` 同口径 = 「别再当我是接入方」。
            // ★ 它**不会**把本站判成离线(见类注释②:对侧只有连不上才判离线),
            //   而是被如实记成一条 `HTTP 410` 摆在其后台 —— 这正是要的:
            //   「这台机器不是 RP」应该有人看见,而不是安静地当作在线。
            if (!cfg.OidcEnabled && !cfg.DashboardOidcEnabled)
                return Results.StatusCode(StatusCodes.Status410Gone);

            var probeState = ctx.RequestServices.GetRequiredService<BetapassProbeState>();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            string? bearer = ReadBearer(ctx.Request.Headers.Authorization.ToString());
            if (bearer is null)
            {
                Reject(ctx, probeState, now, "无 Bearer 令牌");
                return Results.Unauthorized();
            }

            var verifier = ctx.RequestServices.GetRequiredService<BetapassRevokeVerifier>();
            try
            {
                verifier.VerifyProbe(bearer);
            }
            catch (OidcValidationException ex)
            {
                Reject(ctx, probeState, now, ex.Message);
                return Results.Unauthorized();
            }

            // ★ 成功**不记日志**:它每 5 分钟来一次,一条正常日志就会把上面那条告警淹掉 ——
            //   「告警被正常日志埋掉」与「压根没告警」在实际使用中是同一回事。
            //   要看「上一次通过是什么时候」去 `GET /api/preflight` 的 `health_audience`。
            probeState.RecordOk(now);
            return Results.NoContent();   // 204·无响应体
        });
    }

    /// 记下这一发被拒,并**按分钟节流**地叫一声。
    ///
    /// ★ 告警里带上**本机在等哪些 `aud`** —— 排查「探活为什么恒 401」时第一个要问的就是这个,
    ///   而它只有服务器自己知道(对侧后台只看得到一个 401)。
    private static void Reject(HttpContext ctx, BetapassProbeState state, double now, string reason)
    {
        if (!state.RecordRejected(now, reason)) return;
        var verifier = ctx.RequestServices.GetService(typeof(BetapassRevokeVerifier)) as BetapassRevokeVerifier;
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BetapassHealth")
            .LogWarning("贝塔通探活被拒:{Reason}。本机在等的 aud={Expected}(60 秒内不再重复此告警)",
                reason, verifier is null ? "(未构造)" : string.Join(" / ", verifier.ProbeAudiences));
    }

    private static string? ReadBearer(string header)
    {
        string[] parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }
}
