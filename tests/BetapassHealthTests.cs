using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// 贝塔通存活探测端点(`GET /internal/health`)的契约回归(其 P94)。
///
/// ★★ **为什么这几条必须有测试**:这个端点没有任何用户可见行为 —— 它坏掉时**本站零症状**,
///   要到「某人权限撤了却还在里面」被人追查时才发现。没有测试的规则就是没人执行的规则。
///   ★ 注意:**「不实现它就会被判离线」那条旧说法已不成立**(对侧 `5bb16b6` 之后
///   任何 HTTP 应答都算在线),仍然要实现的理由见 `BetapassHealthEndpoint` 的类注释。
///
/// 判据逐条来自 BetaPass `docs/rp-contract.md`「`GET /internal/health`:必办项」:
/// 必须验签(含 `aud` 是不是自己)、任意 2xx 即算在线、不查库。
public class BetapassHealthTests
{
    private const string Issuer = "https://oidc.test";
    private const string Kid = "hk1";

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

    /// 按**贝塔通真实的探活令牌形态**签(核对 BetaPass `src/main.ts` 的 `signToken`):
    /// claims 只有 `iss` / `aud` / `sub`(= client_id 自己)/ `jti` / `exp`,★ **没有 `purpose`**。
    /// 照撤权令牌的形状去写测试就会验出一个不存在的口径。
    private static string SignProbe(RSA rsa, string aud, double? exp = null)
    {
        string header = JsonSerializer.Serialize(new { alg = OidcTokenValidator.SigningAlg, typ = "JWT", kid = Kid });
        string payload = JsonSerializer.Serialize(new
        {
            iss = Issuer, aud, sub = aud, jti = Guid.NewGuid().ToString("N"), exp = exp ?? Now() + 120,
        });
        string input = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payload));
        byte[] sig = rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return input + "." + B64Url(sig);
    }

    private static async Task<HttpResponseMessage> ProbeAsync(HttpClient http, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, BetapassHealthEndpoint.Path);
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task 合法探活令牌_回204无响应体()
    {
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));

        HttpResponseMessage resp = await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());   // 无响应体
    }

    [Fact]
    public async Task 监考台的client_id也认()
    {
        // ★ 两个客户端**各登记一个回调地址,因此探活也是两条**(采集面 `horus-client` /
        //   监考台 `horus-dashboard`)。只认其中一个的话,另一条探活恒验不过 ——
        //   ★ 今天它不会导致判离线(对侧任何 HTTP 应答都算在线),但会在其后台留下一条
        //   永久的 `HTTP 401`,且对侧判据一改回去那一半立刻失效。两边都要认。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(http, SignProbe(rsa, "horus-client"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(http, SignProbe(rsa, "horus-dashboard"))).StatusCode);
    }

    [Fact]
    public async Task 无令牌_拒()
    {
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(app.CreateClient(), null)).StatusCode);
    }

    [Fact]
    public async Task aud不是本机任何client_拒()
    {
        // ★ 用**同一把真私钥**签一枚除 `aud` 外一切合法的令牌 —— 必须被拒,
        //   才证明挡的是 `aud` 而不是签名。不验 `aud` 就等于把接入拓扑白送出去。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "someone-elses-client"))).StatusCode);
    }

    [Fact]
    public async Task 别的密钥签的令牌_拒()
    {
        using RSA real = RSA.Create(2048);
        using RSA forged = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(real, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ProbeAsync(app.CreateClient(), SignProbe(forged, "horus-client"))).StatusCode);
    }

    [Fact]
    public async Task 过期的令牌_拒()
    {
        // 探活令牌寿命 2 分钟(与撤权通知同口径)。放行过期令牌等于把一枚抓到就能永久用的探针留在外面。
        // ★ 减 300 秒而不是「刚过期一点」:验签器有 **60 秒时钟容差**(`ClockSkewSeconds`),
        //   贴着边界写出来的用例测的是容差不是过期判定,而且会随容差调整无声失效。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client", exp: Now() - 300))).StatusCode);
    }

    [Fact]
    public async Task 未开OIDC时回410而不是404()
    {
        // 与 `/internal/revoke` 同口径:路由**始终挂上**,由端点自己回一个明确状态。
        // ★ 410 **不会**让对侧把本站判离线(其 `5bb16b6` 之后任何 HTTP 应答都算在线),
        //   而是被如实记成一条 `HTTP 410` 摆在其后台 —— 这正是要的:
        //   「这台机器不是接入方」应该有人看见,而不是安静地当作一切正常。
        using var app = new TestApp();   // 默认 psk,两条 OIDC 链路都没开
        Assert.Equal(HttpStatusCode.Gone, (await ProbeAsync(app.CreateClient(), null)).StatusCode);
    }

    [Fact]
    public async Task 探活不清任何会话()
    {
        // ★★ 契约点名过的坑:**不要拿 `/internal/revoke` 当探活端点** —— 那会真的清会话,
        //   等于每 5 分钟把全体用户从各站点踢一次。这条锁住「探活端点确实是无副作用的」。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));

        var sessions = app.Services.GetRequiredService<SessionStore>();
        var adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        var claims = new OidcClaims("sub-1", "姓名", "u1");
        HorusSession collect = sessions.Create("E1", "seat1", "agentA", "PC", claims,
            RandomNumberGenerator.GetBytes(32), Now(), 180);
        AdminSession admin = adminSessions.Create(claims, Now(), 180);

        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client"))).StatusCode);

        Assert.NotNull(sessions.Get(collect.SessionId, Now()));
        Assert.NotNull(adminSessions.Get(admin.SessionId, Now()));
    }
}
