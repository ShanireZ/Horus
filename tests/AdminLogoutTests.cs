using System.Net;
using Horus.Server.Config;
using Horus.Server.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// **RP-Initiated Logout**(貝塔通 P85)與登出回跳的迴歸。
///
/// ★★ 這一條最容易漏的地方是**「只退出本站」那一支**:上游只撤 grant、
///   **碰不到 RP 的本機工作階段**。漏了的表現是「使用者點了退出、跳回來了,
///   然後發現自己還登入著」—— 一屏之內就能看見,但只有真去點才看得見。
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
        // ★★ **先清後跳**是 fail-closed 的落點:貝塔通不可達時跳轉會失敗,
        //   而那時使用者**本機已經登出了**。反過來「先跳、回來再清」在 IdP 掛掉的那一刻
        //   就是「點了退出但還登入著」。
        using var app = new TestApp(adminOidc: true);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = store.Create(Claims(), Now(), 360);

        HttpResponseMessage resp = await GetCookie(NoRedirect(app), "/admin/logout", s.SessionId);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Null(store.Get(s.SessionId, Now()));   // ★ 跳走之前本機就已經清了

        string to = resp.Headers.Location!.ToString();
        Assert.StartsWith("https://oidc.test/session/end?", to);
        // ★ `client_id` 不能省:上游要靠它(或 id_token_hint)才認 post_logout_redirect_uri。
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
        // ★★ 「只退出本站」時貝塔通**只撤 grant**,你的本機工作階段它碰不到 ——
        //   契約點名這是最容易漏的一步。這裡模擬「工作階段還在、人從貝塔通跳回來」:
        //   落點必須把它清掉。
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
        // 靜態權杖模式沒有 IdP 工作階段這回事,清完直接給「已退出」頁。
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

        // 顯式設定優先(貝塔通後台登記的那一條未必與回呼同源)
        var explicitCfg = cfg with { OidcPostLogoutRedirectUri = "https://other.lan/bye" };
        Assert.Equal("https://other.lan/bye", explicitCfg.PostLogoutRedirectUriEffective);

        // 兩者都沒有 → null;★ 登出仍照走,只是不帶 post_logout_redirect_uri
        Assert.Null(new ServerConfig().PostLogoutRedirectUriEffective);
    }

    [Fact]
    public async Task 无权限页不放重新登录按钮()
    {
        // ★★ 貝塔通 P98:取消 SSO 之後無權限的人**不再「秒回」**,每點一次登入都要
        //   **完整輸一遍密碼**才被拒。在那一頁上放「重新登入」等於請他再白輸一遍。
        //   owner 明確不做緩解,但措辭與按鈕要如實改。
        using var app = new TestApp(adminOidc: true);
        HttpResponseMessage resp = await NoRedirect(app).GetAsync("/cb?error=access_denied&state=x");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        string html = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("重新登录", html);
        Assert.Contains("联系管理员开通", html);   // ★ 仍然要給一句人話,不得顯示成「系統錯誤」
    }

    [Fact]
    public async Task 协议出错的那一屏仍然留着重新登录()
    {
        // ★ 與上一條相反:重試確實可能成功(網路抖動 / token 端點一時不通),連結留著。
        //   把兩屏合併成「一律不給重試」是把 P98 用過了頭。
        using var app = new TestApp(adminOidc: true);
        HttpResponseMessage resp = await NoRedirect(app).GetAsync("/cb?error=server_error&state=x");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("重新登录", await resp.Content.ReadAsStringAsync());
    }
}
