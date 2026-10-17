using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text.Json;
using Horus.Agent.Hardening;
using Horus.Contracts;
using Xunit;

namespace Horus.Server.Tests;

/// M5 採集端硬化:平台無關檢測核(遮蔽分類 / 掛起監測 / 能力健康 / 異常重新啟動)純邏輯單測。
public class ScreenQualityTests
{
    [Fact]
    public void 正常截图_不判遮蔽()
        => Assert.Equal(ObscureReason.None, ScreenQuality.Classify(new ScreenStats(1920, 1080, 520.0)));

    [Fact]
    public void 纯色_判solid()
        => Assert.Equal(ObscureReason.Solid, ScreenQuality.Classify(new ScreenStats(1920, 1080, 0.0)));

    [Fact]
    public void 坏尺寸_判bad_size()
        => Assert.Equal(ObscureReason.BadSize, ScreenQuality.Classify(new ScreenStats(0, 0, 100.0)));

    [Fact]
    public void 过小_判too_small()
        => Assert.Equal(ObscureReason.TooSmall, ScreenQuality.Classify(new ScreenStats(100, 100, 500.0)));

    [Fact]
    public void 低熵_判low_entropy()
        => Assert.Equal(ObscureReason.LowEntropy, ScreenQuality.Classify(new ScreenStats(1920, 1080, 5.0)));

    [Fact]
    public void 深色IDE有内容_不误报()   // 深色但有文字 → 方差遠高於閾值
        => Assert.False(ScreenQuality.IsObscured(new ScreenStats(1920, 1080, 120.0)));

    [Fact]
    public void 标签映射()
    {
        Assert.Equal("solid", ScreenQuality.ReasonLabel(ObscureReason.Solid));
        Assert.Equal("too_small", ScreenQuality.ReasonLabel(ObscureReason.TooSmall));
    }
}

public class SuspendMonitorTests
{
    [Fact]
    public void 首次观测_只定基线_不报()
    {
        var m = new SuspendMonitor(30);
        Assert.Null(m.Observe(1000.0));
    }

    [Fact]
    public void 正常间隔_不报()
    {
        var m = new SuspendMonitor(30);
        m.Observe(1000.0);
        Assert.Null(m.Observe(1030.0));   // gap 30 < 90 閾值
        Assert.Null(m.Observe(1061.0));   // gap 31
    }

    [Fact]
    public void 大跳变_报挂起_返回gap毫秒()
    {
        var m = new SuspendMonitor(30, 3.0);   // 閾值 90s
        m.Observe(1000.0);
        double? gap = m.Observe(1400.0);        // gap 400s ≫ 90s
        Assert.NotNull(gap);
        Assert.Equal(400_000.0, gap!.Value, 1);
    }
}

public class CapabilityTrackerTests
{
    [Fact]
    public void 连续失败达阈值_才跨入降级_只报一次()
    {
        var t = new CapabilityTracker(3);
        Assert.False(t.RecordFailure("uia"));   // 1
        Assert.False(t.RecordFailure("uia"));   // 2
        Assert.True(t.RecordFailure("uia"));    // 3 → 剛降級
        Assert.False(t.RecordFailure("uia"));   // 4 → 已降級,不再報
        Assert.True(t.IsDegraded("uia"));
    }

    [Fact]
    public void 成功清零_从降级恢复报一次()
    {
        var t = new CapabilityTracker(2);
        t.RecordFailure("wmi"); t.RecordFailure("wmi");   // 降級
        Assert.True(t.IsDegraded("wmi"));
        Assert.True(t.RecordSuccess("wmi"));               // 恢復
        Assert.False(t.IsDegraded("wmi"));
        Assert.False(t.RecordSuccess("wmi"));              // 本就正常 → 不報
    }

    [Fact]
    public void 单次失败不降级()
    {
        var t = new CapabilityTracker(3);
        Assert.False(t.RecordFailure("etw"));
        Assert.False(t.IsDegraded("etw"));
    }
}

public class RestartClassifierTests
{
    [Fact]
    public void 旧标记running_判异常重启()
        => Assert.True(RestartClassifier.IsUnexpectedRestart(RestartClassifier.Running));

    [Fact]
    public void 旧标记clean_正常()
        => Assert.False(RestartClassifier.IsUnexpectedRestart(RestartClassifier.Clean));

    [Fact]
    public void 无标记_首次部署_不算异常()
        => Assert.False(RestartClassifier.IsUnexpectedRestart(null));
}

/// M5 伺服器端:健康信號處置(遮屏獨立賦分入隊 + 座位健康告警計數)。
public class HealthSignalServerTests
{
    private static async Task CreateExam(HttpClient http)
        => (await http.PostAsJsonAsync("/api/exams", new { examId = "E1", name = "T", seats = new[] { new { seatId = "A07" } } }))
            .EnsureSuccessStatusCode();

    private static async Task<WebSocket> HelloAsync(TestApp app)
    {
        WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);   // hello_ack
        return ws;
    }

    [Fact]
    public async Task 遮屏事件_agent自报risk0_服务器独立判入队()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        using WebSocket ws = await HelloAsync(app);

        // Agent 把遮屏事件籤成 risk=0 試圖壓住,伺服器獨立賦分 60 → 入隊。
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.ScreenshotObscured,
            new() { ["reason"] = "solid", ["metric"] = 0.4 }, 0, seq: 1));
        Assert.Equal("ack", (await Ws.ReceiveAsync(ws)).GetProperty("type").GetString());

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(60, events[0].GetProperty("serverRisk").GetInt32());
        // 健康信號獨立成「採集健康」面板:入隊(source='health')→進 /health,不再進可裁決的 /suspicious。
        JsonElement health = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/health");
        Assert.Equal(1, health.GetArrayLength());
        Assert.Equal("screen_obscured", health[0].GetProperty("kind").GetString());
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(0, susp.GetArrayLength());
    }

    [Fact]
    public async Task 座位健康告警_累计各类健康信号()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        using WebSocket ws = await HelloAsync(app);

        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.WatchdogRestart,
            new() { ["reason"] = "killed" }, 0, seq: 1));
        await Ws.ReceiveAsync(ws);
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.SuspectedSuspend,
            new() { ["gapMs"] = 300000.0 }, 0, seq: 2));
        await Ws.ReceiveAsync(ws);

        JsonElement seats = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.Equal(2, seats[0].GetProperty("healthAlerts").GetInt32());
    }
}
