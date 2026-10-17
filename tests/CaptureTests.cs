using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Horus.Contracts;
using Xunit;

namespace Horus.Server.Tests;

/// B4:capture_now 端到端(伺服器端 push → Agent 收到 capture_now 影格)。
public class CaptureTests
{
    private static async Task CreateExam(HttpClient http, string examId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/exams")
        { Content = JsonContent.Create(new { examId, name = "T", seats = new[] { new { seatId = "A07" } } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task 点名抓图_在线Agent_pushed_true_且收到capture_now帧()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);   // ack

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/agents/ag-A07/capture")
        { Content = JsonContent.Create(new { reason = "proctor_call" }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        HttpResponseMessage resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement j = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.True(j.GetProperty("pushed").GetBoolean());

        JsonElement frame = await Ws.ReceiveAsync(ws);
        Assert.Equal("capture_now", frame.GetProperty("type").GetString());
    }

    [Fact]
    public async Task 点名抓图_Agent不在线_pushed_false()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        // 未連接任何 Agent
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/agents/ag-UNKNOWN/capture")
        { Content = JsonContent.Create(new { reason = "proctor_call" }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        HttpResponseMessage resp = await http.SendAsync(req);
        JsonElement j = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(j.GetProperty("pushed").GetBoolean());
    }
}
