using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:監考員看板 OIDC 登入端點(**不走 admin gate** —— 本就是獲取管理憑證的入口,且不在 /api 下)。
///   GET /admin/login  —— 重定向到貝塔通授權頁(生成 state+nonce+PKCE)。
///   GET /cb           —— 貝塔通回呼:換 token → 驗 id_token → 取 userinfo → 建管理工作階段 → 種 HttpOnly cookie → 跳看板。
///   GET /admin/logout —— **先清本機工作階段**,再跳貝塔通的退出範圍二選一頁(其 P85)。
///   GET /logout/done  —— 回跳落點;★ 在這裡**再清一次**本機工作階段。
public static class AdminOidcEndpoints
{
    public static void MapAdminOidc(this WebApplication app)
    {
        AdminOidcFlow? flow = app.Services.GetService(typeof(AdminOidcFlow)) as AdminOidcFlow;
        ServerConfig cfg = app.Services.GetRequiredService<ServerConfig>();
        AdminSessionStore adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminOidc");

        // 清本機工作階段 + 清 cookie。冪等,呼叫兩次無害(登出與回跳各調一次)。
        void ClearLocal(HttpContext ctx)
        {
            adminSessions.Delete(ctx.Request.Cookies["horus_admin"] ?? "");
            ctx.Response.Cookies.Delete("horus_admin", new CookieOptions { Path = "/" });
        }

        // ---- 退出:RP-Initiated Logout(貝塔通 P85)----
        //
        // 使用者點退出 → 跳 `end_session_endpoint` → **貝塔通給他兩個按鈕**:
        //   「只退出本站」  → 撤掉本 client 的 grant,重定向回 post_logout_redirect_uri;
        //   「退出所有站台」→ 銷燬全部授權,並向全部 RP 廣播 `/internal/revoke`(reason=user_logout)。
        //
        // ★★ **先清本機、再跳走。** 兩條理由:
        //   ① fail-closed —— 貝塔通不可達時跳轉會失敗,而那時使用者**本機已經登出了**;
        //      反過來「先跳、回來再清」在 IdP 掛掉的那一刻就是「點了退出但還登入著」。
        //   ② 「只退出本站」上游**只撤 grant,碰不到你的本機工作階段** —— 契約點名這是最容易漏的一步。
        //      本儲存庫在兩處都清(這裡 + 回跳落點),因為回跳未必回得來。
        app.MapGet("/admin/logout", (HttpContext ctx) =>
        {
            ClearLocal(ctx);

            // 未啟用 OIDC(靜態權杖模式)→ 沒有 IdP 工作階段這回事,清完直接給「已退出」頁。
            string? endSession = cfg.OidcEndSessionEndpoint;
            if (!cfg.DashboardOidcEnabled || endSession is null || string.IsNullOrEmpty(cfg.OidcDashboardClientId))
                return Results.Redirect("/logout/done");

            // ★ `client_id` 不能省:上游要靠它(或 id_token_hint)才認 `post_logout_redirect_uri`。
            //   本儲存庫不存 id_token(它在登入時消費一次就丟),所以走 client_id 這條。
            var q = new List<string> { "client_id=" + Uri.EscapeDataString(cfg.OidcDashboardClientId!) };
            string? back = cfg.PostLogoutRedirectUriEffective;
            // ★ 取不到回跳位址時**照樣跳**,只是不帶這個參數 —— 使用者會停在貝塔通自己的「已退出」頁。
            //   本機已經清乾淨了,所以那是「少一次回跳」而不是「沒退出」。
            if (!string.IsNullOrEmpty(back)) q.Add("post_logout_redirect_uri=" + Uri.EscapeDataString(back!));
            return Results.Redirect(endSession + "?" + string.Join("&", q));
        });

        // ---- 回跳落點 ----
        // ★★ **必須在這裡再清一次本機工作階段。** 「只退出本站」那一支上游只撤 grant,
        //   你的本機工作階段它碰不到 —— 漏了的表現是「使用者點了退出、跳回來了,然後發現自己還登入著」。
        //   ★ 本端在 `/admin/logout` 裡已經清過一次,這一發是**備援**:回跳可能由別的路徑進來
        //   (貝塔通側的帳號中心「退出所有站台」也會把人送到這裡),那條路徑沒經過上面那個端點。
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

            // ★★ **無權限現在走這條錯誤回呼,不再走「換到權杖之後發現不是長老」**(貝塔通 P83)。
            //   沒有 `horus-admin` 平台權限的帳號**在貝塔通的授權階段就被拒**(其 §3.2),
            //   於是回呼帶的是 `error=access_denied` 而**沒有 code** —— 拿不到 code、換不到權杖、
            //   **沒有本機工作階段**。按其 rp-contract「無權限時停在未登入」:只換提示語,
            //   ★ **必須給一句人話,不得顯示成「系統錯誤」**;
            //   ★ 再點一次登入會得到**同一個結果**,所以這裡是一屏靜態頁而不是自動重試。
            //   ★★ **2026-08-10 起不再「秒回」**(貝塔通 P84 取消 SSO / P98):以前貝塔通那邊
            //     還有工作階段,無權限的人再點一次是秒回同一個拒絕頁;現在**每點一次都要完整輸一遍
            //     密碼**才被拒。owner 明確不做緩解 —— 但**無權限頁不放「重新登入」**,
            //     那等於請他再白輸一遍密碼。
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
                // 走到這裡說明拿到了 code 卻沒換成工作階段 —— 那是協定/網路層的問題,不是權限問題
                // (權限問題上面已經攔掉了)。不洩細節。
                return Results.Content(ErrorPage("登录失败，请重试。", retryLink: true), "text/html; charset=utf-8", statusCode: 400);
            }

            // 種管理工作階段 cookie(HttpOnly·SameSite=Lax 便於登入後 top-level 跳轉攜帶;不設 Secure 因自簽 https 亦可,瀏覽器對 https 會發)。
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

    /// 一屏靜態頁。
    ///
    /// ★★ `retryLink: false` 是給「**無權限**」那一屏用的(貝塔通 P98)。
    ///   取消 SSO(其 P84)之後,再點一次登入**不再「秒回」** —— 使用者要
    ///   **完整輸一遍密碼**(過 MFA 的還要再過一次),然後被拒於同一個地方。
    ///   ★ 所以那一頁上放「重新登入」等於**請他再白輸一遍密碼**。owner 明確不做緩解,
    ///   但措辭與按鈕要如實改。
    ///   ★ 「協定/網路出錯」那一屏則相反 —— 重試確實可能成功,連結留著。
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
