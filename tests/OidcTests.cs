using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public void 合法id_token_验签通过_取出富画像()
    {
        using RSA rsa = RSA.Create(2048);
        string jwks = BuildJwks(rsa, Kid);
        var v = new OidcTokenValidator(jwks, Issuer, Audience);

        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"));
        OidcClaims c = v.Validate(token, "n1", Now());

        Assert.Equal("sub-abc", c.Sub);
        Assert.Equal("elder", c.UserType);   // M4·RBAC:user_type claim 提取
        Assert.Equal("ye_feng", c.Username);
        Assert.Equal("叶锋", c.Nickname);
        Assert.Equal("问天", c.DaoName);
        Assert.Equal("金丹", c.Realm);
        Assert.Equal(3, c.RealmLevel);
        Assert.Equal(12345, c.CombatPower);
    }

    [Fact]
    public void 缺user_type_默认考生_不误授监考()
    {
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        // 手工造一枚**不含 user_type** claim 的合法 id_token(模拟旧 cpplearn / 未请求 horus_profile)。
        string payload = JsonSerializer.Serialize(new { iss = Issuer, aud = Audience, sub = "sub-x", exp = Now() + 3600, nonce = "n1" });
        OidcClaims c = v.Validate(SignJwt(rsa, Kid, payload), "n1", Now());
        Assert.Equal("disciple", c.UserType);   // fail-safe:缺省绝不当监考员
    }

    [Fact]
    public void 未知user_type值_归一化为考生()
    {
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string payload = JsonSerializer.Serialize(new { iss = Issuer, aud = Audience, sub = "sub-y", exp = Now() + 3600, nonce = "n1", user_type = "ADMIN" });
        OidcClaims c = v.Validate(SignJwt(rsa, Kid, payload), "n1", Now());
        Assert.Equal("disciple", c.UserType);   // 仅严格 "elder" 才是监考员
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
        user_type = "elder",
        username = "ye_feng", nickname = "叶锋", dao_name = "问天", avatar = "a.png",
        realm = "金丹", realm_level = 3, combat_power = 12345,
    });

    private static string SignJwt(RSA rsa, string kid, string payloadJson)
    {
        string header = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid });
        string signingInput = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payloadJson));
        byte[] sig = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
        var claims = new OidcClaims("sub-" + agent, "disciple", "user_" + agent, "昵称", "道号", "a.png", "金丹", 3, 999);
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

    private static OidcClaims Claims(string userType, string sub = "sub-1")
        => new(sub, userType, "user", "昵称", "道号", "", "金丹", 3, 100);

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
    public async Task 弟子会话_拒管理端()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession disciple = store.Create(Claims("disciple"), Now(), 180);
        HttpResponseMessage resp = await GetCookie(http, "/api/exams", disciple.SessionId);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
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
    public async Task admin_login_重定向到cpplearn授权页()
    {
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await http.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        string loc = resp.Headers.Location!.ToString();
        Assert.Contains("oidc.test/oauth/authorize", loc);
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
