using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Agent.Config;
using Horus.Contracts;

namespace Horus.Agent.Identity;

/// 采集签名凭证:PSK 模式 = {Psk, null};OIDC 模式 = {K_sess, sessionId}。UplinkClient/HashChain 用 Key 签名,
/// SessionId 非空时握手/上传带 X-Horus-Session 头。
public sealed record IngestCredential(byte[] Key, string? SessionId);

/// OIDC 登录换会话结果(Agent 侧)。
public sealed record OidcSession(string SessionId, byte[] KSess, double ExpiresAt, string ProfileJson);

/// M4·A1:Agent 登录流(拓扑 A·Server-Broker)。系统浏览器走 cpplearn 授权码 + PKCE,回调落本机 loopback,
/// 拿 code + PKCE verifier + 自己的 ECDH 公钥 POST 到 **Horus Server /oidc/exchange**(Server 持 secret 换 token+验签),
/// 得 sessionId + serverEcdhPub → 本地派生 K_sess(私钥不过网)。见 docs/m4-identity-oidc.md §3.1。
public static class OidcLoginFlow
{
    public static async Task<OidcSession> LoginAsync(
        AgentConfig cfg, HttpClient http, Action<string>? openBrowser = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cfg.OidcIssuer)) throw new InvalidOperationException("oidc 模式需配 oidcIssuer");

        // 1) PKCE(S256) + state + nonce + 临时 ECDH 密钥对
        string codeVerifier = RandUrl(32);
        string codeChallenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        string state = RandUrl(16);
        string nonce = RandUrl(16);
        using ECDiffieHellman agentKey = SessionCrypto.NewEphemeralKey();
        string agentPub = SessionCrypto.ExportPublicKeyB64(agentKey);

        // 2) loopback 监听(动态端口;native app 下 cpplearn 端口无关匹配)
        int port = FreePort();
        string redirectUri = $"http://127.0.0.1:{port}/cb";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/cb/");   // HttpListener 前缀需尾斜杠
        listener.Start();

        // 3) 构造 authorize URL,开系统浏览器(已登 cpplearn → near-无感)
        string authorizeUrl =
            $"{cfg.OidcIssuer!.TrimEnd('/')}/oauth/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(cfg.OidcClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(cfg.OidcScope)}" +
            $"&code_challenge={codeChallenge}&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}&nonce={Uri.EscapeDataString(nonce)}";
        (openBrowser ?? OpenBrowser)(authorizeUrl);

        // 4) 等回调,取 code(校验 state)
        string code = await AwaitCodeAsync(listener, state, ct);
        listener.Stop();

        // 5) 到 Horus Server 换会话(Server 持 secret 换 token + 离线验签)
        var reqBody = new
        {
            code, codeVerifier, redirectUri, nonce, agentEcdhPub = agentPub,
            examId = cfg.ExamId, seatId = cfg.SeatId, agentId = cfg.AgentId, machineId = cfg.MachineId,
        };
        using var content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await http.PostAsync($"{cfg.ServerHttpBase}/oidc/exchange", content, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("OIDC 换会话失败: " + body);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        string sessionId = root.GetProperty("sessionId").GetString()!;
        string serverPub = root.GetProperty("serverEcdhPub").GetString()!;
        double expiresAt = root.TryGetProperty("expiresAt", out JsonElement ex) ? ex.GetDouble() : 0;
        string profileJson = root.TryGetProperty("profile", out JsonElement p) ? p.GetRawText() : "{}";

        // 6) 本地派生 K_sess(与 Server 一致;私钥全程不过网)
        byte[] kSess = SessionCrypto.DeriveKey(agentKey, serverPub);
        return new OidcSession(sessionId, kSess, expiresAt, profileJson);
    }

    private static async Task<string> AwaitCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            HttpListenerContext hc = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            string? code = hc.Request.QueryString["code"];
            string? state = hc.Request.QueryString["state"];
            string? error = hc.Request.QueryString["error"];
            bool ok = error is null && code is not null && state == expectedState;
            byte[] page = Encoding.UTF8.GetBytes(ok
                ? "<html><body style='font-family:sans-serif'>Horus 监考已登录,可关闭本页返回考试。</body></html>"
                : "<html><body style='font-family:sans-serif'>登录失败,请重试。</body></html>");
            hc.Response.ContentType = "text/html; charset=utf-8";
            hc.Response.StatusCode = ok ? 200 : 400;
            await hc.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);
            hc.Response.Close();
            if (error is not null) throw new InvalidOperationException("OIDC 授权被拒: " + error);
            if (code is not null && state == expectedState) return code;
            // state 不符 / 无 code:忽略此次(可能是浏览器预取),继续等
        }
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { Console.Error.WriteLine("[horus-agent] 无法自动打开浏览器,请手动访问:\n" + url); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string RandUrl(int bytes) => B64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
