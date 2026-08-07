using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Data;
using Horus.Server.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// 考试派发 + 常态待命 + 全场远程登出(owner 决策 2026-07-03)的端到端锁定:
/// examId 服务端派发(/oidc/active-exam 轮询 + exchange 指派)、seatId 由 OIDC 身份派生、
/// 考试结束/全场登出以下行帧推给在线 Agent、吊销后的会话不可续用。
public class ExamDispatchTests
{
    private static async Task CreateExamAsync(HttpClient http, string examId = "E1")
        => (await http.PostAsJsonAsync("/api/exams", new { examId, name = "T-" + examId })).EnsureSuccessStatusCode();

    /// 直接建一条 OIDC 会话(绕过真 token 交换),返回 (sessionId, kSess)。
    private static (string sid, byte[] k) MakeSession(TestApp app, string exam, string seat, string agent)
    {
        var store = app.Services.GetRequiredService<SessionStore>();
        byte[] k = RandomNumberGenerator.GetBytes(32);
        var claims = new OidcClaims("sub-" + agent, "姓名", "user_" + agent);
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        HorusSession s = store.Create(exam, seat, agent, "PC", claims, k, now, 180);
        return (s.SessionId, k);
    }

    private static async Task<WebSocket> ConnectWithSessionAsync(TestApp app, string exam, string seat, string agent, string sessionId, byte[] kSess)
    {
        WebSocketClient client = app.Server.CreateWebSocketClient();
        client.ConfigureRequest = req =>
        {
            req.Headers["X-Horus-Session"] = sessionId;
            req.Headers["X-Horus-Auth"] = Auth.Handshake(kSess, exam, seat, agent);
        };
        var uri = new Uri($"ws://localhost/ingest/events?examId={exam}&seatId={seat}&agentId={agent}");
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    // ================= /oidc/active-exam(待命轮询) =================

    [Fact]
    public async Task active_exam端点_无考试false_建考试返回id_结束后false()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();

        JsonElement r0 = await http.GetFromJsonAsync<JsonElement>("/oidc/active-exam");
        Assert.False(r0.GetProperty("active").GetBoolean());

        await CreateExamAsync(http, "E1");
        JsonElement r1 = await http.GetFromJsonAsync<JsonElement>("/oidc/active-exam");
        Assert.True(r1.GetProperty("active").GetBoolean());
        Assert.Equal("E1", r1.GetProperty("examId").GetString());
        Assert.Equal("T-E1", r1.GetProperty("name").GetString());

        (await http.PostAsync("/api/exams/E1/end", null)).EnsureSuccessStatusCode();
        JsonElement r2 = await http.GetFromJsonAsync<JsonElement>("/oidc/active-exam");
        Assert.False(r2.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task 结束已归档考试_被状态守卫拒_不复活伪装ok()
    {
        // COR-1:end 端点须有状态守卫,绝不把 archived/archiving 复活成 ended
        // (否则绕过 /integrity 的 applicable:false 保护 → 对已清空的 live 行返回空审计伪装 ok:true)。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http, "E1");

        // 直接把考试置为已归档态(模拟归档作业已跑完)。
        var db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using var c = conn.Cmd("UPDATE exams SET status='archived' WHERE exam_id='E1'"); c.ExecuteNonQuery(); });

        // 对已归档考试 POST /end → 被拒(404),不得复活成 ended。
        var resp = await http.PostAsync("/api/exams/E1/end", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);

        string status = db.Read(conn => { using var c = conn.Cmd("SELECT status FROM exams WHERE exam_id='E1'"); return (string)c.ExecuteScalar()!; });
        Assert.Equal("archived", status);   // 状态仍为 archived,未被复活
    }

    [Fact]
    public async Task 白名单下发后GET回填_返回已存配置()
    {
        // 看板白名单编辑器:POST 下发 → GET 读回持久化配置以回填抽屉。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http, "E1");

        (await http.PostAsJsonAsync("/api/exams/E1/config",
            new { whitelistHosts = new[] { "luogu.com.cn" }, whitelistProcs = new[] { "code", "g++" } })).EnsureSuccessStatusCode();

        JsonElement r = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/config");
        Assert.Equal("E1", r.GetProperty("examId").GetString());
        JsonElement cfg = r.GetProperty("config");
        Assert.Equal(JsonValueKind.Object, cfg.ValueKind);
        var hosts = cfg.GetProperty("whitelistHosts").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("luogu.com.cn", hosts);
        var procs = cfg.GetProperty("whitelistProcs").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("g++", procs);
    }

    [Fact]
    public async Task 未下发考试GET配置_返回config为null()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http, "E2");
        JsonElement r = await http.GetFromJsonAsync<JsonElement>("/api/exams/E2/config");
        Assert.Equal(JsonValueKind.Null, r.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task 多场active_派发最近创建的一场()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http, "EA");
        await Task.Delay(5);   // created_at 秒级小数,拉开一点;同刻回退 exam_id DESC 也选 EB
        await CreateExamAsync(http, "EB");

        JsonElement r = await http.GetFromJsonAsync<JsonElement>("/oidc/active-exam");
        Assert.Equal("EB", r.GetProperty("examId").GetString());
    }

    // ================= seat 派生规则 =================

    [Fact]
    public void SeatFrom_username为权威_空或不安全回退sub()
    {
        static OidcClaims C(string username, string sub = "sub-1") => new(sub, "姓名", username);

        Assert.Equal("ye_feng", ExamDispatch.SeatFrom(C("ye_feng")));
        Assert.Equal("sub-1", ExamDispatch.SeatFrom(C("")));                  // 空 username → sub
        Assert.Equal("sub-1", ExamDispatch.SeatFrom(C("a/b")));               // 路径危险字符 → sub
        Assert.Equal("sub-1", ExamDispatch.SeatFrom(C("x..y")));              // ".." → sub
        Assert.Equal("sub-1", ExamDispatch.SeatFrom(C(new string('a', 129)))); // 超长 → sub
    }

    // ================= 结束考试 → 在线推送 / 离线补发 =================

    [Fact]
    public async Task 结束考试_在线Agent收到exam_ended推送()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);   // hello_ack(考试 active,无补发帧)

        JsonElement resp = await (await http.PostAsync("/api/exams/E1/end", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, resp.GetProperty("notified").GetInt32());

        JsonElement frame = await Ws.ReceiveAsync(ws);
        Assert.Equal("exam_ended", frame.GetProperty("type").GetString());
        Assert.Equal("E1", frame.GetProperty("examId").GetString());
    }

    [Fact]
    public async Task hello_考试已结束_补发exam_ended()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        (await http.PostAsync("/api/exams/E1/end", null)).EnsureSuccessStatusCode();

        // Agent 离线错过推送 → 重连 hello 时补发
        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("hello_ack", ack.GetProperty("type").GetString());
        JsonElement frame = await Ws.ReceiveAsync(ws);
        Assert.Equal("exam_ended", frame.GetProperty("type").GetString());
    }

    [Fact]
    public async Task hello_考试active_不补发exam_ended_事件照常ack()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);   // hello_ack

        // 紧接着发事件,下一帧必须是 ack(而不是误发的 exam_ended)
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC", SignalType.WindowFocus,
            new() { ["title"] = "t" }, 0, 1));
        JsonElement frame = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", frame.GetProperty("type").GetString());
    }

    // ================= 全场远程登出 =================

    [Fact]
    public async Task 全场登出_吊销会话_在线收session_revoked_重连401()
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        (string sid, byte[] k) = MakeSession(app, "E1", "user_a", "ag-01");

        using WebSocket ws = await ConnectWithSessionAsync(app, "E1", "user_a", "ag-01", sid, k);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);   // hello_ack

        JsonElement resp = await (await http.PostAsync("/api/exams/E1/logout", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, resp.GetProperty("revoked").GetInt32());
        Assert.Equal(1, resp.GetProperty("notified").GetInt32());

        JsonElement frame = await Ws.ReceiveAsync(ws);
        Assert.Equal("session_revoked", frame.GetProperty("type").GetString());

        // 会话已吊销:DB 查无 + 重连(带旧 session)握手 401
        var store = app.Services.GetRequiredService<SessionStore>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Assert.Null(store.Get(sid, now));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectWithSessionAsync(app, "E1", "user_a", "ag-01", sid, k));
    }

    [Fact]
    public async Task 全场登出_只吊销本场_他场会话不受影响()
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http, "E1");
        await CreateExamAsync(http, "E2");
        (string sid1, _) = MakeSession(app, "E1", "user_a", "ag-01");
        (string sid2, _) = MakeSession(app, "E2", "user_b", "ag-02");

        (await http.PostAsync("/api/exams/E1/logout", null)).EnsureSuccessStatusCode();

        var store = app.Services.GetRequiredService<SessionStore>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Assert.Null(store.Get(sid1, now));
        Assert.NotNull(store.Get(sid2, now));
    }

    // ================= 会话探针 /oidc/session =================

    [Fact]
    public async Task 会话探针_有效返回考试状态_吊销后invalid_无头invalid()
    {
        using var app = new TestApp(authMode: "both");
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        (string sid, _) = MakeSession(app, "E1", "user_a", "ag-01");

        async Task<JsonElement> Probe(string? sessionId)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/oidc/session");
            if (sessionId is not null) req.Headers.Add("X-Horus-Session", sessionId);
            using HttpResponseMessage resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<JsonElement>();
        }

        JsonElement ok = await Probe(sid);
        Assert.True(ok.GetProperty("valid").GetBoolean());
        Assert.Equal("E1", ok.GetProperty("examId").GetString());
        Assert.Equal("user_a", ok.GetProperty("seatId").GetString());
        Assert.Equal("active", ok.GetProperty("examStatus").GetString());

        (await http.PostAsync("/api/exams/E1/end", null)).EnsureSuccessStatusCode();
        JsonElement afterEnd = await Probe(sid);
        Assert.True(afterEnd.GetProperty("valid").GetBoolean());
        Assert.Equal("ended", afterEnd.GetProperty("examStatus").GetString());   // Agent 据此排空后停采

        (await http.PostAsync("/api/exams/E1/logout", null)).EnsureSuccessStatusCode();
        Assert.False((await Probe(sid)).GetProperty("valid").GetBoolean());      // 吊销后失效

        Assert.False((await Probe(null)).GetProperty("valid").GetBoolean());     // 无凭证探不到任何信息
        Assert.False((await Probe("sess_bogus")).GetProperty("valid").GetBoolean());
    }
}
