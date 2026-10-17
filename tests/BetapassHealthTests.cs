using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// 貝塔通存活探測端點(`GET /internal/health`)的契約迴歸(其 P94)。
///
/// ★★ **為什麼這幾條必須有測試**:這個端點沒有任何使用者可見行為 —— 它壞掉時**本站零症狀**,
///   要到「某人權限撤了卻還在裡面」被人追查時才發現。沒有測試的規則就是沒人執行的規則。
///   ★ 注意:**「不實作它就會被判離線」那條舊說法已不成立**(對側 `5bb16b6` 之後
///   任何 HTTP 應答都算線上),仍然要實作的理由見 `BetapassHealthEndpoint` 的類註解。
///
/// 判據逐條來自 BetaPass `docs/rp-contract.md`「`GET /internal/health`:必辦項」:
/// 必須驗簽(含 `aud` 是不是自己)、任意 2xx 即算線上、不查庫。
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

    /// 按**貝塔通真實的探活權杖形態**簽(核對 BetaPass `src/main.ts` 的 `signToken`):
    /// claims 只有 `iss` / `aud` / `sub`(= client_id 自己)/ `jti` / `exp`,★ **沒有 `purpose`**。
    /// 照撤權權杖的形狀去寫測試就會驗出一個不存在的口徑。
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

        HttpResponseMessage resp = await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client#health"));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());   // 無回應體
    }

    [Fact]
    public async Task 监考台的client_id也认()
    {
        // ★ 兩個用戶端**各登記一個回呼位址,因此探活也是兩條**(採集面 `horus-client` /
        //   監考臺 `horus-dashboard`)。只認其中一個的話,另一條探活恆驗不過 ——
        //   ★ 今天它不會導致判離線(對側任何 HTTP 應答都算線上),但會在其後台留下一條
        //   永久的 `HTTP 401`,且對側判據一改回去那一半立刻失效。兩邊都要認。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(http, SignProbe(rsa, "horus-client#health"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ProbeAsync(http, SignProbe(rsa, "horus-dashboard#health"))).StatusCode);
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
        // ★ 用**同一把真私鑰**簽一枚除 `aud` 外一切合法的權杖 —— 必須被拒,
        //   才證明擋的是 `aud` 而不是簽名。不驗 `aud` 就等於把接入拓撲白送出去。
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
            (await ProbeAsync(app.CreateClient(), SignProbe(forged, "horus-client#health"))).StatusCode);
    }

    [Fact]
    public async Task 过期的令牌_拒()
    {
        // 探活權杖壽命 2 分鐘(與撤權通知同口徑)。放行過期權杖等於把一枚抓到就能永久用的探針留在外面。
        // ★ 減 300 秒而不是「剛過期一點」:驗簽器有 **60 秒時鐘容差**(`ClockSkewSeconds`),
        //   貼著邊界寫出來的案例測的是容差不是過期判定,而且會隨容差調整無聲失效。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client#health", exp: Now() - 300))).StatusCode);
    }

    [Fact]
    public async Task 未开OIDC时回410而不是404()
    {
        // 與 `/internal/revoke` 同口徑:路由**始終掛上**,由端點自己回一個明確狀態。
        // ★ 410 **不會**讓對側把本站判離線(其 `5bb16b6` 之後任何 HTTP 應答都算線上),
        //   而是被如實記成一條 `HTTP 410` 擺在其後台 —— 這正是要的:
        //   「這臺機器不是接入方」應該有人看見,而不是安靜地當作一切正常。
        using var app = new TestApp();   // 預設 psk,兩條 OIDC 連線路徑都沒開
        Assert.Equal(HttpStatusCode.Gone, (await ProbeAsync(app.CreateClient(), null)).StatusCode);
    }

    // ---- 專屬 `aud`(owner 2026-08-11 拍板) ----

    [Fact]
    public async Task 专属aud的探活令牌_被接受()
    {
        // owner 拍板由貝塔通給探活權杖一個專屬 `aud`(`<client_id>#health`),
        // 把契約裡「多個入站端點唯一的區分是 `aud`」從一條帶例外的規則恢復成**真不變數**。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client#health"))).StatusCode);
    }

    [Fact]
    public async Task 默认只认专属aud_裸client_id被拒()
    {
        // ★★★ **這條是 P100 到底有沒有落成的判據。**
        //   裸 `client_id` 是 `/internal/revoke` 的值;探活端點收下它,
        //   「`aud` 攔不住撤權/探活這一對」的例外就還在 —— 而消滅它正是 P100 的全部目的。
        // ★ 對側 2026-08-11 已寫好(其 `healthProbeAudience()` 只發 `<client_id>#health`,
        //   契約明寫「`/internal/health` **只認** `<client_id>#health`」),所以本儲存庫預設就該是嚴格的。
        //   ★ 核即時對側那批改動還**只在工作樹裡、未提交**;決策已由 owner 拍板、程式碼已寫好,
        //   而「線上跑的是哪一版」由部署決定 —— 那段視窗靠逃生口,不靠把預設值放寬。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client"))).StatusCode);
    }

    [Fact]
    public async Task 逃生口打开时_旧口径也收()
    {
        // ⏳ 只為「程式碼已更新、線上跑的貝塔通還是 P100 之前的版本」那段視窗留的。
        // ★ 逃生口必須**真的開得起來**,否則它就是個裝飾品:那段視窗裡探活會恆 401,
        //   而 401 **算線上**(對側任何 HTTP 應答都算活著),失效幾乎沒有症狀。
        // ★★ 但它預設是關的 —— 開關的預設值站在「例外已消滅」那一側,而不是「先相容著」。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid), healthLegacyAud: true);
        HttpClient http = app.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(http, SignProbe(rsa, "horus-client"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(http, SignProbe(rsa, "horus-client#health"))).StatusCode);
    }

    [Fact]
    public async Task 探活专属aud的令牌打撤权端点_拒()
    {
        // ★★★ **這條是拆開撤權/探活兩份候選集的全部意義。**
        //   兩者共用一份的話,給探活加一個可接受的 `aud` 會**連帶放寬 `/internal/revoke`** ——
        //   方向正好反了:本次改動是要讓 `aud` 重新能區分這兩個端點,
        //   而共用候選集會讓它連原來那點區分力都不剩。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));

        string token = SignProbe(rsa, "horus-client#health");
        var req = new HttpRequestMessage(HttpMethod.Post, BetapassRevokeEndpoint.Path)
        {
            Content = new StringContent(
                "{\"jti\":\"j1\",\"sub\":\"someone\",\"platform\":\"horus\",\"client_id\":\"horus-client\",\"reason\":\"password_changed\"}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().SendAsync(req)).StatusCode);
    }

    // ---- 探活的**實際結果**要被記下來(不是隻報設定) ----

    [Fact]
    public async Task 探活结果被记下来_通过与被拒都是()
    {
        // ★★★ 預檢此前只報「本機認哪些 `aud`」—— 那是**設定讀數**,它永遠綠,
        //   哪怕對側一次都沒探到過、或者每一發都被拒。本儲存庫自己反覆寫著
        //   「設定化最容易做成裝飾品,**斷言必須落在行為上**」,而那一項恰恰就是裝飾品。
        //   這條釘住「跑起來的事實真的被記下來了」。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        HttpClient http = app.CreateClient();
        var state = app.Services.GetRequiredService<BetapassProbeState>();

        // ① 起步:從未探過 —— 一直是這個狀態多半意味著**貝塔通後台沒登記撤權回呼**
        //    (探活位址按它同源推導),而那件事在別處沒有任何症狀。
        Assert.Equal((null, null, null), state.Snapshot());

        // ② 被拒
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ProbeAsync(http, SignProbe(rsa, "horus-client"))).StatusCode);   // 裸 client_id:口徑不對
        (double? ok1, double? rej1, string? why1) = state.Snapshot();
        Assert.Null(ok1);
        Assert.NotNull(rej1);
        Assert.False(string.IsNullOrWhiteSpace(why1));

        // ③ 通過
        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(http, SignProbe(rsa, "horus-client#health"))).StatusCode);
        (double? ok2, double? rej2, _) = state.Snapshot();
        Assert.NotNull(ok2);
        Assert.NotNull(rej2);          // 被拒那次仍留著
        Assert.True(ok2 > rej2);       // 而預檢按「誰更晚」判當前狀態
    }

    [Fact]
    public void 被拒告警按分钟节流_但状态每次都更新()
    {
        // ★ 口徑對不上時探活會**每 5 分鐘失敗一次、永遠失敗** —— 照記就是一條永不停止的告警流;
        //   完全不記(此前記的是 Debug)又等於沒有任何主動信號。所以按分鐘節流。
        // ★★ 節流的是**告警**,不是**狀態** —— 狀態每次都要更新,否則預檢讀到的是過期的事實。
        var state = new BetapassProbeState();
        Assert.True(state.RecordRejected(1000, "第一次"));
        Assert.False(state.RecordRejected(1030, "30 秒后"));    // 節流視窗內不再叫
        Assert.True(state.RecordRejected(1061, "61 秒后"));     // 視窗外再叫

        Assert.Equal("61 秒后", state.Snapshot().Reason);        // ★ 但每一次都更新了狀態
    }

    [Fact]
    public void 通过不记录任何告警状态()
    {
        // ★★ 成功**不叫** —— 它每 5 分鐘來一次,一條正常紀錄就會把被拒那條告警淹掉。
        //   「告警被正常紀錄埋掉」與「壓根沒告警」在實際使用中是同一回事。
        var state = new BetapassProbeState();
        state.RecordOk(1000);
        (double? ok, double? rej, string? why) = state.Snapshot();
        Assert.Equal(1000, ok);
        Assert.Null(rej);
        Assert.Null(why);
    }

    [Fact]
    public async Task 无令牌也算一次被拒()
    {
        // ★ 「誰都沒帶權杖來打」與「帶了但驗不過」對預檢是同一件事:口徑沒對上。
        //   漏記的話,預檢會一直顯示「從未收到過探活」,把人往「後台沒登記回呼」那個方向帶偏。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ProbeAsync(app.CreateClient(), null)).StatusCode);
        Assert.NotNull(app.Services.GetRequiredService<BetapassProbeState>().Snapshot().RejectedAt);
    }

    // ---- P101:本機有意不接撤權廣播 ----

    [Fact]
    public async Task P101默认_从未探过报ok而不是warn()
    {
        // ★★ 不登記撤權回呼 ⇒ 不在探活名單裡 ⇒「從未探過」**恆為真**。
        //   照舊報 warn 的話它會變成一條**永遠亮著的告警**,而永遠亮著的告警等於沒有告警 ——
        //   下一次真出事時沒人會看。所以要顯式說成「有意不接」,並把界限與觸發條件一併寫上。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminAuth: true, jwks: BuildJwks(rsa, Kid));
        JsonElement item = await PreflightItemAsync(app, "health_audience");

        Assert.Equal("ok", item.GetProperty("level").GetString());
        string detail = item.GetProperty("detail").GetString()!;
        Assert.Contains("有意不接", detail);
        Assert.Contains("拉取模型", detail);   // ★ 觸發條件要寫在旁邊,免得有人跑去開隧道
    }

    [Fact]
    public async Task P101下探活居然到了_照实报warn()
    {
        // ★★★ **口徑由部署聲明,而結論仍由行為決定。**
        //   本機聲明「不接廣播」而探活真的到了 ⇒ 有人登記了回呼 ⇒ 現實與聲明不符,
        //   必須照實說出來 —— **不能因為設定說不接就不看**,那就退回成設定讀數了。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminAuth: true, jwks: BuildJwks(rsa, Kid));
        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client#health"))).StatusCode);

        JsonElement item = await PreflightItemAsync(app, "health_audience");
        Assert.Equal("warn", item.GetProperty("level").GetString());
        Assert.Contains("居然到达", item.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task 期待广播的部署_从未探过仍报warn()
    {
        // ★ 另一種部署(將來走隧道/拉取,或別的現場):聲明期待廣播時,
        //   「從未探過」仍然是**該被看見的異常** —— 多半是後台沒登記回呼。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminAuth: true, jwks: BuildJwks(rsa, Kid), expectRevokePush: true);
        JsonElement item = await PreflightItemAsync(app, "health_audience");

        Assert.Equal("warn", item.GetProperty("level").GetString());
        Assert.Contains("从未收到过探活", item.GetProperty("detail").GetString());
    }

    /// 拉一次預檢,取出指定 id 的那一項。
    private static async Task<JsonElement> PreflightItemAsync(TestApp app, string id)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/preflight");
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        HttpResponseMessage resp = await app.CreateClient().SendAsync(req);
        resp.EnsureSuccessStatusCode();
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        foreach (JsonElement c in body.GetProperty("checks").EnumerateArray())
            if (c.GetProperty("id").GetString() == id) return c;
        throw new Xunit.Sdk.XunitException($"预检里没有 {id} 这一项");
    }

    [Fact]
    public async Task 探活不清任何会话()
    {
        // ★★ 契約點名過的坑:**不要拿 `/internal/revoke` 當探活端點** —— 那會真的清工作階段,
        //   等於每 5 分鐘把全體使用者從各站台踢一次。這條鎖住「探活端點確實是無副作用的」。
        using RSA rsa = RSA.Create(2048);
        using var app = new TestApp(authMode: "oidc", adminOidc: true, jwks: BuildJwks(rsa, Kid));

        var sessions = app.Services.GetRequiredService<SessionStore>();
        var adminSessions = app.Services.GetRequiredService<AdminSessionStore>();
        var claims = new OidcClaims("sub-1", "姓名", "u1");
        HorusSession collect = sessions.Create("E1", "seat1", "agentA", "PC", claims,
            RandomNumberGenerator.GetBytes(32), Now(), 180);
        AdminSession admin = adminSessions.Create(claims, Now(), 180);

        Assert.Equal(HttpStatusCode.NoContent,
            (await ProbeAsync(app.CreateClient(), SignProbe(rsa, "horus-client#health"))).StatusCode);

        Assert.NotNull(sessions.Get(collect.SessionId, Now()));
        Assert.NotNull(adminSessions.Get(admin.SessionId, Now()));
    }
}
