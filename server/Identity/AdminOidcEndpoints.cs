using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:监考员看板 OIDC 登录端点(**不走 admin gate** —— 本就是获取管理凭证的入口,且不在 /api 下)。
///   GET /admin/login —— 重定向到 cpplearn 授权页(生成 state+nonce+PKCE)。
///   GET /cb          —— cpplearn 回调:换 token → 验 id_token → 须 elder → 建管理会话 → 种 HttpOnly cookie → 跳看板。
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
            string code = ctx.Request.Query["code"].ToString();
            string state = ctx.Request.Query["state"].ToString();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            AdminOidcFlow.Result res = await flow.CompleteAsync(code, state, now, ctx.RequestAborted);
            if (!res.Ok || res.Session is null)
            {
                // 弟子(非监考员)明确 403 + 可读页;其余登录错误给通用页(不泄细节)。
                int status = res.Error == "not_proctor" ? 403 : 400;
                string msg = res.Error == "not_proctor"
                    ? "你不是监考员（长老），无权访问 Horus 监考看板。"
                    : "登录失败，请重试。";
                return Results.Content(ErrorPage(msg), "text/html; charset=utf-8", statusCode: status);
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
