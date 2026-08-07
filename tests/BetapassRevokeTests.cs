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
            keys = new[] { new { kty = "RSA", use = "sig", alg = "PS256", kid, n = B64Url(p.Modulus!), e = B64Url(p.Exponent!) } },
        });
    }

    /// 按贝塔通真实形态签:**PS256**(其 P58),claims 含 `jti` 与 `purpose`。
    private static string SignNotice(RSA rsa, string aud, string jti, string sub, string purpose)
    {
        string header = JsonSerializer.Serialize(new { alg = "PS256", typ = "JWT", kid = Kid });
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
