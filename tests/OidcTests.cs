using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// M4·S1 + 会话密钥:id_token 离线验签(RS256)与 ECDH K_sess 协商的回归锁定。
public class OidcTokenValidatorTests
{
    private const string Issuer = "https://betaoi.cc";
    private const string Audience = "horus-client";
    private const string Kid = "test-kid-1";

    [Fact]
    public void 合法id_token_验签通过_只取出sub()
    {
        using RSA rsa = RSA.Create(2048);
        string jwks = BuildJwks(rsa, Kid);
        var v = new OidcTokenValidator(jwks, Issuer, Audience);

        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"));
        OidcSubject s = v.Validate(token, "n1", Now());

        Assert.Equal("sub-abc", s.Sub);
    }

    [Fact]
    public void 验签器给不出姓名与用户名_那两项只在userinfo()
    {
        // ★★ **直接断言不变量本身**:验签的产物**只有** sub 一项。
        //   贝塔通把 `conformIdTokenClaims` 留在上游默认 `true`,授权码流下 id_token 里根本没有
        //   `name` / `preferred_username`(其 docs/rp-contract.md)。把它们从 id_token 取会恒得空串,
        //   而空的 Username 让 ExamDispatch.SeatFrom 对每个人回退成 sub —— 不报错、不抛异常。
        //   旧 wentian 之所以能那么干,是它专门设了 `conformIdTokenClaims: false`,**那条豁免不随迁移过来**。
        Assert.Single(typeof(OidcSubject).GetProperties());
    }

    [Fact]
    public void id_token里混进身份claim也不被采信()
    {
        // 贝塔通 P81:即便令牌里混进了那些 claim(例如对着旧 IdP 跑),也一律不进身份 ——
        // 判据是「验签的出口只有 sub」,而不是「我们没请求所以不会有」。
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string payload = JsonSerializer.Serialize(new
        {
            iss = Issuer, aud = Audience, sub = "sub-y", exp = Now() + 3600, nonce = "n1",
            name = "叶锋", preferred_username = "ye_feng",
            user_type = "elder", dao_name = "问天", realm = "金丹", realm_level = 3, combat_power = 12345,
        });
        OidcSubject s = v.Validate(SignJwt(rsa, Kid, payload), "n1", Now());
        Assert.Equal("sub-y", s.Sub);
    }

    // ---- userinfo:身份 claims 的唯一来源 ----

    [Fact]
    public void userinfo取出姓名与用户名()
    {
        OidcClaims c = Userinfo.Parse(
            """{"sub":"sub-abc","name":"叶锋","preferred_username":"ye_feng"}""",
            new OidcSubject("sub-abc"));

        Assert.Equal("sub-abc", c.Sub);
        Assert.Equal("叶锋", c.Name);             // profile.name = 真实姓名
        Assert.Equal("ye_feng", c.Username);      // profile.preferred_username → 座位标识
        Assert.Equal(3, typeof(OidcClaims).GetProperties().Length);   // Sub / Name / Username,不多不少
    }

    [Fact]
    public void 未设用户名的账号_preferred_username缺失_取空串而不是编一个()
    {
        // ★ 贝塔通对**未设置用户名的账号直接省略** `preferred_username`(不发空串,见其 rp-contract)。
        //   这里必须取到空串,好让 ExamDispatch.SeatFrom 走它那条回退 sub 的分支 ——
        //   在这里编一个默认值会让「没设用户名的人」共用同一个座位号。
        OidcClaims c = Userinfo.Parse("""{"sub":"sub-x"}""", new OidcSubject("sub-x"));
        Assert.Equal("", c.Username);
        Assert.Equal("", c.Name);
        Assert.Equal("sub-x", c.Sub);
    }

    [Fact]
    public void userinfo的sub与id_token不符_丢弃()
    {
        // ★★ OIDC Core 5.3.2 的硬性要求。不比这一下,「令牌是谁的」与「资料是谁的」就成了两件事。
        var ex = Assert.Throws<OidcValidationException>(() => Userinfo.Parse(
            """{"sub":"sub-EVIL","name":"别人","preferred_username":"other"}""",
            new OidcSubject("sub-abc")));
        Assert.Contains("sub", ex.Message);
    }

    [Fact]
    public void userinfo缺sub_丢弃()
    {
        Assert.Throws<OidcValidationException>(() => Userinfo.Parse(
            """{"name":"叶锋"}""", new OidcSubject("sub-abc")));
    }

    [Fact]
    public void userinfo不是JSON_如实报错而不是当成没有claims()
    {
        // 贝塔通不给客户端登记 `userinfo_signed_response_alg`,响应恒为 JSON。
        // 真收到 JWT(后台登记被改过)时要报出来 —— 静默当成空 claims 又是一次「无症状」。
        var ex = Assert.Throws<OidcValidationException>(() => Userinfo.Parse(
            "eyJhbGciOiJQUzI1NiJ9.e30.sig", new OidcSubject("sub-abc")));
        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public async Task 取userinfo带Bearer访问令牌()
    {
        // 少了这个头,贝塔通回 401 —— 而 401 在旧写法里根本不会发生(压根没这一次请求),
        // 所以这条锁的是「请求确实发出去了、且带对了凭据」。
        var handler = new RecordingHandler("""{"sub":"sub-abc","name":"叶锋","preferred_username":"ye_feng"}""");
        using var http = new HttpClient(handler);
        OidcClaims c = await Userinfo.FetchAsync(
            http, "https://betaoi.cn/me", "at-xyz", new OidcSubject("sub-abc"), NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastAuth?.Scheme);
        Assert.Equal("at-xyz", handler.LastAuth?.Parameter);
        Assert.Equal("https://betaoi.cn/me", handler.LastUrl);
        Assert.Equal("ye_feng", c.Username);
    }

    [Fact]
    public async Task userinfo非2xx_登录失败而不是放行空身份()
    {
        // ★ fail-closed,与 R3 同一取向:宁可登不进去,也不要一个「进得去但谁也认不出是谁」的考场。
        var handler = new RecordingHandler("nope", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<OidcValidationException>(() => Userinfo.FetchAsync(
            http, "https://betaoi.cn/me", "at-xyz", new OidcSubject("sub-abc"), NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public void userinfo端点跟着入口前缀走_而不是拼在issuer上()
    {
        // ★ 贝塔通 P72:`.cc` 是同一个 issuer 的第二条入口。端点**整套**换前缀,issuer 不动。
        //   userinfo 是本轮新增的一条,与 token / auth / jwks 同源派生,不能漏进这条口径。
        var cfg = new ServerConfig { OidcIssuer = "https://betaoi.cn", OidcEndpointBase = "https://betaoi.cc" };
        Assert.Equal("https://betaoi.cc/me", cfg.OidcUserinfoEndpoint);
        Assert.Equal("https://betaoi.cn", cfg.OidcIssuer);

        // 不填前缀时回落 issuer;★ 路径是根 `/me`,**不是**旧 wentian 的 `/oauth/userinfo`。
        var plain = new ServerConfig { OidcIssuer = "https://betaoi.cn" };
        Assert.Equal("https://betaoi.cn/me", plain.OidcUserinfoEndpoint);
    }

    /// 记录最后一次请求的桩 handler(取 userinfo 用:要验 Bearer 头与目标 URL 都对)。
    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuth { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastAuth = request.Headers.Authorization;
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void RS256签名的令牌_拒_不接受算法降级()
    {
        // ★ 同一把 RSA 公钥,PKCS1 与 PSS **两种填充都验得通** —— 所以「放宽成两种都收」
        //   不是兼容性问题而是安全问题:贝塔通根本签不出 RS256(其 P58),
        //   多出来的那条路只服务于攻击者。这里用**同一把真私钥**按 RS256 签一枚合法令牌,必须被拒。
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"), alg: "RS256");
        var ex = Assert.Throws<OidcValidationException>(() => v.Validate(token, "n1", Now()));
        Assert.Contains("PS256", ex.Message);
    }

    [Fact]
    public void 允许的签名算法只有PS256一项()
    {
        // 直接断言不变量本身,而不是守卫的镜像:有人日后改成 "RS256 or PS256" 时这条立刻红。
        Assert.Equal("PS256", OidcTokenValidator.SigningAlg);
    }

    [Fact]
    public void nonce不符_拒()
    {
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"));
        var ex = Assert.Throws<OidcValidationException>(() => v.Validate(token, "WRONG", Now()));
        Assert.Contains("nonce", ex.Message);
    }

    [Fact]
    public void 过期_拒()
    {
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string token = SignJwt(rsa, Kid, Payload(nonce: "n1", exp: Now() - 3600));
        var ex = Assert.Throws<OidcValidationException>(() => v.Validate(token, "n1", Now()));
        Assert.Contains("过期", ex.Message);
    }

    [Fact]
    public void aud不符_拒()
    {
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, "other-client");
        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"));
        Assert.Throws<OidcValidationException>(() => v.Validate(token, "n1", Now()));
    }

    [Fact]
    public void nbf在未来_尚未生效_拒()
    {
        // 纵深防御:签发方给了未来的 nbf(未生效时间)→ 拒(超时钟偏移容差)。
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string payload = JsonSerializer.Serialize(new
        {
            iss = Issuer, aud = Audience, sub = "sub-z", exp = Now() + 3600, nbf = Now() + 3600, nonce = "n1",
        });
        var ex = Assert.Throws<OidcValidationException>(() => v.Validate(SignJwt(rsa, Kid, payload), "n1", Now()));
        Assert.Contains("nbf", ex.Message);
    }

    [Fact]
    public void nbf已过_通过()
    {
        // nbf 已生效(过去)→ 不因 nbf 而拒(其余合法则通过)。
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string payload = JsonSerializer.Serialize(new
        {
            iss = Issuer, aud = Audience, sub = "sub-w", exp = Now() + 3600, nbf = Now() - 100, nonce = "n1",
        });
        OidcSubject s = v.Validate(SignJwt(rsa, Kid, payload), "n1", Now());
        Assert.Equal("sub-w", s.Sub);
    }

    [Fact]
    public void 签名被篡改_拒()
    {
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"));
        // 改 payload 段但不重签 → 签名对不上
        string[] p = token.Split('.');
        string tampered = p[0] + "." + B64Url(Encoding.UTF8.GetBytes(Payload(nonce: "n1", sub: "sub-EVIL"))) + "." + p[2];
        Assert.Throws<OidcValidationException>(() => v.Validate(tampered, "n1", Now()));
    }

    [Fact]
    public void 另一把密钥签的token_拒()
    {
        using RSA good = RSA.Create(2048);
        using RSA evil = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(good, Kid), Issuer, Audience);
        string token = SignJwt(evil, Kid, Payload(nonce: "n1"));   // 用 evil 私钥签、但 kid 冒充 good
        Assert.Throws<OidcValidationException>(() => v.Validate(token, "n1", Now()));
    }

    [Fact]
    public void ECDH两端派生同一K_sess_公钥不含私钥()
    {
        using ECDiffieHellman agent = SessionCrypto.NewEphemeralKey();
        using ECDiffieHellman server = SessionCrypto.NewEphemeralKey();
        string agentPub = SessionCrypto.ExportPublicKeyB64(agent);
        string serverPub = SessionCrypto.ExportPublicKeyB64(server);

        byte[] kAgent = SessionCrypto.DeriveKey(agent, serverPub);
        byte[] kServer = SessionCrypto.DeriveKey(server, agentPub);

        Assert.Equal(32, kAgent.Length);
        Assert.Equal(Convert.ToHexString(kAgent), Convert.ToHexString(kServer));   // 两端一致

        // 第三方(攻击者)只有双方公钥,拿自己的私钥派生不出同一把 K_sess
        using ECDiffieHellman attacker = SessionCrypto.NewEphemeralKey();
        byte[] kAttacker = SessionCrypto.DeriveKey(attacker, serverPub);
        Assert.NotEqual(Convert.ToHexString(kAgent), Convert.ToHexString(kAttacker));
    }

    [Fact]
    public void K_sess当HMAC密钥_两端握手签名一致()
    {
        using ECDiffieHellman agent = SessionCrypto.NewEphemeralKey();
        using ECDiffieHellman server = SessionCrypto.NewEphemeralKey();
        byte[] kA = SessionCrypto.DeriveKey(agent, SessionCrypto.ExportPublicKeyB64(server));
        byte[] kS = SessionCrypto.DeriveKey(server, SessionCrypto.ExportPublicKeyB64(agent));
        // 复用既有 HMAC 握手/签名:密钥换成 K_sess,两端逐字节一致
        Assert.Equal(Auth.Handshake(kA, "E1", "A07", "ag"), Auth.Handshake(kS, "E1", "A07", "ag"));
        Assert.Equal(EventCanonical.Sig(kA, "hashself", 5), EventCanonical.Sig(kS, "hashself", 5));
    }

    // ---- 小工具:构造 JWKS / 签 JWT ----
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string BuildJwks(RSA rsa, string kid)
    {
        RSAParameters p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid, n = B64Url(p.Modulus!), e = B64Url(p.Exponent!) } },
        });
    }

    private static string Payload(string nonce, string sub = "sub-abc", double? exp = null) => JsonSerializer.Serialize(new
    {
        iss = Issuer, aud = Audience, sub, exp = exp ?? Now() + 3600, nonce,
        // ★ 只有标准 `profile` scope 的两项。贝塔通 P81 之后 `horus_profile` 那套
        //   (user_type / nickname / dao_name / avatar / realm / realm_level / combat_power)不再存在。
        name = "叶锋", preferred_username = "ye_feng",
    });

    /// 按贝塔通真实的形态签:**PS256**(RSA-PSS · SHA-256)。
    /// ★ 替身的形状就是判据的一部分 —— 这里若仍按旧 wentian 的 RS256 签,
    ///   整套用例会在一个**生产上根本不存在**的算法下全绿,而真接上贝塔通当场全红。
    /// ★ 默认算法**从生产常量派生**(`OidcTokenValidator.SigningAlg`),不是再写一遍字面量 ——
    ///   两处各写一份必然漂,而漂了的表现是「测试在一个生产上不存在的算法下全绿」。
    ///   常量本身由「允许的签名算法只有 PS256 一项」那条钉着,所以这不是把判据交给被测方。
    private static string SignJwt(RSA rsa, string kid, string payloadJson, string? alg = null)
    {
        alg ??= OidcTokenValidator.SigningAlg;
        string header = JsonSerializer.Serialize(new { alg, typ = "JWT", kid });
        string signingInput = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payloadJson));
        RSASignaturePadding padding = alg == "PS256" ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
        byte[] sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, padding);
        return signingInput + "." + B64Url(sig);
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// M4·S4/S5:OIDC 会话绑定的采集鉴权 —— **闭合 A1(跨身份栽赃)/A2(seq 抢占)** 的端到端锁定。
public class OidcIngestAuthTests
{
    private static async Task CreateExamAsync(HttpClient http)
        => (await http.PostAsJsonAsync("/api/exams", new { examId = "E1", name = "T", seats = new[] { new { seatId = "A07" } } })).EnsureSuccessStatusCode();

    /// 直接建一条会话(绕过真 token 交换),返回 (sessionId, kSess)。
    private static (string sid, byte[] k) MakeSession(TestApp app, string seat, string agent)
    {
        var store = app.Services.GetRequiredService<SessionStore>();
        byte[] k = RandomNumberGenerator.GetBytes(32);
        var claims = new OidcClaims("sub-" + agent, "姓名", "user_" + agent);
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        HorusSession s = store.Create("E1", seat, agent, "PC", claims, k, now, 180);
        return (s.SessionId, k);
    }

    private static async Task<WebSocket> ConnectWithSessionAsync(TestApp app, string seat, string agent, string sessionId, byte[] kSess)
    {
        WebSocketClient client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = req =>
        {
            req.Headers["X-Horus-Session"] = sessionId;
            req.Headers["X-Horus-Auth"] = Auth.Handshake(kSess, "E1", seat, agent);   // 用 K_sess 握手
        };
        var uri = new Uri($"ws://localhost/ingest/events?examId=E1&seatId={seat}&agentId={agent}");
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    [Fact]
    public async Task OIDC会话_本人事件_接受()
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        (string sid, byte[] k) = MakeSession(app, "A07", "ag-A07");

        using WebSocket ws = await ConnectWithSessionAsync(app, "A07", "ag-A07", sid, k);
        // 本人身份 + K_sess 签名 → ack
        string ev = Ws.SignedEvent("E1", "A07", "ag-A07", "PC", SignalType.WindowFocus,
            new() { ["title"] = "t" }, 0, 1, psk: k);
        await Ws.SendAsync(ws, ev);
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OIDC会话_拿自己会话给他人栽赃_拒(){ await ForgeRejected(bodySeat: "B99", bodyAgent: "ag-B99"); }

    [Fact]
    public async Task OIDC会话_改agentId抢占他人seq_拒(){ await ForgeRejected(bodySeat: "A07", bodyAgent: "ag-VICTIM"); }

    private static async Task ForgeRejected(string bodySeat, string bodyAgent)
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        // 攻击者持自己的合法会话(bound to A07/ag-A07)
        (string sid, byte[] k) = MakeSession(app, "A07", "ag-A07");

        using WebSocket ws = await ConnectWithSessionAsync(app, "A07", "ag-A07", sid, k);
        // 事件体填**他人身份**,但用自己会话的 K_sess 签名(sig 能过)—— 服务器须以身份不符拒收(闭合 A1/A2)
        string ev = Ws.SignedEvent("E1", bodySeat, bodyAgent, "PC", SignalType.BrowserUrl,
            new() { ["url"] = "https://chat.openai.com/" }, 80, 1, psk: k);
        await Ws.SendAsync(ws, ev);
        JsonElement resp = await Ws.ReceiveAsync(ws);
        Assert.Equal("error", resp.GetProperty("type").GetString());
        Assert.Equal("identity_mismatch", resp.GetProperty("code").GetString());
    }

    [Fact]
    public async Task both模式_旧PSK连接仍可用_迁移共存()
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        // 不带 session,走 PSK 路径(legacy)
        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        string ev = Ws.SignedEvent("E1", "A07", "ag-A07", "PC", SignalType.WindowFocus, new() { ["title"] = "t" }, 0, 1);
        await Ws.SendAsync(ws, ev);
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());
    }

    [Fact]
    public async Task both灰度_OIDC座位_authMode为oidc且迁移覆盖全量()
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        var (sid, k) = MakeSession(app, "A07", "ag-A07");
        using WebSocket ws = await ConnectWithSessionAsync(app, "A07", "ag-A07", sid, k);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        await Ws.SendAsync(ws, Ws.SignedEventTs("E1", "A07", "ag-A07", "PC", SignalType.Heartbeat,
            new() { ["status"] = "alive" }, 0, 1, now, psk: k));
        await Ws.ReceiveAsync(ws);

        JsonElement seats = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.Equal("oidc", seats[0].GetProperty("authMode").GetString());   // 有会话 → 已迁移

        // 迁移覆盖:1/1 在线座位走 OIDC → 可切 oidc。
        JsonElement pf = await http.GetFromJsonAsync<JsonElement>("/api/preflight");
        Assert.Equal(1, pf.GetProperty("migration").GetProperty("onlineTotal").GetInt32());
        Assert.Equal(1, pf.GetProperty("migration").GetProperty("onlineOidc").GetInt32());
    }
}

/// M4·RBAC·S8:监考员看板 OIDC 登录 + 管理端授权（长老进 / 弟子拒 / 过期拒 / 静态令牌退役）。
public class AdminOidcTests
{
    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static OidcClaims Claims(string sub = "sub-1") => new(sub, "姓名", "user");

    private static HttpClient NoRedirect(TestApp app)
        => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetCookie(HttpClient http, string path, string? sid)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (sid is not null) req.Headers.Add("Cookie", "horus_admin=" + sid);
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task 长老会话_放行管理端()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession elder = store.Create(Claims("elder"), Now(), 180);
        HttpResponseMessage resp = await GetCookie(http, "/api/exams", elder.SessionId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task 建得出管理会话就放行_本地不再自己判角色()
    {
        // ★★ 这条取代了原来的「弟子会话_拒管理端」。**不变量没变、位置变了**:
        //   「学生进不了看板」现在由**身份中心**保证 —— 看板客户端归属平台 `horus-admin`,
        //   没有该平台权限的人在贝塔通的授权阶段就被拒、换不到 code,于是**建不出这个会话**
        //   (贝塔通 P83)。因此本地 gate 的正确语义就是「会话在且未过期即放行」。
        // ★ 正面锁住它,是为了拦住日后「好心」把角色判据加回本地 —— 那会与身份中心分家,
        //   变成同一个判据两个来源(本仓库与贝塔通都反复吃过这个亏)。
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = store.Create(Claims(), Now(), 180);
        HttpResponseMessage resp = await GetCookie(http, "/api/exams", s.SessionId);
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public void 管理会话不得再带任何角色字段()
    {
        // 断言的是**不变量本身**而不是守卫的镜像:AdminSession 只该有会话标识 + 身份 + 时间,
        // 一旦有人加回 UserType / IsElder / Role 之类,这条立刻红。
        string[] props = [.. typeof(AdminSession).GetProperties().Select(p => p.Name)];
        Assert.Equal(["SessionId", "Sub", "Name", "IssuedAt", "ExpiresAt"], props);
    }

    [Fact]
    public async Task 无权限时停在未登录_给一句人话而不是系统错误()
    {
        // ★ 贝塔通 P83 之后,「没有监考台权限」在**授权阶段**就被拒,回调带的是
        //   `error=access_denied` 且**没有 code**。按其 rp-contract「无权限时停在未登录」:
        //   ① 停在未登录(不种 cookie、不建会话)②必须给一句人话,**不得显示成「系统错误」**。
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await http.GetAsync("/cb?error=access_denied&state=whatever");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie"));            // ★ 停在未登录
        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("监考台", html);                               // 说清是哪个权限
        Assert.Contains("联系管理员", html);                            // 说清下一步该干什么
        Assert.DoesNotContain("系统错误", html);
    }

    [Fact]
    public async Task 无会话_拒管理端()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await GetCookie(http, "/api/exams", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 过期会话_拒管理端()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession stale = store.Create(Claims("elder"), Now() - 10000, 1);   // issued_at 远古 + 1min 寿命 → 已过期
        HttpResponseMessage resp = await GetCookie(http, "/api/exams", stale.SessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 静态令牌登录_oidc模式退役()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await http.PostAsJsonAsync("/api/login", new { token = "anything" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("use_oidc_login", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task admin_login_重定向到wentian授权页()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await http.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        string loc = resp.Headers.Location!.ToString();
        Assert.Contains("oidc.test/auth", loc);   // ★ 贝塔通挂根路径,不是 /oauth/authorize
        Assert.Contains("client_id=horus-dashboard", loc);
        Assert.Contains("code_challenge=", loc);
        Assert.Contains("code_challenge_method=S256", loc);
        Assert.Contains("state=", loc);
        Assert.Contains("response_type=code", loc);
    }

    [Fact]
    public async Task cb_未知state_拒()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await http.GetAsync("/cb?code=x&state=nonexistent");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
