using System.Net;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Ingest;
using Xunit;

namespace Horus.Server.Tests;

/// M4 部署项:考前预检 /api/preflight + /api/exams 白名单标记(§10.1 缺口告警)。
public class PreflightTests
{
    private static async Task<JsonElement> AdminGet(HttpClient http, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        HttpResponseMessage resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task CreateExam(HttpClient http, string examId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/exams")
        { Content = JsonContent.Create(new { examId, name = "T", seats = new[] { new { seatId = "A01" } } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static async Task PushWhitelist(HttpClient http, string examId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/exams/{examId}/config")
        { Content = JsonContent.Create(new { whitelistHosts = new[] { "judge.exam.cn" } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static string? WhitelistCheckLevel(JsonElement preflight)
    {
        foreach (JsonElement c in preflight.GetProperty("checks").EnumerateArray())
            if (c.GetProperty("id").GetString() == "whitelist") return c.GetProperty("level").GetString();
        return null;
    }

    [Fact]
    public async Task 预检_基本可用_含checks与activeExams()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        JsonElement pf = await AdminGet(http, "/api/preflight");
        Assert.True(pf.GetProperty("ok").GetBoolean());                 // token 模式无 fail
        Assert.True(pf.GetProperty("checks").GetArrayLength() > 0);
        Assert.Equal(0, pf.GetProperty("activeExams").GetArrayLength()); // 尚无考试
    }

    [Fact]
    public async Task active考试无白名单_预检告警_下发后转ok()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");

        JsonElement pf1 = await AdminGet(http, "/api/preflight");
        Assert.Equal("warn", WhitelistCheckLevel(pf1));                 // 无白名单 → warn
        JsonElement ae = pf1.GetProperty("activeExams")[0];
        Assert.Equal("E1", ae.GetProperty("examId").GetString());
        Assert.False(ae.GetProperty("hasWhitelist").GetBoolean());

        await PushWhitelist(http, "E1");
        JsonElement pf2 = await AdminGet(http, "/api/preflight");
        Assert.Equal("ok", WhitelistCheckLevel(pf2));                   // 下发后 → ok
    }

    [Fact]
    public async Task exams列表_带hasWhitelist标记()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E9");

        JsonElement before = await AdminGet(http, "/api/exams");
        Assert.False(before[0].GetProperty("hasWhitelist").GetBoolean());

        await PushWhitelist(http, "E9");
        JsonElement after = await AdminGet(http, "/api/exams");
        Assert.True(after[0].GetProperty("hasWhitelist").GetBoolean());
    }

    [Fact]
    public async Task authmode_带采集面模式()
    {
        using var app = new TestApp(adminAuth: true);   // 采集面默认 psk
        JsonElement am = JsonSerializer.Deserialize<JsonElement>(
            await app.CreateClient().GetStringAsync("/api/authmode"));
        Assert.Equal("psk", am.GetProperty("collectAuthMode").GetString());
    }

    [Fact]
    public async Task 预检_psk模式_迁移状态ok且提示残留()
    {
        using var app = new TestApp(adminAuth: true);
        JsonElement pf = await AdminGet(app.CreateClient(), "/api/preflight");
        string? level = null, detail = null;
        foreach (JsonElement c in pf.GetProperty("checks").EnumerateArray())
            if (c.GetProperty("id").GetString() == "migration") { level = c.GetProperty("level").GetString(); detail = c.GetProperty("detail").GetString(); }
        Assert.Equal("ok", level);
        Assert.Contains("psk", detail);
    }

    [Fact]
    public async Task 座位authMode_PSK在线_与_闲置offline()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/exams")
        { Content = JsonContent.Create(new { examId = "E1", name = "T", seats = new[] { new { seatId = "A07" }, new { seatId = "A08" } } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();

        // A07 走 PSK 发一条事件 → 在线;A08 闲置 → offline
        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}"); await Ws.ReceiveAsync(ws);
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        await Ws.SendAsync(ws, Ws.SignedEventTs("E1", "A07", "ag-A07", "PC", SignalType.WindowFocus, new() { ["title"] = "t" }, 0, 1, now));
        await Ws.ReceiveAsync(ws);

        JsonElement seats = await AdminGet(http, "/api/exams/E1/seats");
        var by = seats.EnumerateArray().ToDictionary(s => s.GetProperty("seatId").GetString()!);
        Assert.Equal("psk", by["A07"].GetProperty("authMode").GetString());
        Assert.Equal("offline", by["A08"].GetProperty("authMode").GetString());
    }
}

/// M2 基线抽样策略:确定性 1/N 抽样(跨重启一致)。
public class BaselineSamplingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 抽样率不大于1_全命中(int rate)
    {
        for (int i = 0; i < 50; i++)
            Assert.True(ImageIngest.BaselineSampleHit("img_" + i.ToString("x"), rate));
    }

    [Fact]
    public void 确定性_同id同结果()
    {
        for (int i = 0; i < 100; i++)
        {
            string id = "img_" + Guid.NewGuid().ToString("N");
            Assert.Equal(ImageIngest.BaselineSampleHit(id, 7), ImageIngest.BaselineSampleHit(id, 7));
        }
    }

    [Fact]
    public void 分布_约1_N命中()
    {
        const int rate = 5, total = 5000;
        int hit = 0;
        for (int i = 0; i < total; i++)
            if (ImageIngest.BaselineSampleHit("img_" + i.ToString("x8"), rate)) hit++;
        double frac = (double)hit / total;
        // 期望 ~0.2;放宽到 [0.15, 0.25] 容忍 FNV 在有限样本上的偏差。
        Assert.InRange(frac, 0.15, 0.25);
    }
}
