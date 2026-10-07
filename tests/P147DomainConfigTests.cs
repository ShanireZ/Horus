using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Horus.Agent.Config;
using Horus.Agent.Identity;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Identity;
using Xunit;

namespace Horus.Server.Tests;

/// P147：已批准的主域迁移，真实配置、验签与原生回调接线。
public class P147DomainConfigTests
{
    private const string Issuer = "https://pass.betaoi.cc";
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Horus.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 Horus 仓根");
    }
    private static AgentConfig Defaults() => AgentConfig.Load(Path.Combine(Path.GetTempPath(), "horus-p147-" + Guid.NewGuid().ToString("N") + ".json"));
    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void 主域默认与装机样例一致_Server仍需明确配置_LAN地址保持()
    {
        var cfg = Defaults();
        var sample = AgentConfig.Load(Path.Combine(Root(), "agent/agent.config.sample.json"));
        Assert.Equal(Issuer, cfg.OidcIssuer);
        Assert.Equal(Issuer, sample.OidcIssuer);
        Assert.Equal(Issuer + "/auth", sample.OidcAuthorizeBase);
        Assert.Null(sample.OidcEndpointBase);
        Assert.Equal("http://192.168.32.145:8080", cfg.ServerHttpBase);
        Assert.Equal("ws://192.168.32.145:8080", cfg.ServerWsBase);
        Assert.Equal(cfg.ServerHttpBase, sample.ServerHttpBase);
        Assert.Equal(cfg.ServerWsBase, sample.ServerWsBase);
        Assert.Null(new ServerConfig().OidcIssuer);
        Assert.True(string.IsNullOrEmpty(ServerConfig.Load(Path.Combine(Root(), "server/server.config.sample.json")).OidcIssuer));
        var server = new ServerConfig { OidcIssuer = cfg.OidcIssuer, OidcDashboardRedirectUri = "https://192.168.32.145:8443/cb" };
        Assert.Equal(Issuer + "/token", server.OidcTokenEndpoint);
        Assert.Equal(Issuer + "/jwks", server.OidcJwksEndpoint);
        Assert.Equal(Issuer + "/me", server.OidcUserinfoEndpoint);
        Assert.Equal(Issuer + "/session/end", server.OidcEndSessionEndpoint);
        Assert.Equal("https://192.168.32.145:8443/logout/done", server.PostLogoutRedirectUriEffective);
    }

    [Fact]
    public void 新issuer的PS256被实际默认验证器接受_旧issuer与错误受众被拒()
    {
        using RSA rsa = RSA.Create(2048);
        var key = rsa.ExportParameters(false);
        string jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "PS256", kid = "p147", n = B64(key.Modulus!), e = B64(key.Exponent!) } } });
        var validator = new OidcTokenValidator(jwks, Defaults().OidcIssuer!, "horus-client");
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string Token(string issuer, string audience)
        {
            string header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"PS256\",\"typ\":\"JWT\",\"kid\":\"p147\"}"));
            string payload = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iss = issuer, aud = audience, sub = "synthetic-p147", nonce = "p147-nonce", exp = now + 120 })));
            string input = header + "." + payload;
            return input + "." + B64(rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        }
        Assert.Equal("synthetic-p147", validator.Validate(Token(Issuer, "horus-client"), "p147-nonce", now).Sub);
        Assert.Throws<OidcValidationException>(() => validator.Validate(Token("https://pass.betaoi.cn", "horus-client"), "p147-nonce", now));
        Assert.Throws<OidcValidationException>(() => validator.Validate(Token(Issuer, "horus-dashboard"), "p147-nonce", now));
    }

    [Fact]
    public void 公网模板仅cc静态页_保留安全缓存且不挂彩排门()
    {
        string text = File.ReadAllText(Path.Combine(Root(), "deploy/hr.caddy"));
        string code = Regex.Replace(text, @"(?m)^\s*#.*$", "");
        Assert.Matches(@"(?m)^hr\.betaoi\.cc\s*\{", code);
        Assert.DoesNotMatch(@"(?m)^hr\.betaoi\.cn\s*\{", code);
        Assert.Contains("tls internal", code);
        Assert.Contains("root * /opt/horus-web", code);
        Assert.Contains("file_server", code);
        Assert.DoesNotContain("rehearsal", code);
        Assert.Contains("Content-Security-Policy", code);
        Assert.Contains("max-age=86400", code);
        Assert.Contains("no-cache", code);
    }

    [Fact]
    public async Task 原生登录真实loopback回调_授权为cc_PKCE与LAN换会话保持()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var serverKey = SessionCrypto.NewEphemeralKey();
        using var handler = new BrokerHandler(serverKey);
        using var broker = new HttpClient(handler);
        using var loopback = new HttpClient(new HttpClientHandler { UseProxy = false });
        Uri? authorize = null;
        Task<HttpResponseMessage>? callback = null;
        var cfg = Defaults();
        var result = await OidcLoginFlow.LoginAsync(cfg, broker, url =>
        {
            authorize = new Uri(url);
            var query = Query(authorize);
            callback = loopback.GetAsync(query["redirect_uri"] + "?code=synthetic-code&state=" + Uri.EscapeDataString(query["state"]), cts.Token);
        }, cts.Token);
        Assert.NotNull(authorize);
        Assert.Equal(Issuer + "/auth", authorize!.GetLeftPart(UriPartial.Path));
        var auth = Query(authorize);
        var redirect = new Uri(auth["redirect_uri"]);
        Assert.True(redirect.IsLoopback);
        Assert.Equal("http", redirect.Scheme);
        Assert.True(redirect.Port > 1024);
        Assert.Equal("/cb", redirect.AbsolutePath);
        Assert.Equal("S256", auth["code_challenge_method"]);
        using var response = await callback!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string page = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("Horus 监考已登录", page);
        Assert.Equal(Encoding.UTF8.GetByteCount(page), response.Content.Headers.ContentLength);
        Assert.Equal(new Uri(cfg.ServerHttpBase + "/oidc/exchange"), handler.Uri);
        var sent = handler.Body;
        Assert.Equal(auth["redirect_uri"], sent.GetProperty("redirectUri").GetString());
        Assert.Equal(auth["nonce"], sent.GetProperty("nonce").GetString());
        Assert.Equal(auth["code_challenge"], B64(SHA256.HashData(Encoding.ASCII.GetBytes(sent.GetProperty("codeVerifier").GetString()!))));
        Assert.Equal(SessionCrypto.DeriveKey(serverKey, sent.GetProperty("agentEcdhPub").GetString()!), result.KSess);
        Assert.Equal("synthetic-exam", result.ExamId);
    }
    private static Dictionary<string, string> Query(Uri url) => url.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
    private sealed class BrokerHandler(ECDiffieHellman key) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Body = body.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { sessionId = "synthetic-session", serverEcdhPub = SessionCrypto.ExportPublicKeyB64(key), expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, profile = new { sub = "synthetic-p147" }, examId = "synthetic-exam", seatId = "synthetic-seat" }), Encoding.UTF8, "application/json") };
        }
    }
}
