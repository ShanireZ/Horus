using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Horus.Agent.Buffer;
using Horus.Agent.Config;
using Horus.Agent.Transport;
using Horus.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Horus.Server.Tests;

/// LocalBuffer 純邏輯單測(入隊 / 快照 / 壓實 / 圖片增刪)。
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

            // 逐條 ack:刪 seq=2,**seq=1 與 seq=3 必須都還在**(證明不是範圍壓實,不會連坐刪掉低 seq)
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
            b.RemoveEvent(7);   // 未到壓實閾值,檔案未重寫,但快照須排除已確認的
            var snap = b.SnapshotPendingEvents();
            Assert.Equal(8, snap.Count);
            Assert.DoesNotContain(snap, x => x.seq == 3 || x.seq == 7);

            b.Compact();        // 強制壓實後仍一致
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
            // 模擬"重新啟動":新 LocalBuffer 執行個體讀到同一高水位
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
            long s1 = u1.NextSeq();     // 1(預留一個 block 寫入磁碟)
            long s2 = u1.NextSeq();     // 2

            // 模擬處理程序重新啟動:同目錄新 buffer + 新 uplink,讀回持久化的高水位
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
            Assert.Equal("img_abc123", snap[0].imageId);        // 用戶端預生成 id 往返保真
            Assert.Equal("event:browser", snap[0].trigger);
            Assert.Equal(0x9f3c1a22b0e4d7f1UL, snap[0].phash);
            Assert.Equal(webp, snap[0].webp);

            b.RemoveImage(5);
            Assert.Empty(b.SnapshotPendingImages());
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// UplinkClient 重連 / 續傳端到端(記憶體中打真實伺服器,經注入的 WS 工廠 + HttpClient)。
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

    /// 注入的 WS 連接工廠:走 TestServer(記憶體),帶合法握手頭。
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

            // 離線階段(未 RunAsync,_ws 為 null):3 條事件只落緩衝
            for (long seq = 1; seq <= 3; seq++) await uplink.SendEventAsync(Evt(seq), seq, CancellationToken.None);
            Assert.Equal(0, await EventCountAsync(http));           // 伺服器尚未收到
            Assert.Equal(3, buffer.SnapshotPendingEvents().Count);  // 全在緩衝

            // 上線:連接管理循環啟動 → 握手 → 續傳
            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));

            await WaitUntilAsync(async () => await EventCountAsync(http) == 3);   // 伺服器無損收全
            await WaitUntilAsync(() => Task.FromResult(buffer.SnapshotPendingEvents().Count == 0)); // ack 後緩衝壓實清空

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

            // WS 工廠:第 1 次連上後立即由測試關閉以模擬斷線,第 2 次正常
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

            // 等首次連上,發 2 條並確認到達
            await WaitUntilAsync(() => Task.FromResult(connectCount >= 1));
            await uplink.SendEventAsync(Evt(1), 1, CancellationToken.None);
            await uplink.SendEventAsync(Evt(2), 2, CancellationToken.None);
            await WaitUntilAsync(async () => await EventCountAsync(http) == 2);

            // 強制斷線:中止首個連接 → RunAsync 應退避重連(connectCount≥2)
            first?.Abort();
            await WaitUntilAsync(() => Task.FromResult(connectCount >= 2));

            // 重連後再發 2 條(seq 續)→ 伺服器應共 4 條、不重複
            await uplink.SendEventAsync(Evt(3), 3, CancellationToken.None);
            await uplink.SendEventAsync(Evt(4), 4, CancellationToken.None);
            await WaitUntilAsync(async () => await EventCountAsync(http) == 4);

            // 冪等:即使重連觸發了對 1/2 的重傳,伺服器仍恰好 4 條
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
            // 可切換失敗的 HttpClient(模擬圖片通道離線);內層路由到 TestServer
            var toggle = new ToggleFailHandler(app.Server.CreateHandler());
            var togHttp = new HttpClient(toggle) { BaseAddress = new Uri("http://localhost") };
            var uplink = new UplinkClient(Cfg(), buffer, http: togHttp, wsConnect: WsFactory(app));

            // 離線:圖片通道失敗 + ws 未連 → 觸發型抓圖預生成 id 並落緩衝(回傳該 id 供事件關聯)
            toggle.Fail = true;
            byte[] webp = System.Text.Encoding.ASCII.GetBytes("RIFF-evidence-offline");
            string? imgId = await uplink.UploadImageAsync(webp, "browser_non_whitelist_url", 0x1122334455667788UL, 1, CancellationToken.None);
            Assert.NotNull(imgId);
            Assert.StartsWith("img_", imgId);

            string evt = Ws.SignedEvent(Exam, Seat, Agent, "PC-A07", SignalType.BrowserUrl,
                new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false },
                80, 2, evidenceImageId: imgId);
            await uplink.SendEventAsync(evt, 2, CancellationToken.None);
            Assert.Equal(0, await EventCountAsync(http));            // 伺服器尚無

            // 上線:恢復圖片通道 + 啟動連接管理 → 續傳(先補圖後發事件)
            toggle.Fail = false;
            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));
            await WaitUntilAsync(async () => await EventCountAsync(http) == 1);

            // 事件的 evidenceImageId 指向同一 id;該圖存在且被標記為證據
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
            uplink.OnConfigUpdate = json => live.Apply(json);   // Program 裡也是這麼串接的

            Assert.True(live.IsWhitelistedHost("oj.local"));
            Assert.Equal(200, live.LargePasteThreshold);

            using var runCts = new CancellationTokenSource();
            Task run = Task.Run(() => uplink.RunAsync(runCts.Token));

            // 連接就緒後(pushedTo≥1),下發新設定
            await WaitUntilAsync(async () =>
            {
                HttpResponseMessage r = await http.PostAsJsonAsync($"/api/exams/{Exam}/config",
                    new { whitelistHosts = new[] { "judge.exam.cn" }, largePasteThreshold = 50 });
                JsonElement j = await r.Content.ReadFromJsonAsync<JsonElement>();
                return j.GetProperty("pushedTo").GetInt32() >= 1;
            });

            // Agent 收到並應用:舊白名單被替換,閾值更新
            await WaitUntilAsync(() => Task.FromResult(live.IsWhitelistedHost("judge.exam.cn") && live.LargePasteThreshold == 50));
            Assert.False(live.IsWhitelistedHost("oj.local"));

            runCts.Cancel();
            await run;
            await uplink.DisposeAsync();
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// CaptureReason → 契約 trigger 映射(C3 迴歸)。
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

/// Schema 應用穩健性:註解裡的分號不能劈裂 DDL(F5 迴歸)。
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

/// LiveConfig 熱更新純邏輯單測。
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

        Assert.True(live.IsWhitelistedHost("judge.exam.cn"));   // 新白名單生效
        Assert.False(live.IsWhitelistedHost("oj.local"));       // 舊的被整體替換
        Assert.Equal(50, live.LargePasteThreshold);
        Assert.Equal(40, live.WebpQuality);
        Assert.Equal(1080, live.TargetHeight);                  // 未出現 → 保留預設
    }

    [Fact]
    public void Apply_约束保护_质量clamp_区间纠正()
    {
        var live = new LiveConfig(Base());
        live.Apply(JsonDocument.Parse(
            "{\"webpQuality\":999,\"baselineMinSeconds\":90,\"baselineMaxSeconds\":30}").RootElement);
        Assert.Equal(100, live.WebpQuality);                    // clamp 到 1..100
        Assert.True(live.BaselineMinSeconds <= live.BaselineMaxSeconds);  // min/max 自動糾正
    }

    [Fact]
    public void Apply_只下发min_不篡改未下发的max()
    {
        var live = new LiveConfig(Base());   // min=30, max=90
        live.Apply(JsonDocument.Parse("{\"baselineMinSeconds\":120}").RootElement);  // 只下發 min
        Assert.Equal(90, live.BaselineMaxSeconds);              // max 未被篡改(舊程式碼會變成 120)
        Assert.True(live.BaselineMinSeconds <= live.BaselineMaxSeconds);
    }
}

/// fail-closed:非 loopback 綁定卻缺憑證時拒絕啟動(S2 迴歸)。
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
            Environment.SetEnvironmentVariable("HORUS_ADMIN_TOKEN", null);         // 但缺管理權杖
            Environment.SetEnvironmentVariable("HORUS_URLS", "http://0.0.0.0:5199"); // 非 loopback
            // ★隔離處理程序全域 env 洩漏:AdminOidcTests 等會置 HORUS_ADMIN_AUTH_MODE=oidc + dashboard client,
            //   若不復位,DashboardOidcEnabled=true → AdminAuthEnabled=true → fail-closed 不觸發(此測試曾偶發不拋)。
            foreach (string k in new[] { "HORUS_ADMIN_AUTH_MODE", "HORUS_OIDC_DASHBOARD_CLIENT_ID",
                "HORUS_OIDC_DASHBOARD_SECRET", "HORUS_OIDC_DASHBOARD_REDIRECT",
                "HORUS_AUTH_MODE", "HORUS_OIDC_ISSUER", "HORUS_OIDC_CLIENT_ID", "HORUS_OIDC_JWKS" })
                Environment.SetEnvironmentVariable(k, null);

            using var f = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
            Assert.ThrowsAny<Exception>(() => f.CreateClient());   // fail-closed:建置即拋
        }
        finally
        {
            Environment.SetEnvironmentVariable("HORUS_URLS", "http://127.0.0.1:0");   // 復位,勿汙染後續
            Directory.Delete(dir, true);
        }
    }
}

/// 常數時間比較(FixedTimeEquals)——含長度不洩漏(S1 迴歸)。
public class CryptoCompareTests
{
    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abcd", false)]   // 不同長度也回傳 false
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void FixedTimeEquals(string a, string b, bool expected)
        => Assert.Equal(expected, Horus.Contracts.Crypto.FixedTimeEquals(a, b));
}

/// DB 讀寫分離(檔案庫):寫連接寫入資料庫,獨立唯讀連接讀回;唯讀連接物理拒寫;並行讀寫不死鎖。
/// 既有測試全走 :memory:(退回單連接路徑),此類專測**檔案庫的雙連接路徑**。
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
            Assert.Equal(1, n);   // 獨立唯讀連接看到寫連接剛提交的資料
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
                return c.ExecuteNonQuery();   // Mode=ReadOnly → 拋,杜絕經讀連接誤寫
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

            // 寫進行中並行讀:WAL 下讀不阻塞寫、不死鎖、不拋
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

/// 視覺 adapter JSON 解析:容忍 ```json 圍欄 / 前後噪聲,健壯取欄位。
public class VisionParseTests
{
    [Fact]
    public void 容忍围栏与噪声_取全字段()
    {
        var v = Horus.Server.Analysis.Vision.OpenAiCompatibleVisionAnalyzer.Parse(
            "这是判定结果:\n```json\n{\"suspicious\":true,\"category\":\"web_ai\",\"confidence\":88," +
            "\"hits\":[\"chatgpt\"],\"evidence\":\"检测到 AI 对话界面\",\"text\":\"如何用C++实现\"}\n```");
        Assert.NotNull(v);
        Assert.True(v!.Suspicious);
        Assert.Equal("web_ai", v.Category);
        Assert.Equal("web_ai", v.Kind());
        Assert.Equal(88, v.Confidence);
        Assert.Contains("chatgpt", v.Hits);
        Assert.Equal("如何用C++实现", v.Text);
    }

    [Fact]
    public void 置信度越界钳制_缺字段回退()
    {
        var v = Horus.Server.Analysis.Vision.OpenAiCompatibleVisionAnalyzer.Parse(
            "{\"suspicious\":false,\"confidence\":999}");
        Assert.NotNull(v);
        Assert.False(v!.Suspicious);
        Assert.Equal(100, v.Confidence);        // clamp 到 0..100
        Assert.Equal("none", v.Category);        // 缺 category → none
        Assert.Empty(v.Hits);
    }

    [Fact]
    public void 无JSON_返回null()
        => Assert.Null(Horus.Server.Analysis.Vision.OpenAiCompatibleVisionAnalyzer.Parse("模型拒答,无结构化输出"));
}

/// 視覺端點 API key DPAPI 加密儲存:設定檔案不存明文,讀取時解密。DPAPI 僅 Windows。
public class SecretProtectTests
{
    [Fact]
    public void DPAPI往返_密文非明文_解得回原文()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI 僅 Windows(部署=Windows 筆記本)
        const string key = "sk-mimo-secret-key-1234567890";
        string enc = Horus.Server.Config.SecretProtect.Protect(key);
        Assert.NotEqual(key, enc);
        Assert.DoesNotContain(key, enc);            // 設定裡存的不是明文
        Assert.Equal(key, Horus.Server.Config.SecretProtect.Unprotect(enc));   // 讀取時解得回原文
    }

    [Fact]
    public void Resolve优先级_明文env先于密文_密文次之()
    {
        // 明文欄位(env 已覆蓋進 VisionApiKey)優先
        Assert.Equal("plain-key",
            Horus.Server.Config.SecretProtect.Resolve(new Horus.Server.Config.ServerConfig { VisionApiKey = "plain-key", VisionApiKeyEnc = "ignored" }));
        // 都無 → 空串(不拋)
        Assert.Equal("", Horus.Server.Config.SecretProtect.Resolve(new Horus.Server.Config.ServerConfig()));
    }

    [Fact]
    public void 明文key自动加密回写_清明文_保注释其它字段_可解密()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-cfg-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        string path = Path.Combine(dir, "server.config.json");
        try
        {
            File.WriteAllText(path,
                "{\n  // 视觉 key 注释\n  \"visionApiKey\": \"sk-mimo-plain-999\",\n  \"visionApiKeyEnc\": \"\",\n  \"riskThreshold\": 50\n}");
            string? enc = Horus.Server.Config.SecretProtect.EncryptVisionKeyInFile(path, "sk-mimo-plain-999");
            Assert.NotNull(enc);
            string after = File.ReadAllText(path);
            Assert.DoesNotContain("sk-mimo-plain-999", after);      // 明文已清
            Assert.Contains(enc!, after);                            // 密文已寫
            Assert.Contains("// 视觉 key 注释", after);              // 註解保留(行內替換,非整檔案重寫)
            Assert.Contains("\"riskThreshold\": 50", after);        // 其它欄位原樣
            Assert.Equal("sk-mimo-plain-999", Horus.Server.Config.SecretProtect.Unprotect(enc!));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// §5 送雲前派生處理:降取樣(壓 token)+ 剝離中繼資料 + 直通/退回。用真 WebP(ImageSharp 現造)驗證。
/// (owner 決策 2026-07-02:已去除打碼/裁剪,只保留降取樣。)
public class VisionImagePrepTests
{
    [Fact]
    public void 降采样_输出长边不超上限_有效webp()
    {
        // 800x400 全白 → WebP
        using var src = new Image<Rgba32>(800, 400, Color.White);
        using var ms0 = new MemoryStream();
        src.SaveAsWebp(ms0);
        byte[] webp = ms0.ToArray();

        var cfg = new Horus.Server.Config.ServerConfig { VisionMaxEdge = 200 };   // 長邊 800 → 降到 200
        byte[]? outb = Horus.Server.Analysis.Vision.VisionImagePrep.Prepare(webp, cfg);
        Assert.NotNull(outb);

        using var img = Image.Load<Rgba32>(outb!);
        Assert.True(Math.Max(img.Width, img.Height) <= 200);           // 已降取樣
        Rgba32 white = img[img.Width / 2, img.Height / 2];             // 內容保留(全白)
        Assert.True(white.R > 200 && white.G > 200 && white.B > 200, $"应为白, got {white}");
    }

    [Fact]
    public void 图已小于上限_剥离元数据后仍为有效webp()
    {
        using var src = new Image<Rgba32>(100, 80, Color.White);
        using var ms0 = new MemoryStream();
        src.SaveAsWebp(ms0);
        byte[] webp = ms0.ToArray();

        byte[]? outb = Horus.Server.Analysis.Vision.VisionImagePrep.Prepare(
            webp, new Horus.Server.Config.ServerConfig { VisionMaxEdge = 1600 });
        Assert.NotNull(outb);
        using var img = Image.Load<Rgba32>(outb!);
        Assert.Equal(100, img.Width);     // 未超上限:不縮放
        Assert.Equal(80, img.Height);
        Assert.Null(img.Metadata.ExifProfile);   // 中繼資料已剝離
    }

    [Fact]
    public void 不降采样配置_直通原字节不解码()
    {
        byte[] fake = System.Text.Encoding.ASCII.GetBytes("not-an-image-AICHAT-marker");
        byte[]? outb = Horus.Server.Analysis.Vision.VisionImagePrep.Prepare(
            fake, new Horus.Server.Config.ServerConfig { VisionMaxEdge = 0 });
        Assert.Same(fake, outb);   // 直通(未解碼,mock 標記保留)
    }

    [Fact]
    public void 解码失败_回退原字节不抛()
    {
        byte[] fake = System.Text.Encoding.ASCII.GetBytes("not-an-image");
        byte[]? outb = Horus.Server.Analysis.Vision.VisionImagePrep.Prepare(
            fake, new Horus.Server.Config.ServerConfig { VisionMaxEdge = 1600 });
        Assert.Same(fake, outb);   // 無打碼矩形需守護 → 解不開就直通原位元組
    }
}

/// 視覺回傳 JSON 的健壯解析:confidence/suspicious 容忍供應商多種表述,**絕不誤歸零致漏報**(#2)。
public class VisionVerdictParseTests
{
    private static Horus.Server.Analysis.Vision.VisionVerdict P(string json)
    {
        var v = Horus.Server.Analysis.Vision.OpenAiCompatibleVisionAnalyzer.Parse(json);
        Assert.NotNull(v);
        return v!;
    }

    [Fact]
    public void confidence_整数()
        => Assert.Equal(95, P("{\"suspicious\":true,\"category\":\"web_ai\",\"confidence\":95}").Confidence);

    [Fact]
    public void confidence_小数被截前归零_现正确取整()   // 迴歸:TryGetInt32 對 95.0 回傳 false → 曾歸零漏報
        => Assert.Equal(95, P("{\"suspicious\":true,\"confidence\":95.0}").Confidence);

    [Fact]
    public void confidence_字符串数字()
        => Assert.Equal(88, P("{\"suspicious\":true,\"confidence\":\"88\"}").Confidence);

    [Fact]
    public void confidence_0到1概率_放大到百分比()
        => Assert.Equal(90, P("{\"suspicious\":true,\"confidence\":0.9}").Confidence);

    [Fact]
    public void suspicious_字符串true_被识别为真()   // 迴歸:曾只認 JsonValueKind.True
        => Assert.True(P("{\"suspicious\":\"true\",\"confidence\":80}").Suspicious);

    [Fact]
    public void suspicious_false_保持假()
        => Assert.False(P("{\"suspicious\":false,\"confidence\":40}").Suspicious);

    [Fact]
    public void 容忍代码块围栏与前后噪声()
    {
        var v = P("这是分析结果:\n```json\n{\"suspicious\":true,\"confidence\":70}\n```\n完毕");
        Assert.Equal(70, v.Confidence);
        Assert.True(v.Suspicious);
    }
}

/// 可切換失敗的 HTTP 處理器:Fail=true 時拋異常(模擬圖片通道離線),否則轉發到內層(TestServer)。
public sealed class ToggleFailHandler : DelegatingHandler
{
    public volatile bool Fail;
    public ToggleFailHandler(HttpMessageHandler inner) : base(inner) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Fail ? throw new HttpRequestException("simulated offline") : base.SendAsync(request, ct);
}

/// #10:下發的每考試設定持久化到 exam_config,伺服器重新啟動後回填記憶體快取(白名單不丟 → server_risk 復判不退化)。
public class ConfigPersistenceTests
{
    private static string TempDbDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-cfg-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]
    public async Task 下发配置落库_新AgentHub重启回填()
    {
        string dir = TempDbDir();
        try
        {
            string dbPath = Path.Combine(dir, "t.db");
            const string cfgJson = "{\"whitelistHosts\":[\"judge.exam.cn\"],\"largePasteThreshold\":150}";
            using (var db = new Horus.Server.Data.Db(dbPath))
            {
                var hub = new Horus.Server.Ingest.AgentHub(db);
                Assert.Null(hub.GetConfig("E1"));                       // 初始無
                await hub.PushConfigAsync("E1", cfgJson, default);       // 下發 → 寫入資料庫
                Assert.Equal(cfgJson, hub.GetConfig("E1"));
            }
            // 模擬伺服器重新啟動:同一 DB 檔案 + 全新 AgentHub
            using (var db2 = new Horus.Server.Data.Db(dbPath))
            {
                var hub2 = new Horus.Server.Ingest.AgentHub(db2);
                Assert.Equal(cfgJson, hub2.GetConfig("E1"));            // 重新啟動後回填,白名單不丟
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// Schema 形狀:vec_images 虛表已從 schema 移除(不相依 sqlite-vec 原生擴充)、核心表齊全、心跳 PK 含 seat 維。
public class SchemaShapeTests
{
    [Fact]
    public void 跳过vec0虚表_核心表齐全()
    {
        using var db = new Horus.Server.Data.Db(":memory:");
        var tables = db.Read(conn =>
        {
            var set = new HashSet<string>();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = c.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        });
        Assert.DoesNotContain("vec_images", tables);   // vec_images 虛表已從 schema.sql 移除,不相依 sqlite-vec
        foreach (string t in new[] { "exams", "seats", "events", "images", "ocr_results", "logo_hits",
            "keystroke_samples", "suspicious_queue", "agent_heartbeats", "exam_config" })
            Assert.Contains(t, tables);
    }

    [Fact]
    public void 心跳PK含seat_同agent同ts不同seat两行并存()
    {
        using var db = new Horus.Server.Data.Db(":memory:");
        db.Write(conn =>
        {
            foreach (string seat in new[] { "A01", "A02" })
            {
                using var c = conn.CreateCommand();
                c.CommandText = $"INSERT INTO agent_heartbeats (agent_id,exam_id,seat_id,ts,status) VALUES ('ag','E1','{seat}',100.0,'alive')";
                c.ExecuteNonQuery();
            }
        });
        long n = db.Read(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM agent_heartbeats";
            return Convert.ToInt64(c.ExecuteScalar());
        });
        Assert.Equal(2, n);   // 舊 PK(agent_id,ts) 會 UPSERT 覆蓋成 1 行;新 PK 含 seat → 兩行並存,線上判定不錯位
    }
}
