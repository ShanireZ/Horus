using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// 贝塔通撤权通知接收端(`/internal/revoke`)的契约回归。
///
/// 判据逐条来自 BetaPass `docs/rp-contract.md`,**全部落在行为上**:
/// 验签(含 `aud` 是不是自己)、按 `jti` 幂等、三种 reason 处置相同、认不出的 reason 照清、
/// 按 `aud` 分别处置(撤监考台不动采集面)。
public class BetapassRevokeTests
{
    private const string Issuer = "https://oidc.test";
    private const string Kid = "rk1";

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BuildJwks(RSA rsa, string kid)
    {
        RSAParameters p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", use = "sig", alg = OidcTokenValidator.SigningAlg, kid, n = B64Url(p.Modulus!), e = B64Url(p.Exponent!) } },
        });
    }

    /// 按贝塔通真实形态签,claims 含 `jti` 与 `purpose`。
    /// ★ 算法与 JWKS 里的 `alg` 都**从生产常量派生**,不再写死字面量 —— 见 OidcTests 里同款说明。
    private static string SignNotice(RSA rsa, string aud, string jti, string sub, string purpose)
    {
        string header = JsonSerializer.Serialize(new { alg = OidcTokenValidator.SigningAlg, typ = "JWT", kid = Kid });
        string payload = JsonSerializer.Serialize(new { iss = Issuer, aud, sub, jti, purpose, exp = Now() + 120 });
        string input = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payload));
        byte[] sig = rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return input + "." + B64Url(sig);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient http, string? token, string jti, string sub, string clientId, string reason)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BetapassRevokeEndpoint.Path)
        {
            Content = JsonContent.Create(new { jti, sub, platform = "horus", client_id = clientId, reason }),
        };
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await http.SendAsync(req);
    }

    /// ★ 响应体只能读一次(HttpContent 是流)。此前分成 RevokedAsync / DuplicateAsync 两个助手,
    ///   第二次读就 ObjectDisposedException —— 症状是「测试挂了」而不是「断言不成立」,
    ///   花的时间远超它值得的。统一读一次再取两项。
    private static async Task<(bool Duplicate, int Revoked)> ReadAsync(HttpResponseMessage resp)
    {
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("duplicate").GetBoolean(), body.GetProperty("revoked_sessions").GetInt32());
    }

    private static OidcClaims Claims(string sub = "sub-1") => new(sub, "姓名", "u1");

    [Theory]
    [InlineData("platform_access_revoked")]
    [InlineData("password_changed")]
    [InlineData("mfa_factor_changed")]
    [InlineData("something_new_in_2027")]   // ★ 认不出的新值也必须照清、照回 2xx
    public async Task 三种reason与认不出的新值_处置完全相同(string reason)
    {
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();

        var store = app.Services.GetRequiredService<SessionStore>();
        store.Create("E1", "seat1", "agentA", "PC", Claims(), RandomNumberGenerator.GetBytes(32), Now(), 180);

        string jti = "j-" + Guid.NewGuid().ToString("N")[..8];
        string token = SignNotice(rsa, "horus-client", jti, "sub-1", reason);
        HttpResponseMessage resp = await PostAsync(http, token, jti, "sub-1", "horus-client", reason);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, (await ReadAsync(resp)).Revoked);   // ★ 不是恒 0:那条会话真被清掉了
    }

    [Fact]
    public async Task 按jti幂等_重投回2xx且认出重复_不重复清()
    {
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var store = app.Services.GetRequiredService<SessionStore>();
        store.Create("E1", "seat1", "agentA", "PC", Claims(), RandomNumberGenerator.GetBytes(32), Now(), 180);

        const string jti = "j-fixed";
        string token = SignNotice(rsa, "horus-client", jti, "sub-1", "password_changed");

        HttpResponseMessage first = await PostAsync(http, token, jti, "sub-1", "horus-client", "password_changed");
        (bool dup1, int revoked1) = await ReadAsync(first);
        Assert.Equal(1, revoked1);
        Assert.False(dup1);

        // ★ 贝塔通的 HTTP 超时只有 5 秒 —— 处理成功但花了 6 秒的一发会被判失败并重投,
        //   同一个 `jti` 必然来第二次。「反正只会来一次」是错的。
        HttpResponseMessage again = await PostAsync(http, token, jti, "sub-1", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        (bool dup2, int revoked2) = await ReadAsync(again);
        Assert.True(dup2);
        Assert.Equal(1, revoked2);   // 原样回上次的答案,不是又清了一遍
    }

    [Fact]
    public async Task 撤监考台不动采集面()
    {
        // ★★ 这是拆平台(贝塔通 P83)换来的精确性:`horus-client` → 平台 `horus`(采集面)、
        //   `horus-dashboard` → 平台 `horus-admin`(监考台)。撤掉某人的监考员资格
        //   **不该**把他作为考生的采集会话也断掉。图省事两边一起清就把这条精确性丢了。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();

        var sessions = app.Services.GetRequiredService<SessionStore>();
        var adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        HorusSession collect = sessions.Create("E1", "seat1", "agentA", "PC", Claims(),
            RandomNumberGenerator.GetBytes(32), Now(), 180);
        AdminSession admin = adminSessions.Create(Claims(), Now(), 180);

        const string jti = "j-admin";
        string token = SignNotice(rsa, "horus-dashboard", jti, "sub-1", "platform_access_revoked");
        HttpResponseMessage resp = await PostAsync(http, token, jti, "sub-1", "horus-dashboard", "platform_access_revoked");
        Assert.Equal(1, (await ReadAsync(resp)).Revoked);

        Assert.Null(adminSessions.Get(admin.SessionId, Now()));       // 监考台会话没了
        Assert.NotNull(sessions.Get(collect.SessionId, Now()));       // ★ 采集会话仍在
    }

    [Theory]
    [InlineData("user_logout", "退出所有站点")]
    [InlineData("platform_access_revoked", "权限已关闭")]
    [InlineData("password_changed", "密码已变更")]
    [InlineData("mfa_factor_changed", "二次验证")]
    public async Task 每种reason都给出对应的一句人话(string reason, string expectFragment)
    {
        // ★ 契约明写四种 reason「**处置完全相同,区分只服务提示语**」——
        //   提示语正是这里。撤权是删行,不留痕的话被踢的人只看得到一句笼统的「登录已失效」。
        // ★★ 「平台权限被关掉」这一种尤其要紧:取消 SSO(贝塔通 P84)之后,
        //   无权限的人**每点一次登录都要完整输一遍密码**才被拒(其 P98,owner 明确不做缓解)。
        //   不告诉他真实原因,他会一遍遍白输。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = adminSessions.Create(Claims(), Now(), 360);

        string jti = "j-" + reason;
        string token = SignNotice(rsa, "horus-dashboard", jti, "sub-1", reason);
        await PostAsync(http, token, jti, "sub-1", "horus-dashboard", reason);

        // 用那条已被删掉的会话 id 再打一次 /api/* → 401 的响应体带着「为什么」
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/exams");
        req.Headers.Add("Cookie", "horus_admin=" + s.SessionId);
        HttpResponseMessage resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.Contains(expectFragment, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 同一条通知被重投很多次_每次都回2xx且不重复清()
    {
        // ★★ 贝塔通 P93 起**重投走完不进终态**:阶梯是 5min×3 → 15min → 30min → 1h,
        //   走完转「等待探活」挂起,探到本站回来后**从头再投一遍,直至成功**。
        //   于是同一条通知会被投很多次,且**本站长时间下线再回来时会一次性收到一批积压的**。
        // ★ 处置办法就是 `jti` 幂等台账 —— **不要动它**,也**不要因为「这条太旧了」而丢弃**
        //   (旧不代表已经处理过)。这条锁住「投多少次都稳」。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var store = app.Services.GetRequiredService<SessionStore>();
        store.Create("E1", "seat1", "agentA", "PC", Claims(), RandomNumberGenerator.GetBytes(32), Now(), 180);

        const string jti = "j-storm";
        string token = SignNotice(rsa, "horus-client", jti, "sub-1", "password_changed");

        for (int i = 0; i < 8; i++)   // 首投 + 7 次重投(阶梯共 6 次,多打两下也得稳)
        {
            HttpResponseMessage resp = await PostAsync(http, token, jti, "sub-1", "horus-client", "password_changed");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            (bool dup, int revoked) = await ReadAsync(resp);
            Assert.Equal(i > 0, dup);
            Assert.Equal(1, revoked);   // 恒回第一次的答案,不是每次都「又清了 0 条」
        }
    }

    [Fact]
    public async Task 认不出的reason_照清且兜底提示语不为空()
    {
        // ★★ 贝塔通那边加一种 reason 时本端一行代码都不用改(处置本就与 reason 无关),
        //   但**提示语不能因此变成空白或「未知错误」** —— 兜底那句必须自己站得住。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = adminSessions.Create(Claims(), Now(), 360);

        const string jti = "j-future", reason = "something_new_in_2027";
        string token = SignNotice(rsa, "horus-dashboard", jti, "sub-1", reason);
        HttpResponseMessage revoke = await PostAsync(http, token, jti, "sub-1", "horus-dashboard", reason);
        Assert.Equal(1, (await ReadAsync(revoke)).Revoked);   // 照清

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/exams");
        req.Headers.Add("Cookie", "horus_admin=" + s.SessionId);
        JsonElement body = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();

        string msg = body.GetProperty("message").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.Contains("重新登录", msg);
    }

    [Fact]
    public async Task 无令牌_拒()
    {
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), null, "j1", "sub-1", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task aud不是本机任何client_拒()
    {
        // ★ 不验 `aud` 就等于开了一个「谁都能踢人下线」的端点。这里用**同一把真私钥**签一枚
        //   除 `aud` 外一切合法的令牌 —— 必须被拒,才证明挡的是 `aud` 而不是签名。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        string token = SignNotice(rsa, "someone-elses-client", "j2", "sub-1", "password_changed");
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), token, "j2", "sub-1", "someone-elses-client", "password_changed");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 别的密钥签的令牌_拒()
    {
        using RSA real = RSA.Create(2048);
        using RSA forged = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(real, Kid));
        string token = SignNotice(forged, "horus-client", "j3", "sub-1", "password_changed");
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), token, "j3", "sub-1", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 报文与令牌对不上_拒()
    {
        // 令牌是权威,报文只是便于阅读的副本。对不上说明有人拿一枚合法令牌套了别人的报文。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        string token = SignNotice(rsa, "horus-client", "j4", "sub-1", "password_changed");
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), token, "j4", "sub-OTHER", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task 未开OIDC时回410而不是404_否则会被无限重投()
    {
        // ★ 贝塔通判成功的口径是 2xx,而 **404 / 405 与 5xx 一样会被重投 12 次**
        //   (「RP 还没实现这个端点」正是最需要重投的情形)。所以路由必须**始终挂上**,
        //   由端点自己回一个「别再投了」的状态,而不是让它落到 404 去被反复敲。
        using var app = new TestApp();   // 默认 psk,两条 OIDC 链路都没开
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), null, "j5", "sub-1", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }
}
