using System.Net;
using Horus.Server.Config;
using Horus.Server.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// **RP-Initiated Logout**(贝塔通 P85)与登出回跳的回归。
///
/// ★★ 这一条最容易漏的地方是**「只退出本站」那一支**:上游只撤 grant、
///   **碰不到 RP 的本地会话**。漏了的表现是「用户点了退出、跳回来了,
///   然后发现自己还登录着」—— 一屏之内就能看见,但只有真去点才看得见。
public class AdminLogoutTests
{
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    private static OidcClaims Claims() => new("sub-1", "张三", "zhangsan");

    private static HttpClient NoRedirect(TestApp app)
        => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetCookie(HttpClient http, string path, string? sid)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (sid is not null) req.Headers.Add("Cookie", "horus_admin=" + sid);
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task 退出_先清本地会话_再跳贝塔通的二选一页()
    {
        // ★★ **先清后跳**是 fail-closed 的落点:贝塔通不可达时跳转会失败,
        //   而那时用户**本地已经登出了**。反过来「先跳、回来再清」在 IdP 挂掉的那一刻
        //   就是「点了退出但还登录着」。
        using var app = new TestApp(adminOidc: true);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = store.Create(Claims(), Now(), 360);

        HttpResponseMessage resp = await GetCookie(NoRedirect(app), "/admin/logout", s.SessionId);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Null(store.Get(s.SessionId, Now()));   // ★ 跳走之前本地就已经清了

        string to = resp.Headers.Location!.ToString();
        Assert.StartsWith("https://oidc.test/session/end?", to);
        // ★ `client_id` 不能省:上游要靠它(或 id_token_hint)才认 post_logout_redirect_uri。
        Assert.Contains("client_id=horus-dashboard", to);
        Assert.Contains("post_logout_redirect_uri=", to);
    }

    [Fact]
    public async Task 退出时也清cookie()
    {
        using var app = new TestApp(adminOidc: true);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = store.Create(Claims(), Now(), 360);

        HttpResponseMessage resp = await GetCookie(NoRedirect(app), "/admin/logout", s.SessionId);

        Assert.Contains(resp.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith("horus_admin=", StringComparison.Ordinal) && v.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 回跳落点再清一次本地会话()
    {
        // ★★ 「只退出本站」时贝塔通**只撤 grant**,你的本地会话它碰不到 ——
        //   契约点名这是最容易漏的一步。这里模拟「会话还在、人从贝塔通跳回来」:
        //   落点必须把它清掉。
        using var app = new TestApp(adminOidc: true);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = store.Create(Claims(), Now(), 360);

        HttpResponseMessage resp = await GetCookie(NoRedirect(app), "/logout/done", s.SessionId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(store.Get(s.SessionId, Now()));
        Assert.Contains("已退出", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 令牌模式下退出不跳贝塔通()
    {
        // 静态令牌模式没有 IdP 会话这回事,清完直接给「已退出」页。
        using var app = new TestApp(adminAuth: true);
        HttpResponseMessage resp = await GetCookie(NoRedirect(app), "/admin/logout", null);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal("/logout/done", resp.Headers.Location!.ToString());
    }

    [Fact]
    public void 回跳地址按dashboard回调同源推导()
    {
        var cfg = new ServerConfig { OidcDashboardRedirectUri = "https://horus.lan:8443/cb" };
        Assert.Equal("https://horus.lan:8443/logout/done", cfg.PostLogoutRedirectUriEffective);

        // 显式配置优先(贝塔通后台登记的那一条未必与回调同源)
        var explicitCfg = cfg with { OidcPostLogoutRedirectUri = "https://other.lan/bye" };
        Assert.Equal("https://other.lan/bye", explicitCfg.PostLogoutRedirectUriEffective);

        // 两者都没有 → null;★ 登出仍照走,只是不带 post_logout_redirect_uri
        Assert.Null(new ServerConfig().PostLogoutRedirectUriEffective);
    }

    [Fact]
    public async Task 无权限页不放重新登录按钮()
    {
        // ★★ 贝塔通 P98:取消 SSO 之后无权限的人**不再「秒回」**,每点一次登录都要
        //   **完整输一遍密码**才被拒。在那一页上放「重新登录」等于请他再白输一遍。
        //   owner 明确不做缓解,但措辞与按钮要如实改。
        using var app = new TestApp(adminOidc: true);
        HttpResponseMessage resp = await NoRedirect(app).GetAsync("/cb?error=access_denied&state=x");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        string html = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("重新登录", html);
        Assert.Contains("联系管理员开通", html);   // ★ 仍然要给一句人话,不得显示成「系统错误」
    }

    [Fact]
    public async Task 协议出错的那一屏仍然留着重新登录()
    {
        // ★ 与上一条相反:重试确实可能成功(网络抖动 / token 端点一时不通),链接留着。
        //   把两屏合并成「一律不给重试」是把 P98 用过了头。
        using var app = new TestApp(adminOidc: true);
        HttpResponseMessage resp = await NoRedirect(app).GetAsync("/cb?error=server_error&state=x");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("重新登录", await resp.Content.ReadAsStringAsync());
    }
}
