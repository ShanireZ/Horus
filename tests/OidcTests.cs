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

/// M4·S1 + 工作階段金鑰:id_token 離線驗簽(RS256)與 ECDH K_sess 協商的迴歸鎖定。
public class OidcTokenValidatorTests
{
    /// ★★★ 驗簽用的 issuer 夾具:**虛構網域**。
    /// ★ 本組測的是驗簽器的行為(iss 不符要拒 / aud 不符要拒 / kid 輪換……),
    ///   ★★ **與貝塔通此刻的 issuer 是什麼完全無關** —— 寫真實網域只會讓它跟著上游一起腐爛,
    ///   而 2026-08-26 實測:那種腐爛是**自洽的**,一道判據都不會紅。
    private const string Issuer = "https://idp.test";
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
        // ★★ **直接斷言不變數本身**:驗簽的產物**只有** sub 一項。
        //   貝塔通把 `conformIdTokenClaims` 留在上游預設 `true`,授權碼流下 id_token 裡根本沒有
        //   `name` / `preferred_username`(其 docs/rp-contract.md)。把它們從 id_token 取會恆得空串,
        //   而空的 Username 讓 ExamDispatch.SeatFrom 對每個人退回成 sub —— 不回報錯誤、不拋異常。
        //   舊 wentian 之所以能那麼幹,是它專門設了 `conformIdTokenClaims: false`,**那條豁免不隨遷移過來**。
        Assert.Single(typeof(OidcSubject).GetProperties());
    }

    [Fact]
    public void id_token里混进身份claim也不被采信()
    {
        // 貝塔通 P81:即便權杖裡混進了那些 claim(例如對著舊 IdP 跑),也一律不進身分 ——
        // 判據是「驗簽的出口只有 sub」,而不是「我們沒請求所以不會有」。
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

    // ---- userinfo:身分 claims 的唯一來源 ----

    [Fact]
    public void userinfo取出姓名与用户名()
    {
        OidcClaims c = Userinfo.Parse(
            """{"sub":"sub-abc","name":"叶锋","preferred_username":"ye_feng"}""",
            new OidcSubject("sub-abc"));

        Assert.Equal("sub-abc", c.Sub);
        Assert.Equal("叶锋", c.Name);             // profile.name = 真實姓名
        Assert.Equal("ye_feng", c.Username);      // profile.preferred_username → 座位標識
        Assert.Equal(3, typeof(OidcClaims).GetProperties().Length);   // Sub / Name / Username,不多不少
    }

    [Fact]
    public void 未设用户名的账号_preferred_username缺失_取空串而不是编一个()
    {
        // ★ 貝塔通對**未設定使用者名的帳號直接省略** `preferred_username`(不發空串,見其 rp-contract)。
        //   這裡必須取到空串,好讓 ExamDispatch.SeatFrom 走它那條退回 sub 的分支 ——
        //   在這裡編一個預設值會讓「沒設使用者名的人」共用同一個座位號。
        OidcClaims c = Userinfo.Parse("""{"sub":"sub-x"}""", new OidcSubject("sub-x"));
        Assert.Equal("", c.Username);
        Assert.Equal("", c.Name);
        Assert.Equal("sub-x", c.Sub);
    }

    [Fact]
    public void userinfo的sub与id_token不符_丢弃()
    {
        // ★★ OIDC Core 5.3.2 的硬性要求。不比這一下,「權杖是誰的」與「資料是誰的」就成了兩件事。
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
        // 貝塔通不給用戶端登記 `userinfo_signed_response_alg`,回應恆為 JSON。
        // 真收到 JWT(後台登記被改過)時要報出來 —— 靜默當成空 claims 又是一次「無症狀」。
        var ex = Assert.Throws<OidcValidationException>(() => Userinfo.Parse(
            "eyJhbGciOiJQUzI1NiJ9.e30.sig", new OidcSubject("sub-abc")));
        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public async Task 取userinfo带Bearer访问令牌()
    {
        // 少了這個頭,貝塔通回 401 —— 而 401 在舊寫法裡根本不會發生(壓根沒這一次請求),
        // 所以這條鎖的是「請求確實發出去了、且帶對了憑證」。
        var handler = new RecordingHandler("""{"sub":"sub-abc","name":"叶锋","preferred_username":"ye_feng"}""");
        using var http = new HttpClient(handler);
        OidcClaims c = await Userinfo.FetchAsync(
            http, "https://idp.test/me", "at-xyz", new OidcSubject("sub-abc"), NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastAuth?.Scheme);
        Assert.Equal("at-xyz", handler.LastAuth?.Parameter);
        Assert.Equal("https://idp.test/me", handler.LastUrl);
        Assert.Equal("ye_feng", c.Username);
    }

    [Fact]
    public async Task userinfo非2xx_登录失败而不是放行空身份()
    {
        // ★ fail-closed,與 R3 同一取向:寧可登不進去,也不要一個「進得去但誰也認不出是誰」的考場。
        var handler = new RecordingHandler("nope", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<OidcValidationException>(() => Userinfo.FetchAsync(
            http, "https://idp.test/me", "at-xyz", new OidcSubject("sub-abc"), NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public void userinfo端点跟着入口前缀走_而不是拼在issuer上()
    {
        // ★ 貝塔通 P72:`.cc` 是同一個 issuer 的第二條入口。端點**整套**換前綴,issuer 不動。
        //   userinfo 是本輪新增的一條,與 token / auth / jwks 同源派生,不能漏進這條口徑。
// ★★★ 貝塔通那一側用**虛構網域**:本條測的是**關係**(端點整套換前綴而 issuer 不動),
        //   ★ 而關係不隨上游搬域而變。2026-08-26 的教訓:這些夾具裡的真實網域此前彼此自洽地
        //   停在 P110 之前的舊值,**一道判據都沒紅** —— 自洽的抄件與正確的抄件長得一模一樣。
        var cfg = new ServerConfig { OidcIssuer = "https://idp.test", OidcEndpointBase = "https://idp-backup.test" };
        Assert.Equal("https://idp-backup.test/me", cfg.OidcUserinfoEndpoint);
        Assert.Equal("https://idp.test", cfg.OidcIssuer);
        Assert.NotEqual(cfg.OidcIssuer, cfg.OidcEndpointBase);   // ★ 要害是兩者**可以**不同主機

        // 不填前綴時回落 issuer;★ 路徑是根 `/me`,**不是**舊 wentian 的 `/oauth/userinfo`。
        var plain = new ServerConfig { OidcIssuer = "https://idp.test" };
        Assert.Equal("https://idp.test/me", plain.OidcUserinfoEndpoint);
    }

    /// 記錄最後一次請求的樁 handler(取 userinfo 用:要驗 Bearer 頭與目標 URL 都對)。
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
        // ★ 同一把 RSA 公鑰,PKCS1 與 PSS **兩種填充都驗得通** —— 所以「放寬成兩種都收」
        //   不是相容性問題而是安全問題:貝塔通根本簽不出 RS256(其 P58),
        //   多出來的那條路只服務於攻擊者。這裡用**同一把真私鑰**按 RS256 簽一枚合法權杖,必須被拒。
        using RSA rsa = RSA.Create(2048);
        var v = new OidcTokenValidator(BuildJwks(rsa, Kid), Issuer, Audience);
        string token = SignJwt(rsa, Kid, Payload(nonce: "n1"), alg: "RS256");
        var ex = Assert.Throws<OidcValidationException>(() => v.Validate(token, "n1", Now()));
        Assert.Contains("PS256", ex.Message);
    }

    [Fact]
    public void 允许的签名算法只有PS256一项()
    {
        // 直接斷言不變數本身,而不是守衛的鏡像:有人日後改成 "RS256 or PS256" 時這條立刻紅。
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
        // 縱深防禦:簽發方給了未來的 nbf(未生效時間)→ 拒(超時鐘偏移容差)。
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
        // nbf 已生效(過去)→ 不因 nbf 而拒(其餘合法則通過)。
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
        // 改 payload 段但不重簽 → 簽名對不上
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
        string token = SignJwt(evil, Kid, Payload(nonce: "n1"));   // 用 evil 私鑰簽、但 kid 冒充 good
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
        Assert.Equal(Convert.ToHexString(kAgent), Convert.ToHexString(kServer));   // 兩端一致

        // 第三方(攻擊者)只有雙方公鑰,拿自己的私鑰派生不出同一把 K_sess
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
        // 重複使用既有 HMAC 握手/簽名:金鑰換成 K_sess,兩端逐位元組一致
        Assert.Equal(Auth.Handshake(kA, "E1", "A07", "ag"), Auth.Handshake(kS, "E1", "A07", "ag"));
        Assert.Equal(EventCanonical.Sig(kA, "hashself", 5), EventCanonical.Sig(kS, "hashself", 5));
    }

    // ---- 小工具:建構 JWKS / 簽 JWT ----
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
        // ★ 只有標準 `profile` scope 的兩項。貝塔通 P81 之後 `horus_profile` 那套
        //   (user_type / nickname / dao_name / avatar / realm / realm_level / combat_power)不再存在。
        name = "叶锋", preferred_username = "ye_feng",
    });

    /// 按貝塔通真實的形態簽:**PS256**(RSA-PSS · SHA-256)。
    /// ★ 替身的形狀就是判據的一部分 —— 這裡若仍按舊 wentian 的 RS256 簽,
    ///   整套案例會在一個**生產上根本不存在**的演算法下全綠,而真接上貝塔通當場全紅。
    /// ★ 預設演算法**從生產常數派生**(`OidcTokenValidator.SigningAlg`),不是再寫一遍字面值 ——
    ///   兩處各寫一份必然漂,而漂了的表現是「測試在一個生產上不存在的演算法下全綠」。
    ///   常數本身由「允許的簽名演算法只有 PS256 一項」那條釘著,所以這不是把判據交給被測方。
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

/// M4·S4/S5:OIDC 工作階段綁定的採集鑑權 —— **閉合 A1(跨身分栽贓)/A2(seq 搶佔)** 的端到端鎖定。
public class OidcIngestAuthTests
{
    private static async Task CreateExamAsync(HttpClient http)
        => (await http.PostAsJsonAsync("/api/exams", new { examId = "E1", name = "T", seats = new[] { new { seatId = "A07" } } })).EnsureSuccessStatusCode();

    /// 直接建一條工作階段(繞過真 token 交換),回傳 (sessionId, kSess)。
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
        // 本人身分 + K_sess 簽名 → ack
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
        // 攻擊者持自己的合法工作階段(bound to A07/ag-A07)
        (string sid, byte[] k) = MakeSession(app, "A07", "ag-A07");

        using WebSocket ws = await ConnectWithSessionAsync(app, "A07", "ag-A07", sid, k);
        // 事件體填**他人身分**,但用自己工作階段的 K_sess 簽名(sig 能過)—— 伺服器須以身分不符拒收(閉合 A1/A2)
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
        // 不帶 session,走 PSK 路徑(legacy)
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
        Assert.Equal("oidc", seats[0].GetProperty("authMode").GetString());   // 有工作階段 → 已遷移

        // 遷移覆蓋:1/1 線上座位走 OIDC → 可切 oidc。
        JsonElement pf = await http.GetFromJsonAsync<JsonElement>("/api/preflight");
        Assert.Equal(1, pf.GetProperty("migration").GetProperty("onlineTotal").GetInt32());
        Assert.Equal(1, pf.GetProperty("migration").GetProperty("onlineOidc").GetInt32());
    }
}

/// M4·RBAC·S8:監考員看板 OIDC 登入 + 管理端授權（長老進 / 弟子拒 / 過期拒 / 靜態權杖退役）。
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
        // ★★ 這條取代了原來的「弟子工作階段_拒管理端」。**不變數沒變、位置變了**:
        //   「學生進不了看板」現在由**身分中心**保證 —— 看板用戶端歸屬平台 `horus-admin`,
        //   沒有該平台權限的人在貝塔通的授權階段就被拒、換不到 code,於是**建不出這個工作階段**
        //   (貝塔通 P83)。因此本機 gate 的正確語義就是「工作階段在且未過期即放行」。
        // ★ 正面鎖住它,是為了攔住日後「好心」把角色判據加回本機 —— 那會與身分中心分家,
        //   變成同一個判據兩個來源(本儲存庫與貝塔通都反覆吃過這個虧)。
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
        // 斷言的是**不變數本身**而不是守衛的鏡像:AdminSession 只該有工作階段標識 + 身分 + 時間,
        // 一旦有人加回 UserType / IsElder / Role 之類,這條立刻紅。
        string[] props = [.. typeof(AdminSession).GetProperties().Select(p => p.Name)];
        Assert.Equal(["SessionId", "Sub", "Name", "IssuedAt", "ExpiresAt"], props);
    }

    [Fact]
    public async Task 无权限时停在未登录_给一句人话而不是系统错误()
    {
        // ★ 貝塔通 P83 之後,「沒有監考臺權限」在**授權階段**就被拒,回呼帶的是
        //   `error=access_denied` 且**沒有 code**。按其 rp-contract「無權限時停在未登入」:
        //   ① 停在未登入(不種 cookie、不建工作階段)②必須給一句人話,**不得顯示成「系統錯誤」**。
        using var app = new TestApp(adminOidc: true);
        HttpClient http = NoRedirect(app);
        HttpResponseMessage resp = await http.GetAsync("/cb?error=access_denied&state=whatever");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie"));            // ★ 停在未登入
        string html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("监考台", html);                               // 說清是哪個權限
        Assert.Contains("联系管理员", html);                            // 說清下一步該幹什麼
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
        AdminSession stale = store.Create(Claims("elder"), Now() - 10000, 1);   // issued_at 遠古 + 1min 壽命 → 已過期
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
        Assert.Contains("oidc.test/auth", loc);   // ★ 貝塔通掛根路徑,不是 /oauth/authorize
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
