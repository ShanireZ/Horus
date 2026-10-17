using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Agent.Buffer;
using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Jobs;
using Horus.Server.Ingest;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Horus.Server.Tests;

// 2026-07-02 審查後修復的迴歸鎖定測試。編號對應審查報告的 13 項。

/// #2 事件緩衝原子性 / #12 孤兒圖清理。
public class FixLocalBufferTests
{
    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-fix-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]   // #2:崩潰/掉電撕裂的半截行,讀側整行丟棄且不卡續傳佇列;換行修復使後續事件不被併入損壞。
    public async Task 崩溃撕裂坏行_快照丢弃_下一条不被并入损坏()
    {
        string dir = TempDir();
        try
        {
            var b = new LocalBuffer(dir);
            await b.EnqueueEventAsync(1, "{\"a\":1}");
            // 模擬崩潰:直接往 pending 追加一條**無換行結尾的半截行**(撕裂)
            File.AppendAllText(Path.Combine(dir, "events.pending.jsonl"), "2\t{\"a\":");
            // 重新啟動 + 再入隊:換行修復應使 seq=3 獨佔一行,不與壞行併成一行
            var b2 = new LocalBuffer(dir);
            await b2.EnqueueEventAsync(3, "{\"a\":3}");

            var snap = b2.SnapshotPendingEvents();
            Assert.Equal(2, snap.Count);                              // 壞的 seq=2 被丟棄(不續傳垃圾卡佇列)
            Assert.Contains(snap, x => x.seq == 1);
            Assert.Equal("{\"a\":3}", snap.Single(x => x.seq == 3).json);   // seq=3 完整,未被壞行併入損壞
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]   // #12:建構時清理孤兒圖片檔案(無 meta 的 webp / 殘留 meta.tmp),有效配對保留。
    public void 孤儿图片文件_构造时清理_有效配对保留()
    {
        string dir = TempDir();
        try
        {
            string img = Path.Combine(dir, "images");
            Directory.CreateDirectory(img);
            File.WriteAllBytes(Path.Combine(img, "9.webp"), new byte[] { 1, 2, 3 });     // 孤兒:無 meta
            File.WriteAllText(Path.Combine(img, "8.meta.tmp"), "junk");                   // 未完成的原子 meta 寫
            File.WriteAllBytes(Path.Combine(img, "5.webp"), new byte[] { 1 });           // 有效配對
            File.WriteAllText(Path.Combine(img, "5.meta"), "img_x\tevent:browser\t0000000000000000");

            _ = new LocalBuffer(dir);   // 建構即清理

            Assert.False(File.Exists(Path.Combine(img, "9.webp")));
            Assert.False(File.Exists(Path.Combine(img, "8.meta.tmp")));
            Assert.True(File.Exists(Path.Combine(img, "5.webp")));    // 有效配對不誤刪
            Assert.True(File.Exists(Path.Combine(img, "5.meta")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// #4 送雲前派生:聯網分析器必剝中繼資料 / 解碼失敗 fail-safe。
public class FixVisionPrepStripTests
{
    private static byte[] Webp(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h, Color.White);
        using var ms = new MemoryStream();
        img.SaveAsWebp(ms);
        return ms.ToArray();
    }

    [Fact]   // maxEdge<=0 時聯網分析器仍解碼+剝中繼資料(不直通),只是不縮放。
    public void 联网分析器_maxEdge0_仍剥元数据不直通()
    {
        byte[] webp = Webp(50, 40);
        byte[]? outb = VisionImagePrep.Prepare(webp, new ServerConfig { VisionMaxEdge = 0 }, mustStrip: true);
        Assert.NotNull(outb);
        Assert.NotSame(webp, outb);               // 未直通:已重編碼剝中繼資料
        using var img = Image.Load<Rgba32>(outb!);
        Assert.Null(img.Metadata.ExifProfile);
        Assert.Equal(50, img.Width);              // maxEdge<=0 → 不縮放
    }

    [Fact]   // 聯網分析器解碼失敗 → 回傳 null(fail-safe),絕不把未剝中繼資料的原位元組送雲。
    public void 联网分析器_解码失败_返回null()
        => Assert.Null(VisionImagePrep.Prepare(
            Encoding.ASCII.GetBytes("not-an-image"), new ServerConfig { VisionMaxEdge = 1600 }, mustStrip: true));

    [Fact]   // 本機/mock(mustStrip=false)maxEdge<=0 保持直通原位元組(測試假圖位元組可達分析器)。
    public void 本地分析器_maxEdge0_直通原字节()
    {
        byte[] fake = Encoding.ASCII.GetBytes("AICHAT-mark");
        Assert.Same(fake, VisionImagePrep.Prepare(fake, new ServerConfig { VisionMaxEdge = 0 }, mustStrip: false));
    }
}

/// #5 歸檔 archive_events 保留 server_risk,risk 存原始 agent 自報值(錨點可復算)。
public class FixArchiveServerRiskTests
{
    private const double Day = 86400.0;
    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-fix-sr-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    [Fact]
    public void 归档保留server_risk_risk存原始值()
    {
        string dir = TempDir();
        try
        {
            using var db = new Db(Path.Combine(dir, "live.db"));
            var storage = new Storage(dir);
            db.Write(conn =>
            {
                using (SqliteCommand c = conn.Cmd("INSERT INTO exams (exam_id,name,status,started_at,ended_at,created_at) VALUES ('E1','x','ended',1000,1000,1000)")) c.ExecuteNonQuery();
                using (SqliteCommand c = conn.Cmd("INSERT INTO seats (exam_id,seat_id,agent_id) VALUES ('E1','A01','ag')")) c.ExecuteNonQuery();
                // 學員把"訪問 AI 站"籤成 risk=0,伺服器復判 server_risk=80 → 有效風險 max=80≥閾值 = 關鍵。
                using (SqliteCommand c = conn.Cmd(
                    @"INSERT INTO events (exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,server_risk,hash_self,sig)
                      VALUES ('E1','A01','ag','PC',1,1000,1000,'browser_url','{""url"":""x""}',0,80,'h','s')")) c.ExecuteNonQuery();
            });

            var cfg = new ServerConfig { RetentionDays = 30, ArchiveCriticalRisk = 50, ArchiveDbPath = "arch.db", ArchiveEnabled = false };
            var svc = new ArchiveService(db, storage, cfg, NullLogger<ArchiveService>.Instance);
            Assert.Equal(1, svc.RunOnce(1000 + 40 * Day).Archived);

            using var arc = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "arch.db"), Pooling = false }.ToString());
            arc.Open();
            using SqliteCommand c2 = arc.CreateCommand();
            c2.CommandText = "SELECT risk, server_risk FROM archive_events WHERE exam_id='E1'";
            using SqliteDataReader r = c2.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(0, r.GetInt32(0));    // risk 存原始 agent 自報(canonicalCore 籤的正是它,錨點可復算)
            Assert.Equal(80, r.GetInt32(1));   // server_risk 旁註留證:歸檔庫仍能查證"為何判高危"
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

/// #1 / #7 / #9 / #13b / #13c 伺服器端集成迴歸。
public class FixServerApiTests
{
    private static async Task CreateExam(HttpClient http, string examId = "E1", string seat = "A07")
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId, name = "x",
            seats = new[] { new { seatId = seat, agentId = "ag-A07", machineId = "PC-A07" } },
        });
        r.EnsureSuccessStatusCode();
    }

    [Fact]   // #7:免鑑權 login 收到畸形 JSON 應 400(而非未捕獲異常 500)。
    public async Task 登录_畸形JSON_返回400()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        HttpResponseMessage resp = await http.PostAsync("/api/login", new StringContent("garbage", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]   // #7:建考試端點收到畸形 JSON 應 400。
    public async Task 建考试_畸形JSON_返回400()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        HttpResponseMessage resp = await http.PostAsync("/api/exams", new StringContent("{not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]   // #13b:examId 含路徑危險字元 → 400;中文 examId 合法放行。
    public async Task 建考试_examId危险字符400_中文examId放行()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        HttpResponseMessage bad = await http.PostAsJsonAsync("/api/exams", new { examId = "a/b", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        HttpResponseMessage ok = await http.PostAsJsonAsync("/api/exams", new { examId = "期末-2026", name = "期末" });
        ok.EnsureSuccessStatusCode();
    }

    [Fact]   // #13c:歸檔進行中(archiving)考試的 integrity 端點回傳 applicable:false,不對半清理資料跑稽核。
    public async Task 归档中考试_integrity返回applicable_false()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using SqliteCommand c = conn.Cmd("UPDATE exams SET status='archiving' WHERE exam_id='E1'"); c.ExecuteNonQuery(); });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/integrity");
        Assert.Equal("archiving", rep.GetProperty("status").GetString());
        Assert.False(rep.GetProperty("applicable").GetBoolean());
    }

    [Fact]   // #1:圖片上傳到已封存(archived)考試被拒、不寫入資料庫(late-ingest 隔離)。
    public async Task 图片上传到已封存考试_拒绝不落库()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using SqliteCommand c = conn.Cmd("UPDATE exams SET status='archived' WHERE exam_id='E1'"); c.ExecuteNonQuery(); });

        const string cid = "img_sealed00001", phash = "aabbccddeeff0011", ts = "1750000000.1";
        byte[] webp = Encoding.ASCII.GetBytes("RIFF-webp-sealed");
        string canon = Auth.ImageCanonicalHeaders("E1", "A07", "ag-A07", 1, "event:browser", phash, ts, cid);
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images") { Content = new ByteArrayContent(webp) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Horus-Exam", "E1");
        req.Headers.Add("X-Horus-Seat", "A07");
        req.Headers.Add("X-Horus-Agent", "ag-A07");
        req.Headers.Add("X-Horus-Seq", "1");
        req.Headers.Add("X-Horus-Trigger", "event:browser");
        req.Headers.Add("X-Horus-Phash", phash);
        req.Headers.Add("X-Horus-Ts", ts);
        req.Headers.Add("X-Horus-Sig", sig);
        req.Headers.Add("X-Horus-Image-Id", cid);
        HttpResponseMessage resp = await http.SendAsync(req);

        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("stored").GetBoolean());
        long n = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT COUNT(*) FROM images WHERE image_id=@id", ("@id", cid)); return Convert.ToInt64(c.ExecuteScalar()); });
        Assert.Equal(0, n);   // 未寫入資料庫
    }

    [Fact]   // #9:同一 (exam,seat,agent) 多條心跳後,agent_heartbeats 只保留最新一行(裁剪,防無界追加)。
    public async Task 心跳裁剪_只保留最新一行()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExam(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        double t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        for (int i = 0; i < 3; i++)
        {
            string hb = Ws.SignedEventTs("E1", "A07", "ag-A07", "PC-A07", SignalType.Heartbeat,
                new() { ["status"] = "alive" }, 0, i + 1, t0 + i);
            await Ws.SendAsync(ws, hb);
            await Ws.ReceiveAsync(ws);
        }

        Db db = app.Services.GetRequiredService<Db>();
        long n = db.Read(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT COUNT(*) FROM agent_heartbeats WHERE exam_id='E1' AND seat_id='A07' AND agent_id='ag-A07'");
            return Convert.ToInt64(c.ExecuteScalar());
        });
        Assert.Equal(1, n);   // 裁剪:只留最新 ts 一行
    }
}

// ===== 2026-07-02 第三輪審查修復的迴歸鎖定 =====

/// F5:URL 黑名單改按 DNS 標籤匹配,杜絕子串誤傷(richardbard≠bard·foryou.com≠you.com·googleusercontent≠google)。
public class FixHostMatchTests
{
    [Theory]
    // 真命中(應判 AI/搜索):整段標籤 / 域後綴
    [InlineData("chat.openai.com", true)]
    [InlineData("openai.com", true)]
    [InlineData("poe.com", true)]
    [InlineData("you.com", true)]
    [InlineData("www.you.com", true)]
    // 假陽性(舊子串匹配會誤命中,新標籤匹配不命中)
    [InlineData("richardbard.com", false)]   // 含 "bard" 子串但非整段標籤
    [InlineData("foryou.com", false)]        // 含 "you.com" 子串但非域後綴
    [InlineData("myclaude-notes.local", false)]
    [InlineData("openai-mirror.cn", false)]  // 連字元鏡像:標籤匹配會漏(交白名單+人工備援,換低假陽性)
    public void AI站按标签匹配_不再子串误伤(string host, bool expected)
        => Assert.Equal(expected, Horus.Server.Analysis.RiskModel.HostMatchesAny(host, Horus.Server.Analysis.RiskModel.AiHosts));

    [Theory]
    [InlineData("www.google.com", true)]
    [InlineData("google.com", true)]
    [InlineData("googleusercontent.com", false)]   // 含 "google" 子串但 "googleusercontent" 非整段 → 不誤判搜索
    [InlineData("baidu.com", true)]
    public void 搜索站按标签匹配(string host, bool expected)
        => Assert.Equal(expected, Horus.Server.Analysis.RiskModel.HostMatchesAny(host, Horus.Server.Analysis.RiskModel.SearchHosts));

    [Fact]   // F11:空 host(about:blank/data:/解析失敗)在配了白名單時不再被誤判高危(回傳 0)。
    public void 空host不误判高危()
    {
        using JsonDocument p = JsonDocument.Parse("{\"url\":\"about:blank\"}");
        var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "judge.exam.cn" };
        int risk = Horus.Server.Analysis.RiskModel.Derive(SignalType.BrowserUrl, p.RootElement, wl, null);
        Assert.Equal(0, risk);
    }

    [Fact]   // F5 端到端:chat.openai.com 仍判 80(未因收嚴匹配漏掉真命中)。
    public void 真AI站仍判高危()
    {
        using JsonDocument p = JsonDocument.Parse("{\"url\":\"https://chat.openai.com/c/abc\"}");
        int risk = Horus.Server.Analysis.RiskModel.Derive(SignalType.BrowserUrl, p.RootElement, null, null);
        Assert.Equal(80, risk);
    }
}

/// F9:confidence 整數 1(=1%)不再被 0-1 概率啟發式誤放大成 100;嚴格 (0,1) 才放大。
/// BUG#2 修復:1.0(小數,=100% 歸一化概率)放大成 100,避免 confidence:1 且 suspicious:true 因 1<閾值而漏報。
/// 整數 1 與 1.0 解析後同為 double 1.0,故用原始 token 是否含小數點區分(見 ParseConfidence)。
public class FixConfidenceBoundaryTests
{
    private static int Conf(string json) => OpenAiCompatibleVisionAnalyzer.Parse(json)!.Confidence;

    [Fact] public void 整数1保持1_不放大()   => Assert.Equal(1, Conf("{\"suspicious\":true,\"confidence\":1}"));
    [Fact] public void 小数09仍放大到90()     => Assert.Equal(90, Conf("{\"suspicious\":true,\"confidence\":0.9}"));
    [Fact] public void 整数95不变()           => Assert.Equal(95, Conf("{\"suspicious\":true,\"confidence\":95}"));
    // BUG#2:模型以 1.0(歸一化概率)回傳滿分 → 放大成 100,進可疑佇列(原實作保持 1 → 1<閾值漏報)。
    [Fact] public void 小数1dot0放大到100()   => Assert.Equal(100, Conf("{\"suspicious\":true,\"confidence\":1.0}"));
}

/// BUG#1 / 品質#5:ingest body 共用讀取 + 顯式大小上限。Content-Length 超限在分配 MemoryStream 前即拒;
/// 實測長度備援;空 body 按 allowEmpty 決定拒/收。ImageIngest 與 KeystrokeIngest 共用此 helper。
public class IngestBodyTests
{
    private static DefaultHttpContext Ctx(long? contentLength, byte[] body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentLength = contentLength;
        ctx.Request.Body = new MemoryStream(body);
        return ctx;
    }

    [Fact]
    public async Task 声明超限_前置拒绝不读body()
    {
        var (body, status, err) = await IngestBody.ReadWithLimitAsync(Ctx(1000, []).Request, 100);
        Assert.Null(body);
        Assert.Equal(413, status);
        Assert.Equal("too_large", err);
    }

    [Fact]
    public async Task 实测超限_兜底拒绝()
    {
        var (body, status, err) = await IngestBody.ReadWithLimitAsync(Ctx(null, new byte[200]).Request, 100);
        Assert.Null(body);
        Assert.Equal(413, status);
        Assert.Equal("too_large", err);
    }

    [Fact]
    public async Task 正常body_返回字节()
    {
        var data = Encoding.UTF8.GetBytes("hello-ingest");
        var (body, status, err) = await IngestBody.ReadWithLimitAsync(Ctx(data.Length, data).Request, 1024);
        Assert.NotNull(body);
        Assert.Equal(data, body);
        Assert.Null(status);
        Assert.Null(err);
    }

    [Fact]
    public async Task 空body_allowEmptyFalse_拒()
    {
        var (body, status, err) = await IngestBody.ReadWithLimitAsync(Ctx(0, []).Request, 1024, allowEmpty: false);
        Assert.Null(body);
        Assert.Equal(400, status);
        Assert.Equal("empty_body", err);
    }

    [Fact]
    public async Task 空body_allowEmptyTrue_收()
    {
        var (body, status, err) = await IngestBody.ReadWithLimitAsync(Ctx(0, []).Request, 1024, allowEmpty: true);
        Assert.NotNull(body);
        Assert.Empty(body);
        Assert.Null(status);
    }
}

/// M5 採集端硬化:伺服器側獨立復判分(不信任 Agent 自報)的自動化驗收基礎(第二節待辦#1 真機驗收的前置)。
/// 三個健康信號應得固定 server_risk;疑似掛起=0;四個 M5 信號均歸 source='health'(經「採集健康」面板呈現,不汙染作弊裁決率)。
public class M5SignalServerRiskTests
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    [Fact] public void 屏幕遮挡服务器复判60() =>
        Assert.Equal(RiskScores.ScreenObscured, Horus.Server.Analysis.RiskModel.Derive(SignalType.ScreenshotObscured, Empty, null, null));
    [Fact] public void 能力降级服务器复判55() =>
        Assert.Equal(RiskScores.CapabilityDegraded, Horus.Server.Analysis.RiskModel.Derive(SignalType.CapabilityDegraded, Empty, null, null));
    [Fact] public void 看门狗重启服务器复判55() =>
        Assert.Equal(RiskScores.WatchdogRestart, Horus.Server.Analysis.RiskModel.Derive(SignalType.WatchdogRestart, Empty, null, null));
    [Fact] public void 疑似挂起服务器复判0() =>
        Assert.Equal(0, Horus.Server.Analysis.RiskModel.Derive(SignalType.SuspectedSuspend, Empty, null, null));

    [Fact] public void 四个M5信号均归健康面板()
    {
        foreach (SignalType t in new[]
        {
            SignalType.ScreenshotObscured, SignalType.CapabilityDegraded,
            SignalType.WatchdogRestart, SignalType.SuspectedSuspend,
        })
            Assert.Equal("health", Horus.Server.Analysis.Suspicion.SourceForKind(Horus.Server.Analysis.Suspicion.KindFor(t, Empty)));
    }
}

/// 第二節待辦#2:both→oidc 灰度。AuthMode 驅動採集面鑑權形態——psk/both 接受 PSK ingest,oidc 不接受;oidc/both 啟用 OIDC。
/// /api/preflight 據此報告仍走 PSK 的座位數,作為切 oidc 的 go/no-go 信號(設定驅動 + preflight 已完成實作)。
public class AuthModeGrayMigrationTests
{
    [Fact] public void psk模式接受PSK且不开OIDC() =>
        Assert.True(Mk("psk").PskAcceptedForIngest && !Mk("psk").OidcEnabled);
    [Fact] public void oidc模式拒PSK且开OIDC() =>
        Assert.False(Mk("oidc").PskAcceptedForIngest && Mk("oidc").OidcEnabled);
    [Fact] public void both模式共存PSK与OIDC() =>
        Assert.True(Mk("both").PskAcceptedForIngest && Mk("both").OidcEnabled);

    private static Horus.Server.Config.ServerConfig Mk(string mode) => new() { AuthMode = mode };
}
