using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:监考员看板 OIDC 登录端点(**不走 admin gate** —— 本就是获取管理凭证的入口,且不在 /api 下)。
///   GET /admin/login —— 重定向到 wentian 授权页(生成 state+nonce+PKCE)。
///   GET /cb          —— wentian 回调:换 token → 验 id_token → 须 elder → 建管理会话 → 种 HttpOnly cookie → 跳看板。
public static class AdminOidcEndpoints
{
    public static void MapAdminOidc(this WebApplication app)
    {
        AdminOidcFlow? flow = app.Services.GetService(typeof(AdminOidcFlow)) as AdminOidcFlow;
        ServerConfig cfg = app.Services.GetRequiredService<ServerConfig>();
        ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminOidc");

        app.MapGet("/admin/login", (HttpContext ctx) =>
        {
            if (flow is null) return Results.Json(new { ok = false, error = "admin_oidc_disabled" }, statusCode: 404);
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            string url = flow.BeginLogin(now);
            return Results.Redirect(url);
        });

        app.MapGet("/cb", async (HttpContext ctx) =>
        {
            if (flow is null) return Results.Json(new { ok = false, error = "admin_oidc_disabled" }, statusCode: 404);

            // ★★ **无权限现在走这条错误回调,不再走「换到令牌之后发现不是长老」**(贝塔通 P83)。
            //   没有 `horus-admin` 平台权限的账号**在贝塔通的授权阶段就被拒**(其 §3.2),
            //   于是回调带的是 `error=access_denied` 而**没有 code** —— 拿不到 code、换不到令牌、
            //   **没有本地会话**。按其 rp-contract「无权限时停在未登录」:只换提示语,
            //   ★ **必须给一句人话,不得显示成「系统错误」**;
            //   ★ 再点一次登录会得到**同一个结果**(贝塔通已有会话、秒回、仍然无权限),
            //     这是对的行为 —— 但别把它做成无限转圈,所以这里是一屏静态页而不是自动重试。
            string oauthError = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(oauthError))
            {
                string denied = oauthError == "access_denied"
                    ? "你的账号还没有开通「贝塔天目·监考台」权限，请联系管理员开通后再登录。"
                    : "贝塔通没有完成这次授权，请重试。";
                return Results.Content(ErrorPage(denied), "text/html; charset=utf-8",
                    statusCode: oauthError == "access_denied" ? 403 : 400);
            }

            string code = ctx.Request.Query["code"].ToString();
            string state = ctx.Request.Query["state"].ToString();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            AdminOidcFlow.Result res = await flow.CompleteAsync(code, state, now, ctx.RequestAborted);
            if (!res.Ok || res.Session is null)
            {
                // 走到这里说明拿到了 code 却没换成会话 —— 那是协议/网络层的问题,不是权限问题
                // (权限问题上面已经拦掉了)。不泄细节。
                return Results.Content(ErrorPage("登录失败，请重试。"), "text/html; charset=utf-8", statusCode: 400);
            }

            // 种管理会话 cookie(HttpOnly·SameSite=Lax 便于登录后 top-level 跳转携带;不设 Secure 因自签 https 亦可,浏览器对 https 会发)。
            ctx.Response.Cookies.Append("horus_admin", res.Session.SessionId, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
            });
            log.LogInformation("监考员会话种 cookie sub={Sub}", res.Session.Sub);
            return Results.Redirect("/");
        });
    }

    private static string ErrorPage(string message) =>
        "<!doctype html><html lang=\"zh\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Horus 监考</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;background:#0f1115;color:#e6e6e6;display:flex;" +
        "align-items:center;justify-content:center;height:100vh;margin:0\">" +
        "<div style=\"text-align:center;max-width:32rem;padding:2rem\">" +
        "<h1 style=\"font-size:1.25rem\">Horus 监考看板</h1>" +
        "<p style=\"color:#f38ba8\">" + System.Net.WebUtility.HtmlEncode(message) + "</p>" +
        "<p><a href=\"/admin/login\" style=\"color:#89b4fa\">重新登录</a></p>" +
        "</div></body></html>";
}
