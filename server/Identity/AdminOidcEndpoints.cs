using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:监考员看板 OIDC 登录端点(**不走 admin gate** —— 本就是获取管理凭证的入口,且不在 /api 下)。
///   GET /admin/login  —— 重定向到贝塔通授权页(生成 state+nonce+PKCE)。
///   GET /cb           —— 贝塔通回调:换 token → 验 id_token → 取 userinfo → 建管理会话 → 种 HttpOnly cookie → 跳看板。
///   GET /admin/logout —— **先清本地会话**,再跳贝塔通的退出范围二选一页(其 P85)。
///   GET /logout/done  —— 回跳落点;★ 在这里**再清一次**本地会话。
public static class AdminOidcEndpoints
{
    public static void MapAdminOidc(this WebApplication app)
    {
        AdminOidcFlow? flow = app.Services.GetService(typeof(AdminOidcFlow)) as AdminOidcFlow;
        ServerConfig cfg = app.Services.GetRequiredService<ServerConfig>();
        AdminSessionStore adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminOidc");

        // 清本地会话 + 清 cookie。幂等,调用两次无害(登出与回跳各调一次)。
        void ClearLocal(HttpContext ctx)
        {
            adminSessions.Delete(ctx.Request.Cookies["horus_admin"] ?? "");
            ctx.Response.Cookies.Delete("horus_admin", new CookieOptions { Path = "/" });
        }

        // ---- 退出:RP-Initiated Logout(贝塔通 P85)----
        //
        // 用户点退出 → 跳 `end_session_endpoint` → **贝塔通给他两个按钮**:
        //   「只退出本站」  → 撤掉本 client 的 grant,重定向回 post_logout_redirect_uri;
        //   「退出所有站点」→ 销毁全部授权,并向全部 RP 广播 `/internal/revoke`(reason=user_logout)。
        //
        // ★★ **先清本地、再跳走。** 两条理由:
        //   ① fail-closed —— 贝塔通不可达时跳转会失败,而那时用户**本地已经登出了**;
        //      反过来「先跳、回来再清」在 IdP 挂掉的那一刻就是「点了退出但还登录着」。
        //   ② 「只退出本站」上游**只撤 grant,碰不到你的本地会话** —— 契约点名这是最容易漏的一步。
        //      本仓在两处都清(这里 + 回跳落点),因为回跳未必回得来。
        app.MapGet("/admin/logout", (HttpContext ctx) =>
        {
            ClearLocal(ctx);

            // 未启用 OIDC(静态令牌模式)→ 没有 IdP 会话这回事,清完直接给「已退出」页。
            string? endSession = cfg.OidcEndSessionEndpoint;
            if (!cfg.DashboardOidcEnabled || endSession is null || string.IsNullOrEmpty(cfg.OidcDashboardClientId))
                return Results.Redirect("/logout/done");

            // ★ `client_id` 不能省:上游要靠它(或 id_token_hint)才认 `post_logout_redirect_uri`。
            //   本仓不存 id_token(它在登录时消费一次就丢),所以走 client_id 这条。
            var q = new List<string> { "client_id=" + Uri.EscapeDataString(cfg.OidcDashboardClientId!) };
            string? back = cfg.PostLogoutRedirectUriEffective;
            // ★ 取不到回跳地址时**照样跳**,只是不带这个参数 —— 用户会停在贝塔通自己的「已退出」页。
            //   本地已经清干净了,所以那是「少一次回跳」而不是「没退出」。
            if (!string.IsNullOrEmpty(back)) q.Add("post_logout_redirect_uri=" + Uri.EscapeDataString(back!));
            return Results.Redirect(endSession + "?" + string.Join("&", q));
        });

        // ---- 回跳落点 ----
        // ★★ **必须在这里再清一次本地会话。** 「只退出本站」那一支上游只撤 grant,
        //   你的本地会话它碰不到 —— 漏了的表现是「用户点了退出、跳回来了,然后发现自己还登录着」。
        //   ★ 本端在 `/admin/logout` 里已经清过一次,这一发是**兜底**:回跳可能由别的路径进来
        //   (贝塔通侧的账号中心「退出所有站点」也会把人送到这里),那条路径没经过上面那个端点。
        app.MapGet("/logout/done", (HttpContext ctx) =>
        {
            ClearLocal(ctx);
            return Results.Content(ByePage(), "text/html; charset=utf-8");
        });

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
            //   ★ 再点一次登录会得到**同一个结果**,所以这里是一屏静态页而不是自动重试。
            //   ★★ **2026-08-10 起不再「秒回」**(贝塔通 P84 取消 SSO / P98):以前贝塔通那边
            //     还有会话,无权限的人再点一次是秒回同一个拒绝页;现在**每点一次都要完整输一遍
            //     密码**才被拒。owner 明确不做缓解 —— 但**无权限页不放「重新登录」**,
            //     那等于请他再白输一遍密码。
            string oauthError = ctx.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(oauthError))
            {
                bool denied = oauthError == "access_denied";
                string msg = denied
                    ? "你的账号还没有开通「贝塔天目·监考台」权限，请联系管理员开通后再登录。"
                    : "贝塔通没有完成这次授权，请重试。";
                return Results.Content(ErrorPage(msg, retryLink: !denied), "text/html; charset=utf-8",
                    statusCode: denied ? 403 : 400);
            }

            string code = ctx.Request.Query["code"].ToString();
            string state = ctx.Request.Query["state"].ToString();
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            AdminOidcFlow.Result res = await flow.CompleteAsync(code, state, now, ctx.RequestAborted);
            if (!res.Ok || res.Session is null)
            {
                // 走到这里说明拿到了 code 却没换成会话 —— 那是协议/网络层的问题,不是权限问题
                // (权限问题上面已经拦掉了)。不泄细节。
                return Results.Content(ErrorPage("登录失败，请重试。", retryLink: true), "text/html; charset=utf-8", statusCode: 400);
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

    private static string ByePage() => Page("已退出监考看板。", "#a6e3a1", retryLink: true);

    /// 一屏静态页。
    ///
    /// ★★ `retryLink: false` 是给「**无权限**」那一屏用的(贝塔通 P98)。
    ///   取消 SSO(其 P84)之后,再点一次登录**不再「秒回」** —— 用户要
    ///   **完整输一遍密码**(过 MFA 的还要再过一次),然后被拒于同一个地方。
    ///   ★ 所以那一页上放「重新登录」等于**请他再白输一遍密码**。owner 明确不做缓解,
    ///   但措辞与按钮要如实改。
    ///   ★ 「协议/网络出错」那一屏则相反 —— 重试确实可能成功,链接留着。
    private static string ErrorPage(string message, bool retryLink) => Page(message, "#f38ba8", retryLink);

    private static string Page(string message, string color, bool retryLink) =>
        "<!doctype html><html lang=\"zh\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Horus 监考</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;background:#0f1115;color:#e6e6e6;display:flex;" +
        "align-items:center;justify-content:center;height:100vh;margin:0\">" +
        "<div style=\"text-align:center;max-width:32rem;padding:2rem\">" +
        "<h1 style=\"font-size:1.25rem\">Horus 监考看板</h1>" +
        "<p style=\"color:" + color + "\">" + System.Net.WebUtility.HtmlEncode(message) + "</p>" +
        (retryLink ? "<p><a href=\"/admin/login\" style=\"color:#89b4fa\">重新登录</a></p>" : "") +
        "</div></body></html>";
}
