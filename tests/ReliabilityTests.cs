using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Horus.Agent.Buffer;
using Horus.Agent.Config;
using Horus.Agent.Transport;
using Horus.Contracts;
using Xunit;

namespace Horus.Server.Tests;

/// LocalBuffer 纯逻辑单测(入队 / 快照 / 压实 / 图片增删)。
public class LocalBufferTests
{
    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-buf-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]
    public async Task 事件_入队_快照升序_逐条精确删除不误删()
    {
        string dir = TempDir();
        try
        {
            var b = new LocalBuffer(dir);
            await b.EnqueueEventAsync(3, "{\"a\":3}");
            await b.EnqueueEventAsync(1, "{\"a\":1}");
            await b.EnqueueEventAsync(2, "{\"a\":2}");

            var snap = b.SnapshotPendingEvents();
            Assert.Equal(3, snap.Count);
            Assert.Equal(1, snap[0].seq);           // 升序
            Assert.Equal("{\"a\":2}", snap[1].json);

            // 逐条 ack:删 seq=2,**seq=1 与 seq=3 必须都还在**(证明不是范围压实,不会连坐删掉低 seq)
            b.RemoveEvent(2);
            var snap2 = b.SnapshotPendingEvents();
            Assert.Equal(2, snap2.Count);
            Assert.Equal(1, snap2[0].seq);
            Assert.Equal(3, snap2[1].seq);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 事件_逐条ack攒批压实_快照排除已确认()
    {
        string dir = TempDir();
        try
        {
            var b = new LocalBuffer(dir);
            for (long seq = 1; seq <= 10; seq++) await b.EnqueueEventAsync(seq, "{\"s\":" + seq + "}");

            b.RemoveEvent(3);
            b.RemoveEvent(7);   // 未到压实阈值,文件未重写,但快照须排除已确认的
            var snap = b.SnapshotPendingEvents();
            Assert.Equal(8, snap.Count);
            Assert.DoesNotContain(snap, x => x.seq == 3 || x.seq == 7);

            b.Compact();        // 强制压实后仍一致
            var snap2 = b.SnapshotPendingEvents();
            Assert.Equal(8, snap2.Count);
            Assert.DoesNotContain(snap2, x => x.seq == 3 || x.seq == 7);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 序号高水位持久化_重启后不复用()
    {
        string dir = TempDir();
        try
        {
            var b = new LocalBuffer(dir);
            b.SaveSeqCeiling(500);
            Assert.Equal(500, b.LoadSeqCeiling());
            // 模拟"重启":新 LocalBuffer 实例读到同一高水位
            var b2 = new LocalBuffer(dir);
            Assert.Equal(500, b2.LoadSeqCeiling());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 重启后NextSeq不复用旧seq()
    {
        string dir = TempDir();
        try
        {
            var cfg = new AgentConfig
            {
                ExamId = "E", SeatId = "S", AgentId = "A", MachineId = "M",
                ServerWsBase = "ws://x", ServerHttpBase = "http://x", Psk = TestApp.Psk,
            };
            Func<Uri, CancellationToken, Task<WebSocket>> noConnect = (_, __) => throw new InvalidOperationException("test: no connect");

            var u1 = new UplinkClient(cfg, new LocalBuffer(dir), http: new HttpClient(), wsConnect: noConnect);
            long s1 = u1.NextSeq();     // 1(预留一个 block 落盘)
            long s2 = u1.NextSeq();     // 2

            // 模拟进程重启:同目录新 buffer + 新 uplink,读回持久化的高水位
            var u2 = new UplinkClient(cfg, new LocalBuffer(dir), http: new HttpClient(), wsConnect: noConnect);
            long s3 = u2.NextSeq();

            Assert.True(s3 > s2, $"重启复用了旧 seq: s3={s3} s2={s2}");
            Assert.True(s3 > 256, $"应越过已持久化的预留块: s3={s3}");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 图片_入队_快照_删除()
    {
        string dir = TempDir();
        try
        {
            var b = new LocalBuffer(dir);
            byte[] webp = { 1, 2, 3, 4, 5 };
            await b.EnqueueImageAsync(5, "img_abc123", "event:browser", 0x9f3c1a22b0e4d7f1UL, webp);

            var snap = b.SnapshotPendingImages();
            Assert.Single(snap);
            Assert.Equal(5, snap[0].seq);
            Assert.Equal("img_abc123", snap[0].imageId);        // 客户端预生成 id 往返保真
            Assert.Equal("event:browser", snap[0].trigger);
            Assert.Equal(0x9f3c1a22b0e4d7f1UL, snap[0].phash);
            Assert.Equal(webp, snap[0].webp);

            b.RemoveImage(5);
            Assert.Empty(b.SnapshotPendingImages());
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// UplinkClient 重连 / 续传端到端(内存中打真实服务器,经注入的 WS 工厂 + HttpClient)。
public class ReconnectTests
{
    private const string Exam = "E1", Seat = "A07", Agent = "ag-A07";

    private static async Task CreateExamAsync(HttpClient http)
    {
        var r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId = Exam, name = "重连测试",
            seats = new[] { new { seatId = Seat, agentId = Agent, machineId = "PC-A07", displayName = "学员", studentId = "s01" } },
        });
        r.EnsureSuccessStatusCode();
    }

    private static async Task<int> EventCountAsync(HttpClient http)
    {
        JsonElement e = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events?seatId={Seat}");
        return e.GetArrayLength();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> cond, int timeoutMs = 8000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            if (await cond()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("条件未在超时内满足");
    }

    private static AgentConfig Cfg() => new()
    {
        ExamId = Exam, SeatId = Seat, AgentId = Agent, MachineId = "PC-A07",
        ServerWsBase = "ws://localhost", ServerHttpBase = "http://localhost",
        Psk = TestApp.Psk,
    };

    /// 注入的 WS 连接工厂:走 TestServer(内存),带合法握手头。
    private static Func<Uri, CancellationToken, Task<WebSocket>> WsFactory(TestApp app)
        => async (uri, ct) =>
        {
            var client = app.Server.CreateWebSocketClient();
            string auth = Auth.Handshake(TestApp.Psk, Exam, Seat, Agent);
            client.ConfigureRequest = req => req.Headers["X-Horus-Auth"] = auth;
            return await client.ConnectAsync(uri, ct);
        };

    private static string Evt(long seq) => Ws.SignedEvent(Exam, Seat, Agent, "PC-A07", SignalType.BrowserUrl,
        new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, seq);

    [Fact]
    public async Task 离线缓冲_上线后续传_服务器无损收全()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-rc-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        try
        {
            var buffer = new LocalBuffer(dir);
            var uplink = new UplinkClient(Cfg(), buffer, http: app.CreateClient(), wsConnect: WsFactory(app));

            // 离线阶段(未 RunAsync,_ws 为 null):3 条事件只落缓冲
            for (long seq = 1; seq <= 3; seq++) await uplink.SendEventAsync(Evt(seq), seq, CancellationToken.None);
            Assert.Equal(0, await EventCountAsync(http));           // 服务器尚未收到
            Assert.Equal(3, buffer.SnapshotPendingEvents().Count);  // 全在缓冲

            // 上线:连接管理循环启动 → 握手 → 续传
            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));

            await WaitUntilAsync(async () => await EventCountAsync(http) == 3);   // 服务器无损收全
            await WaitUntilAsync(() => Task.FromResult(buffer.SnapshotPendingEvents().Count == 0)); // ack 后缓冲压实清空

            runCts.Cancel();
            await run;
            await uplink.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 断线重连_续传后不重复_幂等()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-rc-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        try
        {
            var buffer = new LocalBuffer(dir);

            // WS 工厂:第 1 次连上后立即由测试关闭以模拟断线,第 2 次正常
            int connectCount = 0;
            WebSocket? first = null;
            Func<Uri, CancellationToken, Task<WebSocket>> factory = async (uri, ct) =>
            {
                var client = app.Server.CreateWebSocketClient();
                string auth = Auth.Handshake(TestApp.Psk, Exam, Seat, Agent);
                client.ConfigureRequest = req => req.Headers["X-Horus-Auth"] = auth;
                WebSocket ws = await client.ConnectAsync(uri, ct);
                if (Interlocked.Increment(ref connectCount) == 1) first = ws;
                return ws;
            };

            var uplink = new UplinkClient(Cfg(), buffer, http: app.CreateClient(), wsConnect: factory);
            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));

            // 等首次连上,发 2 条并确认到达
            await WaitUntilAsync(() => Task.FromResult(connectCount >= 1));
            await uplink.SendEventAsync(Evt(1), 1, CancellationToken.None);
            await uplink.SendEventAsync(Evt(2), 2, CancellationToken.None);
            await WaitUntilAsync(async () => await EventCountAsync(http) == 2);

            // 强制断线:中止首个连接 → RunAsync 应退避重连(connectCount≥2)
            first?.Abort();
            await WaitUntilAsync(() => Task.FromResult(connectCount >= 2));

            // 重连后再发 2 条(seq 续)→ 服务器应共 4 条、不重复
            await uplink.SendEventAsync(Evt(3), 3, CancellationToken.None);
            await uplink.SendEventAsync(Evt(4), 4, CancellationToken.None);
            await WaitUntilAsync(async () => await EventCountAsync(http) == 4);

            // 幂等:即使重连触发了对 1/2 的重传,服务器仍恰好 4 条
            Assert.Equal(4, await EventCountAsync(http));

            runCts.Cancel();
            await run;
            await uplink.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 离线抓图_断线重连_事件与证据图关联不断()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-rc-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        try
        {
            var buffer = new LocalBuffer(dir);
            // 可切换失败的 HttpClient(模拟图片通道离线);内层路由到 TestServer
            var toggle = new ToggleFailHandler(app.Server.CreateHandler());
            var togHttp = new HttpClient(toggle) { BaseAddress = new Uri("http://localhost") };
            var uplink = new UplinkClient(Cfg(), buffer, http: togHttp, wsConnect: WsFactory(app));

            // 离线:图片通道失败 + ws 未连 → 触发型抓图预生成 id 并落缓冲(返回该 id 供事件关联)
            toggle.Fail = true;
            byte[] webp = System.Text.Encoding.ASCII.GetBytes("RIFF-evidence-offline");
            string? imgId = await uplink.UploadImageAsync(webp, "browser_non_whitelist_url", 0x1122334455667788UL, 1, CancellationToken.None);
            Assert.NotNull(imgId);
            Assert.StartsWith("img_", imgId);

            string evt = Ws.SignedEvent(Exam, Seat, Agent, "PC-A07", SignalType.BrowserUrl,
                new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false },
                80, 2, evidenceImageId: imgId);
            await uplink.SendEventAsync(evt, 2, CancellationToken.None);
            Assert.Equal(0, await EventCountAsync(http));            // 服务器尚无

            // 上线:恢复图片通道 + 启动连接管理 → 续传(先补图后发事件)
            toggle.Fail = false;
            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));
            await WaitUntilAsync(async () => await EventCountAsync(http) == 1);

            // 事件的 evidenceImageId 指向同一 id;该图存在且被标记为证据
            JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events?seatId={Seat}");
            Assert.Equal(imgId, events[0].GetProperty("evidenceImageId").GetString());
            JsonElement meta = await http.GetFromJsonAsync<JsonElement>($"/api/images/{imgId}/meta");
            Assert.True(meta.GetProperty("isEvidence").GetBoolean());

            runCts.Cancel();
            await run;
            await uplink.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 下发config_update_在线Agent即时应用()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-rc-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        try
        {
            var buffer = new LocalBuffer(dir);
            var live = new LiveConfig(new AgentConfig
            {
                ExamId = Exam, SeatId = Seat, AgentId = Agent, MachineId = "PC-A07",
                ServerWsBase = "ws://localhost", ServerHttpBase = "http://localhost", Psk = TestApp.Psk,
                WhitelistHosts = new() { "oj.local" }, WhitelistProcs = new(), LargePasteThreshold = 200,
            });
            var uplink = new UplinkClient(Cfg(), buffer, http: app.CreateClient(), wsConnect: WsFactory(app));
            uplink.OnConfigUpdate = json => live.Apply(json);   // Program 里也是这么接线的

            Assert.True(live.IsWhitelistedHost("oj.local"));
            Assert.Equal(200, live.LargePasteThreshold);

            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));

            // 连接就绪后(pushedTo≥1),下发新配置
            await WaitUntilAsync(async () =>
            {
                HttpResponseMessage r = await http.PostAsJsonAsync($"/api/exams/{Exam}/config",
                    new { whitelistHosts = new[] { "judge.exam.cn" }, largePasteThreshold = 50 });
                JsonElement j = await r.Content.ReadFromJsonAsync<JsonElement>();
                return j.GetProperty("pushedTo").GetInt32() >= 1;
            });

            // Agent 收到并应用:旧白名单被替换,阈值更新
            await WaitUntilAsync(() => Task.FromResult(live.IsWhitelistedHost("judge.exam.cn") && live.LargePasteThreshold == 50));
            Assert.False(live.IsWhitelistedHost("oj.local"));

            runCts.Cancel();
            await run;
            await uplink.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// CaptureReason → 契约 trigger 映射(C3 回归)。
public class TriggerMapTests
{
    [Theory]
    [InlineData("baseline_random", "baseline_random")]
    [InlineData("browser_non_whitelist_url", "event:browser")]
    [InlineData("browser_url_unreadable", "event:browser")]
    [InlineData("non_whitelist_process", "event:process")]
    [InlineData("large_paste", "event:paste")]
    [InlineData("usb_insert", "event:usb")]
    [InlineData("capture_now", "event:manual")]
    public void ToContract_映射到契约取值(string reason, string expected)
        => Assert.Equal(expected, Horus.Agent.Transport.TriggerMap.ToContract(reason));
}

/// Schema 应用健壮性:注释里的分号不能劈裂 DDL(F5 回归)。
public class SchemaTests
{
    [Fact]
    public void 建表_注释含分号不劈裂_关键表齐全()
    {
        using var db = new Horus.Server.Data.Db(":memory:");
        foreach (string table in new[] { "exams", "seats", "events", "images", "ocr_results", "logo_hits", "keystroke_samples", "suspicious_queue", "agent_heartbeats" })
        {
            bool exists = db.Locked(conn =>
            {
                using var c = conn.CreateCommand();
                c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
                c.Parameters.AddWithValue("$n", table);
                return c.ExecuteScalar() is not null;
            });
            Assert.True(exists, $"表 {table} 未建立(可能被注释里的分号劈裂)");
        }
    }
}

/// LiveConfig 热更新纯逻辑单测。
public class LiveConfigTests
{
    private static AgentConfig Base() => new()
    {
        ExamId = "E", SeatId = "S", AgentId = "A", MachineId = "M",
        ServerWsBase = "ws://x", ServerHttpBase = "http://x", Psk = TestApp.Psk,
        WhitelistHosts = new() { "oj.local" }, WhitelistProcs = new() { "code" },
        LargePasteThreshold = 200, TargetHeight = 1080, WebpQuality = 75,
        BaselineMinSeconds = 30, BaselineMaxSeconds = 90,
    };

    [Fact]
    public void Apply_只更新出现的字段_缺省保留()
    {
        var live = new LiveConfig(Base());
        Assert.True(live.IsWhitelistedHost("oj.local"));
        Assert.False(live.IsWhitelistedHost("chat.openai.com"));

        live.Apply(JsonDocument.Parse(
            "{\"whitelistHosts\":[\"judge.exam.cn\"],\"largePasteThreshold\":50,\"webpQuality\":40}").RootElement);

        Assert.True(live.IsWhitelistedHost("judge.exam.cn"));   // 新白名单生效
        Assert.False(live.IsWhitelistedHost("oj.local"));       // 旧的被整体替换
        Assert.Equal(50, live.LargePasteThreshold);
        Assert.Equal(40, live.WebpQuality);
        Assert.Equal(1080, live.TargetHeight);                  // 未出现 → 保留缺省
    }

    [Fact]
    public void Apply_约束保护_质量clamp_区间纠正()
    {
        var live = new LiveConfig(Base());
        live.Apply(JsonDocument.Parse(
            "{\"webpQuality\":999,\"baselineMinSeconds\":90,\"baselineMaxSeconds\":30}").RootElement);
        Assert.Equal(100, live.WebpQuality);                    // clamp 到 1..100
        Assert.True(live.BaselineMinSeconds <= live.BaselineMaxSeconds);  // min/max 自动纠正
    }

    [Fact]
    public void Apply_只下发min_不篡改未下发的max()
    {
        var live = new LiveConfig(Base());   // min=30, max=90
        live.Apply(JsonDocument.Parse("{\"baselineMinSeconds\":120}").RootElement);  // 只下发 min
        Assert.Equal(90, live.BaselineMaxSeconds);              // max 未被篡改(旧代码会变成 120)
        Assert.True(live.BaselineMinSeconds <= live.BaselineMaxSeconds);
    }
}

/// fail-closed:非 loopback 绑定却缺凭证时拒绝启动(S2 回归)。
public class FailClosedTests
{
    [Fact]
    public void 非loopback绑定缺管理令牌_拒绝启动()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-fc-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        try
        {
            Environment.SetEnvironmentVariable("HORUS_DBPATH", ":memory:");
            Environment.SetEnvironmentVariable("HORUS_DATADIR", dir);
            Environment.SetEnvironmentVariable("HORUS_PSK_B64", TestApp.PskB64);   // 有 PSK
            Environment.SetEnvironmentVariable("HORUS_ADMIN_TOKEN", null);         // 但缺管理令牌
            Environment.SetEnvironmentVariable("HORUS_URLS", "http://0.0.0.0:5199"); // 非 loopback

            using var f = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
            Assert.ThrowsAny<Exception>(() => f.CreateClient());   // fail-closed:构建即抛
        }
        finally
        {
            Environment.SetEnvironmentVariable("HORUS_URLS", "http://127.0.0.1:0");   // 复位,勿污染后续
            Directory.Delete(dir, true);
        }
    }
}

/// 常量时间比较(FixedTimeEquals)——含长度不泄漏(S1 回归)。
public class CryptoCompareTests
{
    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abcd", false)]   // 不同长度也返回 false
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void FixedTimeEquals(string a, string b, bool expected)
        => Assert.Equal(expected, Horus.Contracts.Crypto.FixedTimeEquals(a, b));
}

/// DB 读写分离(文件库):写连接落库,独立只读连接读回;只读连接物理拒写;并发读写不死锁。
/// 既有测试全走 :memory:(回退单连接路径),此类专测**文件库的双连接路径**。
public class DbReadWriteTests
{
    private static string TempDbDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-db-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]
    public void 文件库_写连接落库_只读连接读回同一份数据()
    {
        string dir = TempDbDir();
        try
        {
            using var db = new Horus.Server.Data.Db(Path.Combine(dir, "t.db"));
            db.Write(conn =>
            {
                using var c = conn.CreateCommand();
                c.CommandText = "INSERT INTO exams (exam_id,name,status,created_at) VALUES ('E1','考试','active',1)";
                c.ExecuteNonQuery();
            });
            long n = db.Read(conn =>
            {
                using var c = conn.CreateCommand();
                c.CommandText = "SELECT COUNT(*) FROM exams WHERE exam_id='E1'";
                return Convert.ToInt64(c.ExecuteScalar());
            });
            Assert.Equal(1, n);   // 独立只读连接看到写连接刚提交的数据
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 文件库_只读连接物理拒写()
    {
        string dir = TempDbDir();
        try
        {
            using var db = new Horus.Server.Data.Db(Path.Combine(dir, "t.db"));
            Assert.ThrowsAny<Exception>(() => db.Read(conn =>
            {
                using var c = conn.CreateCommand();
                c.CommandText = "INSERT INTO exams (exam_id,name,status,created_at) VALUES ('X','x','active',1)";
                return c.ExecuteNonQuery();   // Mode=ReadOnly → 抛,杜绝经读连接误写
            }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task 文件库_并发读写_不死锁不报错_计数一致()
    {
        string dir = TempDbDir();
        try
        {
            using var db = new Horus.Server.Data.Db(Path.Combine(dir, "t.db"));
            db.Write(conn =>
            {
                using var c = conn.CreateCommand();
                c.CommandText = "INSERT INTO exams (exam_id,name,status,created_at) VALUES ('E1','考试','active',1)";
                c.ExecuteNonQuery();
            });

            const int N = 200;
            Task writer = Task.Run(() =>
            {
                for (int i = 1; i <= N; i++)
                {
                    long seq = i;
                    db.Write(conn =>
                    {
                        using var c = conn.CreateCommand();
                        c.CommandText =
                            @"INSERT INTO events (exam_id,seat_id,agent_id,seq,ts,recv_ts,type,payload,risk)
                              VALUES ('E1','A07','ag',$seq,1,1,'heartbeat','{}',0)";
                        c.Parameters.AddWithValue("$seq", seq);
                        c.ExecuteNonQuery();
                    });
                }
            });

            // 写进行中并发读:WAL 下读不阻塞写、不死锁、不抛
            for (int r = 0; r < N; r++)
                db.Read(conn =>
                {
                    using var c = conn.CreateCommand();
                    c.CommandText = "SELECT COUNT(*) FROM events";
                    return Convert.ToInt64(c.ExecuteScalar());
                });

            await writer;
            long total = db.Read(conn =>
            {
                using var c = conn.CreateCommand();
                c.CommandText = "SELECT COUNT(*) FROM events";
                return Convert.ToInt64(c.ExecuteScalar());
            });
            Assert.Equal(N, total);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// 可切换失败的 HTTP 处理器:Fail=true 时抛异常(模拟图片通道离线),否则转发到内层(TestServer)。
public sealed class ToggleFailHandler : DelegatingHandler
{
    public volatile bool Fail;
    public ToggleFailHandler(HttpMessageHandler inner) : base(inner) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Fail ? throw new HttpRequestException("simulated offline") : base.SendAsync(request, ct);
}
