using System.Net.Http.Json;
using System.Security.Cryptography;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// **三道門 + 存活心跳**的迴歸(貝塔通 P88–P92,契約見其 `docs/rp-contract.md`)。
///
/// ★★ 這一整套的失效形態都是**靜默的**:
///   - 少一道門 → 工作階段該失效時沒失效,**沒有任何症狀**,只在事後追查「他為什麼還在裡面」時才發現;
///   - `active` 恆為 false → 每個人考到 30 分鐘被踢,看起來像網路問題;
///   - 節流判據沒按欄位分開 → 只有多標籤的人偶發被踢。
///   沒有測試的規則就是沒人執行的規則,所以每一道門、每一條反向約束都在這裡各有一條。
public class SessionGateTests
{
    private const double T0 = 1_700_000_000;
    private static readonly double Min = 60.0;

    private static OidcClaims Claims(string sub = "sub-1") => new(sub, "姓名", "u1");

    /// 直接讀兩個時間戳列 —— 判據只有 SQL 一處(見 SessionGates 末尾的說明),
    /// 所以迴歸也只能打真列,不能打某個 C# 副本。
    private static (double Hb, double Seen) Stamps(Db db, string table, string sessionId)
        => db.Read(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                $"SELECT last_heartbeat_at,last_seen_at FROM {table} WHERE session_id=@s", ("@s", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            Assert.True(r.Read());
            return (r.GetDouble(0), r.GetDouble(1));
        });

    private static HorusSession NewCollect(SessionStore store, int absoluteMinutes = 360)
        => store.Create("E1", "seat1", "agentA", "PC", Claims(), RandomNumberGenerator.GetBytes(32), T0, absoluteMinutes);

    // ---- absolute ----

    [Fact]
    public void absolute到点_拒()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store, absoluteMinutes: 60);

        // ★ 要單獨驗 absolute,就得先把另兩道門餵飽 —— 否則 idle(30min)會先攔下,
        //   案例看起來過了但測的根本不是 absolute。
        for (double t = T0 + 180; t < T0 + 61 * Min; t += 180) store.Heartbeat(s.SessionId, active: true, t);

        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 59 * Min).Gate);
        Assert.Equal(SessionGate.Absolute, store.GetWithGate(s.SessionId, T0 + 61 * Min).Gate);
    }

    [Fact]
    public void absolute不被活动推迟()
    {
        // ★★ 這是三道門裡唯一「任何活動都推不動」的一道。把它做成滑動視窗,
        //   等於把「最後一道防線」變成「只要有人在用就永不過期」——
        //   公用機房那個威脅模型下,那正是要擋的場景。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store, absoluteMinutes: 60);

        // 一路 active:true 心跳打到 absolute 前一刻(節流 150 秒,所以每 3 分鐘一發都能寫進去)
        for (double t = T0 + 180; t < T0 + 60 * Min; t += 180) store.Heartbeat(s.SessionId, active: true, t);
        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 59.9 * Min).Gate);

        Assert.Equal(SessionGate.Absolute, store.GetWithGate(s.SessionId, T0 + 60 * Min + 1).Gate);
    }

    // ---- 心跳門 ----

    [Fact]
    public void 无心跳15分钟_被心跳门拒()
    {
        // 擋的是「關頁面 / 關瀏覽器走人」。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 14 * Min).Gate);
        Assert.Equal(SessionGate.Heartbeat, store.GetWithGate(s.SessionId, T0 + 16 * Min).Gate);
    }

    // ---- idle 門 ----

    [Fact]
    public void activefalse的心跳续心跳门但不续idle()
    {
        // ★★ 整套設計的核心分工:**任何**心跳續心跳門;**只有 active:true** 才額外續 idle。
        //   合併成一個判據的後果是「頁面開著就永不過期」,idle 那道門直接失效。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        // 頁面開著但人走了:心跳照發,active 恆 false
        for (double t = T0 + 180; t <= T0 + 45 * Min; t += 180) store.Heartbeat(s.SessionId, active: false, t);

        // 心跳門被續住了 —— 20 分鐘時若只有心跳門,它是過的
        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 20 * Min).Gate);
        // 但 idle 那道門到點了(30 分鐘)
        Assert.Equal(SessionGate.Idle, store.GetWithGate(s.SessionId, T0 + 31 * Min).Gate);
    }

    [Fact]
    public void activetrue的心跳续idle()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        for (double t = T0 + 180; t <= T0 + 45 * Min; t += 180) store.Heartbeat(s.SessionId, active: true, t);

        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 46 * Min).Gate);
    }

    // ---- 判定順序 ----

    [Fact]
    public void 判定顺序_更强的终止理由排前面()
    {
        // revoked → absolute → idle → heartbeat。三道同時到點時報最靠前的那一道,
        // 這樣給使用者的話才是對的(「本次登入已達最長時限」而不是「你太久沒動了」)。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store, absoluteMinutes: 60);

        // 三道全到點
        Assert.Equal(SessionGate.Absolute, store.GetWithGate(s.SessionId, T0 + 120 * Min).Gate);

        // idle 與心跳同時到點(absolute 還沒到)→ 報 idle
        HorusSession s2 = NewCollect(store);
        Assert.Equal(SessionGate.Idle, store.GetWithGate(s2.SessionId, T0 + 40 * Min).Gate);
    }

    [Fact]
    public void 撤权即删行_是判定顺序里最前的一道()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        store.RevokeBySub("sub-1", "password_changed", T0);

        (HorusSession? session, SessionGate gate) = store.GetWithGate(s.SessionId, T0);
        Assert.Null(session);
        Assert.Equal(SessionGate.Ok, gate);   // 查無此工作階段:沒有哪一道門攔下它,它壓根不在了
    }

    // ---- 節流(150 秒)與「判據按欄位分開」 ----

    [Fact]
    public void 节流窗口内的心跳不写()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        HorusSession s = NewCollect(store);

        Assert.False(store.Heartbeat(s.SessionId, active: true, T0 + 10));    // 距建工作階段才 10 秒
        Assert.Equal((T0, T0), Stamps(db, "oidc_sessions", s.SessionId));

        Assert.True(store.Heartbeat(s.SessionId, active: true, T0 + 151));    // 過了 150 秒視窗
        Assert.Equal((T0 + 151, T0 + 151), Stamps(db, "oidc_sessions", s.SessionId));
    }

    [Fact]
    public void 节流判据按字段分开_activefalse不吞掉随后的activetrue()
    {
        // ★★ 本次改造裡最容易做錯的一處。只看「心跳夠不夠新」的話:
        //   標籤 A 的 active:false 剛寫完,標籤 B 隨後的 active:true 就被整體節流掉,
        //   **「人還在」這個信號隨之丟失** —— 表現只是「多開標籤的人偶發被 idle 踢掉」。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        HorusSession s = NewCollect(store);

        // 標籤 A:人不在
        Assert.True(store.Heartbeat(s.SessionId, active: false, T0 + 200));
        Assert.Equal((T0 + 200, T0), Stamps(db, "oidc_sessions", s.SessionId));   // 只動心跳,不動 idle

        // 標籤 B:10 秒後報「人還在」—— 心跳那道門還在節流視窗內,但 idle 那個時間戳已經夠舊了
        Assert.True(store.Heartbeat(s.SessionId, active: true, T0 + 210));
        (double hb, double seen) = Stamps(db, "oidc_sessions", s.SessionId);
        Assert.Equal(T0 + 200, hb);      // ★ 心跳仍被節流住(沒被這一發順手推走)
        Assert.Equal(T0 + 210, seen);    // ★★ 而「人還在」被記下了 —— 沒被吞掉
    }

    [Fact]
    public void 心跳打不存在的会话_不报错且返回false()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        Assert.False(store.Heartbeat("sess_nope", active: true, T0 + 1000));
        Assert.False(store.Heartbeat("", active: true, T0 + 1000));
    }

    // ---- 管理工作階段走同一套 ----

    [Fact]
    public void 管理会话三道门与采集面同口径()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        AdminSession s = store.Create(Claims(), T0, 360);

        Assert.Equal(SessionGate.Heartbeat, store.GetWithGate(s.SessionId, T0 + 16 * Min).Gate);

        AdminSession s2 = store.Create(Claims(), T0, 360);
        Assert.True(store.Heartbeat(s2.SessionId, active: false, T0 + 200));
        Assert.Equal((T0 + 200, T0), Stamps(db, "admin_sessions", s2.SessionId));
        Assert.True(store.Heartbeat(s2.SessionId, active: true, T0 + 210));
        Assert.Equal((T0 + 200, T0 + 210), Stamps(db, "admin_sessions", s2.SessionId));
    }

    // ---- 心跳端點 POST /api/heartbeat ----

    private static async Task<HttpResponseMessage> PostHeartbeat(HttpClient http, string? sid, bool active)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/heartbeat")
        {
            Content = new StringContent($"{{\"active\":{(active ? "true" : "false")}}}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        if (sid is not null) req.Headers.Add("Cookie", "horus_admin=" + sid);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> GetWithCookie(HttpClient http, string path, string? sid)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (sid is not null) req.Headers.Add("Cookie", "horus_admin=" + sid);
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task 心跳端点回204无响应体()
    {
        using var app = new TestApp(adminOidc: true);
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        AdminSession s = store.Create(Claims(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, 360);

        HttpResponseMessage resp = await PostHeartbeat(app.CreateClient(), s.SessionId, active: true);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 无会话的心跳_被gate拒()
    {
        // ★ 心跳走 admin gate:要續誰的工作階段總得先知道是誰。
        //   工作階段已被某道門判掉時這裡回 401 —— 別讓一個已死的工作階段被自己的心跳救活。
        using var app = new TestApp(adminOidc: true);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await PostHeartbeat(app.CreateClient(), null, active: true)).StatusCode);
    }

    [Fact]
    public async Task 业务请求不得续idle()
    {
        // ★★★ **本次改造裡最容易踩的一條**(貝塔通 P92 翻案 P25)。
        //   看板每 5 秒輪詢一次,把輪詢算作活動的話 idle 那道門**永遠不會到點** ——
        //   監考機上開著看板就等於永不登出,而那正是 R9 那個威脅模型要擋的場景。
        //   這條鎖住:業務請求打再多次,兩個時間戳都不動。
        using var app = new TestApp(adminOidc: true);
        HttpClient http = app.CreateClient();
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        // ★ 走真端點的案例必須用**真即時間**建工作階段:gate 在 Program.cs 裡取的是 UtcNow,
        //   拿 T0(2023 年)建的工作階段會先被 absolute 判掉,測出來的就不是本條要測的東西。
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        AdminSession s = store.Create(Claims(), now, 360);

        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage r = await GetWithCookie(http, "/api/exams", s.SessionId);
            Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);   // 確實是「有效的業務請求」
        }

        Assert.Equal((now, now), Stamps(db, "admin_sessions", s.SessionId));   // ★ 一個都沒被推動
    }

    // ---- 採集端心跳:重複使用既有 WS 上行通道 ----

    private static async Task<System.Net.WebSockets.WebSocket> ConnectWithSessionAsync(
        TestApp app, string sid, byte[] kSess)
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = req =>
        {
            req.Headers["X-Horus-Session"] = sid;
            req.Headers["X-Horus-Auth"] = Horus.Contracts.Auth.Handshake(kSess, "E1", "A07", "ag-A07");
        };
        return await client.ConnectAsync(
            new Uri("ws://localhost/ingest/events?examId=E1&seatId=A07&agentId=ag-A07"), CancellationToken.None);
    }

    /// 起一場考試 + 一條採集工作階段,發一條帶指定 payload 的心跳,回兩個時間戳。
    private static async Task<(double Hb, double Seen, double Created)> AgentHeartbeatAsync(
        TestApp app, Dictionary<string, object?> payload)
    {
        HttpClient http = app.CreateClient();
        (await http.PostAsJsonAsync("/api/exams",
            new { examId = "E1", name = "T", seats = new[] { new { seatId = "A07" } } })).EnsureSuccessStatusCode();

        var store = app.Services.GetRequiredService<SessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        byte[] k = RandomNumberGenerator.GetBytes(32);
        // ★ 建工作階段的時刻往前挪 10 分鐘:否則「剛建好」就落在 150 秒節流視窗內,
        //   心跳寫不進去,案例測的就成了節流而不是這條連線路徑。
        double created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - 600;
        HorusSession s = store.Create("E1", "A07", "ag-A07", "PC", Claims(), k, created, 360);

        using System.Net.WebSockets.WebSocket ws = await ConnectWithSessionAsync(app, s.SessionId, k);
        await Ws.SendAsync(ws, Ws.SignedEventTs("E1", "A07", "ag-A07", "PC",
            Horus.Contracts.SignalType.Heartbeat, payload, 0, 1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, psk: k));
        await Ws.ReceiveAsync(ws);   // 等 ack,確保伺服器端已處理完

        (double hb, double seen) = Stamps(db, "oidc_sessions", s.SessionId);
        return (hb, seen, created);
    }

    [Fact]
    public async Task 采集端心跳带active_true_两道门都续()
    {
        // ★ Agent **重複使用既有上行通道**發心跳,不另開端點 —— 它本來就在持續上傳事件流。
        using var app = new TestApp(authMode: "both");
        (double hb, double seen, double created) =
            await AgentHeartbeatAsync(app, new() { ["status"] = "alive", ["active"] = true });

        Assert.True(hb > created);
        Assert.True(seen > created);
    }

    [Fact]
    public async Task 采集端心跳带active_false_只续心跳门()
    {
        using var app = new TestApp(authMode: "both");
        (double hb, double seen, double created) =
            await AgentHeartbeatAsync(app, new() { ["status"] = "alive", ["active"] = false });

        Assert.True(hb > created);
        Assert.Equal(created, seen);   // ★ 「人還在」沒被這一發推動
    }

    [Fact]
    public async Task 采集端心跳缺active字段_按false算()
    {
        // ★ 缺欄位說明採集端版本比伺服器舊,是部署事故。按 true 算能讓它繼續跑,
        //   但代價是 idle 那道門對那批機器**靜默失效** —— 而失效這件事沒有任何症狀。
        //   寧可讓它以「學生 30 分鐘後被踢」的形態暴露出來。
        using var app = new TestApp(authMode: "both");
        (double hb, double seen, double created) =
            await AgentHeartbeatAsync(app, new() { ["status"] = "alive" });

        Assert.True(hb > created);
        Assert.Equal(created, seen);
    }

    // ---- 設定預設值(口徑本身也要被釘住) ----

    [Fact]
    public void 三道门的默认值()
    {
        // ★ 各值**可自行調、不登記、貝塔通不攔**(其 P90),但預設值要與契約給的一致,
        //   否則「照預設部署」的那臺機器就是另一套口徑。
        var cfg = new ServerConfig();
        Assert.Equal(15, cfg.SessionHeartbeatMinutes);   // 心跳:5min × 3
        Assert.Equal(30, cfg.SessionIdleMinutes);        // idle
        Assert.Equal(360, cfg.OidcSessionMinutes);       // absolute:6h ≥ 一整場考試
        Assert.Equal(360, cfg.AdminSessionMinutes);
        Assert.Equal(150, SessionGates.ThrottleSeconds); // 伺服器端節流:5min 間隔的一半有餘
    }
}
