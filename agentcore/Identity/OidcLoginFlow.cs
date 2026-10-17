using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Agent.Config;
using Horus.Contracts;

namespace Horus.Agent.Identity;

/// 採集簽名憑證:PSK 模式 = {Psk, null};OIDC 模式 = {K_sess, sessionId}。UplinkClient/HashChain 用 Key 簽名,
/// SessionId 非空時握手/上傳帶 X-Horus-Session 頭。
public sealed record IngestCredential(byte[] Key, string? SessionId);

/// OIDC 登入換工作階段結果(Agent 側)。ExamId/SeatId 為**伺服器端派發值**(當前活躍考試 + username 派生座位),
/// Agent 採用它們填事件體/握手 —— 設定檔案裡已無這兩項。
/// <param name="ExpectedExamMinutes">伺服器給的「一場考試預計多久」,供開考預檢用(貝塔通 P91)。</param>
public sealed record OidcSession(string SessionId, byte[] KSess, double ExpiresAt, string ProfileJson,
    string ExamId, string SeatId, int ExpectedExamMinutes = 0)
{
    /// 開考預檢:這次登入的剩餘壽命夠不夠撐完一場考試(貝塔通 P91)。
    ///
    /// ★★ 原設計是**靠 IdP 下發一個 claim** 告訴 RP「你的工作階段還剩多久」——
    ///   **取消 SSO(其 P84)之後那條作廢**,改為 Horus 拿自己的 absolute 自己算。
    /// ★ 採集端每場考試都是**新登入**,所以正常情況下剩餘恆為滿;
    ///   這個檢查真正能逮到的是**伺服器把 `oidcSessionMinutes` 配短了** ——
    ///   而那件事的自然表現是「考到一半整場學生一起掉線」,到那時才發現就太晚了。
    ///   ★ 因此這裡的處置是**開考前一聲大的**,不是自動重登:剛登入完再登一次只會死循環,
    ///     真正的修法在伺服器設定上。
    public double RemainingMinutes(double nowUnix) => (ExpiresAt - nowUnix) / 60.0;

    public bool CoversExam(double nowUnix) =>
        ExpectedExamMinutes <= 0 || RemainingMinutes(nowUnix) >= ExpectedExamMinutes;
}

/// M4·A1:Agent 登入流(拓撲 A·Server-Broker)。系統瀏覽器走 BetaPass 授權碼 + PKCE,回呼落本機 loopback,
/// 拿 code + PKCE verifier + 自己的 ECDH 公鑰 POST 到 **Horus Server /oidc/exchange**(Server 持 secret 換 token+驗籤),
/// 得 sessionId + serverEcdhPub → 本機派生 K_sess(私鑰不過網)。見 docs/m4-identity-oidc.md §3.1。
public static class OidcLoginFlow
{
    public static async Task<OidcSession> LoginAsync(
        AgentConfig cfg, HttpClient http, Action<string>? openBrowser = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cfg.OidcIssuer)) throw new InvalidOperationException("oidc 模式需配 oidcIssuer");

        // 1) PKCE(S256) + state + nonce + 臨時 ECDH 金鑰對
        string codeVerifier = RandUrl(32);
        string codeChallenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        string state = RandUrl(16);
        string nonce = RandUrl(16);
        using ECDiffieHellman agentKey = SessionCrypto.NewEphemeralKey();
        string agentPub = SessionCrypto.ExportPublicKeyB64(agentKey);

        // 2) loopback 監聽(動態連接埠;native app 下 BetaPass 連接埠無關匹配)
        int port = FreePort();
        string redirectUri = $"http://127.0.0.1:{port}/cb";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/cb/");   // HttpListener 前綴需尾斜槓
        listener.Start();

        // 3) 建構 authorize URL,開系統瀏覽器。
        // ★★ **不再是「已登過就近乎無感」了**(貝塔通 P84 取消 SSO,2026-08-10):
        //   每一次授權請求都強制重新認證 —— 學員**每場考試都要完整輸一遍密碼**
        //   (過 MFA 的還要再過一次)。回呼流程本身一個字沒變,但**用時變長了**,
        //   開考引導要按這個安排(見 agent/Program.cs 裡那句提示)。
        //   ★ 這裡**不設等回呼的超時**:學生輸密碼 + 過 MFA 可能要好幾分鐘,
        //   卡個短超時的表現是「快輸完了卻被判失敗,還得從頭再來一遍」。
        string authorizeUrl =
            $"{cfg.OidcAuthorizeBase}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(cfg.OidcClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(cfg.OidcScope)}" +
            $"&code_challenge={codeChallenge}&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}&nonce={Uri.EscapeDataString(nonce)}";
        Console.WriteLine("[horus-agent] OIDC 授权地址(如浏览器未自动打开请手动访问):\n" + authorizeUrl);
        if (Environment.GetEnvironmentVariable("HORUS_OIDC_NO_BROWSER") != "1")
            (openBrowser ?? OpenBrowser)(authorizeUrl);

        // 4) 等回呼,取 code(檢核 state)
        string code = await AwaitCodeAsync(listener, state, ct);
        listener.Stop();

        // 5) 到 Horus Server 換工作階段(Server 持 secret 換 token + 離線驗籤)。
        //    不再上送 examId/seatId —— examId 伺服器端派發,seatId 由 OIDC 身分派生,二者從回應取回。
        var reqBody = new
        {
            code, codeVerifier, redirectUri, nonce, agentEcdhPub = agentPub,
            agentId = cfg.AgentId, machineId = cfg.MachineId,
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
        string examId = root.GetProperty("examId").GetString()!;   // 伺服器端派發,缺失即協定不符 → 拋錯
        string seatId = root.GetProperty("seatId").GetString()!;
        int expectedExamMinutes = root.TryGetProperty("expectedExamMinutes", out JsonElement em)
            && em.TryGetInt32(out int emv) ? emv : 0;   // 老伺服器不發這一項 → 0 = 不做開考預檢

        // 6) 本機派生 K_sess(與 Server 一致;私鑰全程不過網)
        byte[] kSess = SessionCrypto.DeriveKey(agentKey, serverPub);
        return new OidcSession(sessionId, kSess, expiresAt, profileJson, examId, seatId, expectedExamMinutes);
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
            // 明確正文長度，讓瀏覽器在 listener 停止前完成回呼頁讀取。
            hc.Response.ContentLength64 = page.Length;
            await hc.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);
            hc.Response.Close();
            if (error is not null) throw new InvalidOperationException("OIDC 授权被拒: " + error);
            if (code is not null && state == expectedState) return code;
            // state 不符 / 無 code:忽略此次(可能是瀏覽器預取),繼續等
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
