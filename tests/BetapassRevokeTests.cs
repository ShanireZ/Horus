using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// 貝塔通撤權通知接收端(`/internal/revoke`)的契約迴歸。
///
/// 判據逐條來自 BetaPass `docs/rp-contract.md`,**全部落在行為上**:
/// 驗籤(含 `aud` 是不是自己)、按 `jti` 冪等、三種 reason 處置相同、認不出的 reason 照清、
/// 按 `aud` 分別處置(撤監考臺不動採集面)。
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

    /// 按貝塔通真實形態籤,claims 含 `jti` 與 `purpose`。
    /// ★ 演算法與 JWKS 裡的 `alg` 都**從生產常數派生**,不再寫死字面值 —— 見 OidcTests 裡同款說明。
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

    /// ★ 回應體只能讀一次(HttpContent 是流)。此前分成 RevokedAsync / DuplicateAsync 兩個助手,
    ///   第二次讀就 ObjectDisposedException —— 症狀是「測試掛了」而不是「斷言不成立」,
    ///   花的時間遠超它值得的。統一讀一次再取兩項。
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
    [InlineData("something_new_in_2027")]   // ★ 認不出的新值也必須照清、照回 2xx
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
        Assert.Equal(1, (await ReadAsync(resp)).Revoked);   // ★ 不是恆 0:那條工作階段真被清掉了
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

        // ★ 貝塔通的 HTTP 超時只有 5 秒 —— 處理成功但花了 6 秒的一發會被判失敗並重投,
        //   同一個 `jti` 必然來第二次。「反正只會來一次」是錯的。
        HttpResponseMessage again = await PostAsync(http, token, jti, "sub-1", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        (bool dup2, int revoked2) = await ReadAsync(again);
        Assert.True(dup2);
        Assert.Equal(1, revoked2);   // 原樣回上次的答案,不是又清了一遍
    }

    [Fact]
    public async Task 撤监考台不动采集面()
    {
        // ★★ 這是拆平台(貝塔通 P83)換來的精確性:`horus-client` → 平台 `horus`(採集面)、
        //   `horus-dashboard` → 平台 `horus-admin`(監考臺)。撤掉某人的監考員資格
        //   **不該**把他作為考生的採集工作階段也斷掉。圖省事兩邊一起清就把這條精確性丟了。
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

        Assert.Null(adminSessions.Get(admin.SessionId, Now()));       // 監考臺工作階段沒了
        Assert.NotNull(sessions.Get(collect.SessionId, Now()));       // ★ 採集工作階段仍在
    }

    [Theory]
    [InlineData("user_logout", "退出所有站点")]
    [InlineData("platform_access_revoked", "权限已关闭")]
    [InlineData("password_changed", "密码已变更")]
    [InlineData("mfa_factor_changed", "二次验证")]
    public async Task 每种reason都给出对应的一句人话(string reason, string expectFragment)
    {
        // ★ 契約明寫四種 reason「**處置完全相同,區分只服務提示語**」——
        //   提示語正是這裡。撤權是刪行,不留痕的話被踢的人只看得到一句籠統的「登入已失效」。
        // ★★ 「平台權限被關掉」這一種尤其要緊:取消 SSO(貝塔通 P84)之後,
        //   無權限的人**每點一次登入都要完整輸一遍密碼**才被拒(其 P98,owner 明確不做緩解)。
        //   不告訴他真實原因,他會一遍遍白輸。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = adminSessions.Create(Claims(), Now(), 360);

        string jti = "j-" + reason;
        string token = SignNotice(rsa, "horus-dashboard", jti, "sub-1", reason);
        await PostAsync(http, token, jti, "sub-1", "horus-dashboard", reason);

        // 用那條已被刪掉的工作階段 id 再打一次 /api/* → 401 的回應體帶著「為什麼」
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
        // ★★ 貝塔通 P93 起**重投走完不進終態**:階梯是 5min×3 → 15min → 30min → 1h,
        //   走完轉「等待探活」掛起,探到本站回來後**從頭再投一遍,直至成功**。
        //   於是同一條通知會被投很多次,且**本站長時間下線再回來時會一次性收到一批積壓的**。
        // ★ 處置辦法就是 `jti` 冪等臺賬 —— **不要動它**,也**不要因為「這條太舊了」而丟棄**
        //   (舊不代表已經處理過)。這條鎖住「投多少次都穩」。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var store = app.Services.GetRequiredService<SessionStore>();
        store.Create("E1", "seat1", "agentA", "PC", Claims(), RandomNumberGenerator.GetBytes(32), Now(), 180);

        const string jti = "j-storm";
        string token = SignNotice(rsa, "horus-client", jti, "sub-1", "password_changed");

        for (int i = 0; i < 8; i++)   // 首投 + 7 次重投(階梯共 6 次,多打兩下也得穩)
        {
            HttpResponseMessage resp = await PostAsync(http, token, jti, "sub-1", "horus-client", "password_changed");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            (bool dup, int revoked) = await ReadAsync(resp);
            Assert.Equal(i > 0, dup);
            Assert.Equal(1, revoked);   // 恆回第一次的答案,不是每次都「又清了 0 條」
        }
    }

    [Fact]
    public async Task 认不出的reason_照清且兜底提示语不为空()
    {
        // ★★ 貝塔通那邊加一種 reason 時本端一行程式碼都不用改(處置本就與 reason 無關),
        //   但**提示語不能因此變成空白或「未知錯誤」** —— 備援那句必須自己站得住。
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
        // ★ 不驗 `aud` 就等於開了一個「誰都能踢人下線」的端點。這裡用**同一把真私鑰**籤一枚
        //   除 `aud` 外一切合法的權杖 —— 必須被拒,才證明擋的是 `aud` 而不是簽名。
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
    public async Task 拿探活令牌打撤权端点_踢不掉任何人()
    {
        // ★ **`aud` 撞車那件事本身已由對側 P100 修掉**(探活改發 `<client_id>#health`),
        //   所以今天一枚真探活權杖打到這裡,**光看 `aud` 就被拒**(見
        //   `BetapassHealthTests.探活专属aud的令牌打撤权端点_拒`)。
        //
        // ★★ **但這條案例不能刪,它測的是第二道防線**:一枚 `aud` = 裸 `client_id`、
        //   `sub` = `client_id` 自己的權杖(P100 之前探活權杖的形狀,也是任何「拿撤權權杖
        //   套別人訊息」的形狀)打進來,必須踢不掉任何人。
        //   落法 = 契約要求的「**權杖的 `sub` == 訊息的 `sub`**」(`BodyMatches`),
        //   而且本端更進一步:`RevokeBySub` 用的是**權杖裡的 `sub`**、不是訊息裡的。
        //
        // ★★★ **為什麼兩道都要留**:P100 那道**相依對側**保持發專屬 `aud`;
        //   這一道**自洽、不相依對側任何欄位**。對側自己也是這麼說的(其 `main.ts` 註解:
        //   「`sub` 有意仍是 client_id …… 這一道要一直留著」)。
        //   ★ 刪掉它 = 把安全性整個押在對側不改口徑上,而對側這兩週已經改過兩次。
        //
        // ★ **不要改判成「要求權杖帶 purpose」** —— 那相依契約沒承諾過的實作細節,
        //   對側哪天給探活權杖也加個 purpose,判據就悄悄失效且沒有任何症狀。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession victim = store.Create("E1", "seat1", "agentA", "PC", Claims("victim-sub"),
            RandomNumberGenerator.GetBytes(32), Now(), 180);

        // 一枚**真的探活權杖**:aud = 本機 client_id、sub = client_id 自己、**沒有 purpose**
        // (核對 BetaPass `src/main.ts` 的 signToken)。
        string header = JsonSerializer.Serialize(new { alg = OidcTokenValidator.SigningAlg, typ = "JWT", kid = Kid });
        string payload = JsonSerializer.Serialize(new
        {
            iss = Issuer, aud = "horus-client", sub = "horus-client",
            jti = Guid.NewGuid().ToString("N"), exp = Now() + 120,
        });
        string input = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payload));
        string probeToken = input + "." + B64Url(rsa.SignData(Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

        // 攻擊者拿它去撤別人的權:訊息裡塞受害者的 sub
        HttpResponseMessage resp = await PostAsync(http, probeToken, "whatever", "victim-sub", "horus-client", "password_changed");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);      // 訊息與權杖對不上 → 拒
        Assert.NotNull(store.Get(victim.SessionId, Now()));            // ★ 受害者還在
    }

    [Fact]
    public async Task 报文与令牌对不上_拒()
    {
        // 權杖是權威,訊息只是便於閱讀的副本。對不上說明有人拿一枚合法權杖套了別人的訊息。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        string token = SignNotice(rsa, "horus-client", "j4", "sub-1", "password_changed");
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), token, "j4", "sub-OTHER", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task 未开OIDC时回410而不是404_否则会被无限重投()
    {
        // ★ 貝塔通判成功的口徑是 2xx,而 **404 / 405 與 5xx 一樣會被重投 12 次**
        //   (「RP 還沒實作這個端點」正是最需要重投的情形)。所以路由必須**始終掛上**,
        //   由端點自己回一個「別再投了」的狀態,而不是讓它落到 404 去被反覆敲。
        using var app = new TestApp();   // 預設 psk,兩條 OIDC 連線路徑都沒開
        HttpResponseMessage resp = await PostAsync(app.CreateClient(), null, "j5", "sub-1", "horus-client", "password_changed");
        Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
    }
}
