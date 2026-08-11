using System.Security.Cryptography;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// **三道门 + 存活心跳**的回归(贝塔通 P88–P92,契约见其 `docs/rp-contract.md`)。
///
/// ★★ 这一整套的失效形态都是**静默的**:
///   - 少一道门 → 会话该失效时没失效,**没有任何症状**,只在事后追查「他为什么还在里面」时才发现;
///   - `active` 恒为 false → 每个人考到 30 分钟被踢,看起来像网络问题;
///   - 节流判据没按字段分开 → 只有多标签的人偶发被踢。
///   没有测试的规则就是没人执行的规则,所以每一道门、每一条反向约束都在这里各有一条。
public class SessionGateTests
{
    private const double T0 = 1_700_000_000;
    private static readonly double Min = 60.0;

    private static OidcClaims Claims(string sub = "sub-1") => new(sub, "姓名", "u1");

    /// 直接读两个时间戳列 —— 判据只有 SQL 一处(见 SessionGates 末尾的说明),
    /// 所以回归也只能打真列,不能打某个 C# 副本。
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

        // ★ 要单独验 absolute,就得先把另两道门喂饱 —— 否则 idle(30min)会先拦下,
        //   用例看起来过了但测的根本不是 absolute。
        for (double t = T0 + 180; t < T0 + 61 * Min; t += 180) store.Heartbeat(s.SessionId, active: true, t);

        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 59 * Min).Gate);
        Assert.Equal(SessionGate.Absolute, store.GetWithGate(s.SessionId, T0 + 61 * Min).Gate);
    }

    [Fact]
    public void absolute不被活动推迟()
    {
        // ★★ 这是三道门里唯一「任何活动都推不动」的一道。把它做成滑动窗口,
        //   等于把「最后一道防线」变成「只要有人在用就永不过期」——
        //   公用机房那个威胁模型下,那正是要挡的场景。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store, absoluteMinutes: 60);

        // 一路 active:true 心跳打到 absolute 前一刻(节流 150 秒,所以每 3 分钟一发都能写进去)
        for (double t = T0 + 180; t < T0 + 60 * Min; t += 180) store.Heartbeat(s.SessionId, active: true, t);
        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 59.9 * Min).Gate);

        Assert.Equal(SessionGate.Absolute, store.GetWithGate(s.SessionId, T0 + 60 * Min + 1).Gate);
    }

    // ---- 心跳门 ----

    [Fact]
    public void 无心跳15分钟_被心跳门拒()
    {
        // 挡的是「关页面 / 关浏览器走人」。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 14 * Min).Gate);
        Assert.Equal(SessionGate.Heartbeat, store.GetWithGate(s.SessionId, T0 + 16 * Min).Gate);
    }

    // ---- idle 门 ----

    [Fact]
    public void activefalse的心跳续心跳门但不续idle()
    {
        // ★★ 整套设计的核心分工:**任何**心跳续心跳门;**只有 active:true** 才额外续 idle。
        //   合并成一个判据的后果是「页面开着就永不过期」,idle 那道门直接失效。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        // 页面开着但人走了:心跳照发,active 恒 false
        for (double t = T0 + 180; t <= T0 + 45 * Min; t += 180) store.Heartbeat(s.SessionId, active: false, t);

        // 心跳门被续住了 —— 20 分钟时若只有心跳门,它是过的
        Assert.Equal(SessionGate.Ok, store.GetWithGate(s.SessionId, T0 + 20 * Min).Gate);
        // 但 idle 那道门到点了(30 分钟)
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

    // ---- 判定顺序 ----

    [Fact]
    public void 判定顺序_更强的终止理由排前面()
    {
        // revoked → absolute → idle → heartbeat。三道同时到点时报最靠前的那一道,
        // 这样给用户的话才是对的(「本次登录已达最长时限」而不是「你太久没动了」)。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store, absoluteMinutes: 60);

        // 三道全到点
        Assert.Equal(SessionGate.Absolute, store.GetWithGate(s.SessionId, T0 + 120 * Min).Gate);

        // idle 与心跳同时到点(absolute 还没到)→ 报 idle
        HorusSession s2 = NewCollect(store);
        Assert.Equal(SessionGate.Idle, store.GetWithGate(s2.SessionId, T0 + 40 * Min).Gate);
    }

    [Fact]
    public void 撤权即删行_是判定顺序里最前的一道()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        HorusSession s = NewCollect(store);

        store.RevokeBySub("sub-1");

        (HorusSession? session, SessionGate gate) = store.GetWithGate(s.SessionId, T0);
        Assert.Null(session);
        Assert.Equal(SessionGate.Ok, gate);   // 查无此会话:没有哪一道门拦下它,它压根不在了
    }

    // ---- 节流(150 秒)与「判据按字段分开」 ----

    [Fact]
    public void 节流窗口内的心跳不写()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        HorusSession s = NewCollect(store);

        Assert.False(store.Heartbeat(s.SessionId, active: true, T0 + 10));    // 距建会话才 10 秒
        Assert.Equal((T0, T0), Stamps(db, "oidc_sessions", s.SessionId));

        Assert.True(store.Heartbeat(s.SessionId, active: true, T0 + 151));    // 过了 150 秒窗口
        Assert.Equal((T0 + 151, T0 + 151), Stamps(db, "oidc_sessions", s.SessionId));
    }

    [Fact]
    public void 节流判据按字段分开_activefalse不吞掉随后的activetrue()
    {
        // ★★ 本次改造里最容易做错的一处。只看「心跳够不够新」的话:
        //   标签 A 的 active:false 刚写完,标签 B 随后的 active:true 就被整体节流掉,
        //   **「人还在」这个信号随之丢失** —— 表现只是「多开标签的人偶发被 idle 踢掉」。
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        HorusSession s = NewCollect(store);

        // 标签 A:人不在
        Assert.True(store.Heartbeat(s.SessionId, active: false, T0 + 200));
        Assert.Equal((T0 + 200, T0), Stamps(db, "oidc_sessions", s.SessionId));   // 只动心跳,不动 idle

        // 标签 B:10 秒后报「人还在」—— 心跳那道门还在节流窗口内,但 idle 那个时间戳已经够旧了
        Assert.True(store.Heartbeat(s.SessionId, active: true, T0 + 210));
        (double hb, double seen) = Stamps(db, "oidc_sessions", s.SessionId);
        Assert.Equal(T0 + 200, hb);      // ★ 心跳仍被节流住(没被这一发顺手推走)
        Assert.Equal(T0 + 210, seen);    // ★★ 而「人还在」被记下了 —— 没被吞掉
    }

    [Fact]
    public void 心跳打不存在的会话_不报错且返回false()
    {
        using var app = new TestApp();
        var store = app.Services.GetRequiredService<SessionStore>();
        Assert.False(store.Heartbeat("sess_nope", active: true, T0 + 1000));
        Assert.False(store.Heartbeat("", active: true, T0 + 1000));
    }

    // ---- 管理会话走同一套 ----

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

    // ---- 心跳端点 POST /api/heartbeat ----

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
        // ★ 心跳走 admin gate:要续谁的会话总得先知道是谁。
        //   会话已被某道门判掉时这里回 401 —— 别让一个已死的会话被自己的心跳救活。
        using var app = new TestApp(adminOidc: true);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await PostHeartbeat(app.CreateClient(), null, active: true)).StatusCode);
    }

    [Fact]
    public async Task 业务请求不得续idle()
    {
        // ★★★ **本次改造里最容易踩的一条**(贝塔通 P92 翻案 P25)。
        //   看板每 5 秒轮询一次,把轮询算作活动的话 idle 那道门**永远不会到点** ——
        //   监考机上开着看板就等于永不登出,而那正是 R9 那个威胁模型要挡的场景。
        //   这条锁住:业务请求打再多次,两个时间戳都不动。
        using var app = new TestApp(adminOidc: true);
        HttpClient http = app.CreateClient();
        var store = app.Services.GetRequiredService<AdminSessionStore>();
        var db = app.Services.GetRequiredService<Db>();
        // ★ 走真端点的用例必须用**真实时间**建会话:gate 在 Program.cs 里取的是 UtcNow,
        //   拿 T0(2023 年)建的会话会先被 absolute 判掉,测出来的就不是本条要测的东西。
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        AdminSession s = store.Create(Claims(), now, 360);

        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage r = await GetWithCookie(http, "/api/exams", s.SessionId);
            Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);   // 确实是「有效的业务请求」
        }

        Assert.Equal((now, now), Stamps(db, "admin_sessions", s.SessionId));   // ★ 一个都没被推动
    }

    // ---- 配置默认值(口径本身也要被钉住) ----

    [Fact]
    public void 三道门的默认值()
    {
        // ★ 各值**可自行调、不登记、贝塔通不拦**(其 P90),但默认值要与契约给的一致,
        //   否则「照默认部署」的那台机器就是另一套口径。
        var cfg = new ServerConfig();
        Assert.Equal(15, cfg.SessionHeartbeatMinutes);   // 心跳:5min × 3
        Assert.Equal(30, cfg.SessionIdleMinutes);        // idle
        Assert.Equal(360, cfg.OidcSessionMinutes);       // absolute:6h ≥ 一整场考试
        Assert.Equal(360, cfg.AdminSessionMinutes);
        Assert.Equal(150, SessionGates.ThrottleSeconds); // 服务端节流:5min 间隔的一半有余
    }
}
