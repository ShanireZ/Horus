using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 贝塔通**存活探测**的接收端(其 P94,契约见 BetaPass `docs/rp-contract.md`「`GET /internal/health`:必办项」)。
///
/// 贝塔通**每 5 分钟**打一次;**连续 3 次失败(15 分钟)即判本站离线**,并**暂停对本站的撤权重投**,
/// 探到回来之后才从头重投一遍。
///
/// ★★ **不实现它的后果不是「少个功能」,而是「撤权通知永远送不到」** ——
///   贝塔通会永远认为 Horus 离线。而「权限撤了、人还在里面」这件事**没有任何症状**,
///   所以它不会被谁报上来。这正是它被列为必办项的判据。
///
/// 三条硬要求,逐条落在下面:
/// ① **必须验签**(与 `/internal/revoke` 同一套:拉 `/jwks`、按 `kid` 取公钥、`PS256`、`aud` 是不是自己)。
///    不验就等于把「贝塔通接了哪些站」这份接入拓扑白送给任何能打到这个地址的人。
/// ② 回 **204 无响应体**。
///    ★ **判据已经变过一次**(对侧 `5bb16b6`,2026-08-11):**只有连不上才判离线**,
///    非 2xx **不再**判离线(答得上话就说明进程活着),状态码照记供其后台如实展示。
///    ⚠ 其 `docs/rp-contract.md` 那张表仍写着「任意 2xx 即算在线」—— **以它的 `health/probe.ts` 为准**,
///    契约文本那一行还没跟上。对本端的实现没有影响(成功本来就回 204),
///    但**别照那句去推断非 2xx 的后果**。
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
