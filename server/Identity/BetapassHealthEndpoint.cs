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
///    ⚠ 其 `docs/rp-contract.md` 那张表仍写着「任意 2xx 即算在线」,而它自己的
///    `health/probe.ts` 已改成「只有连不上才算离线」—— 两者不一致时**以代码为准**。
///    对本端的实现没有影响(成功本来就回 204),但**别照那句去推断非 2xx 的后果**。
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

            string? bearer = ReadBearer(ctx.Request.Headers.Authorization.ToString());
            if (bearer is null) return Results.Unauthorized();

            try
            {
                ctx.RequestServices.GetRequiredService<BetapassRevokeVerifier>().VerifyProbe(bearer);
            }
            catch (OidcValidationException ex)
            {
                // ★ 只 Debug 级:它每 5 分钟来一次,验不过时按 Warning 记会把日志淹掉,
                //   而真正需要被看见的是**探不通**(那一端在贝塔通的后台里有状态),不是这里。
                ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("BetapassHealth").LogDebug("探活令牌验签失败:{Msg}", ex.Message);
                return Results.Unauthorized();
            }

            return Results.NoContent();   // 204·无响应体
        });
    }

    private static string? ReadBearer(string header)
    {
        string[] parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }
}
