using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Data;
using Horus.Server.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Horus.Server.Tests;

/// M3 归档 / 清理作业:到龄考试关键数据入 archive 库、非关键就地清理、status='archived';
/// pending 未裁决则跳过;未到龄不动;幂等重跑 no-op。
public class ArchiveTests
{
    private const string Exam = "E1", Seat = "A07", Agent = "ag-A07", Machine = "PC-A07";
    private const double Day = 86400.0;

    private static async Task CreateExamAsync(HttpClient http)
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId = Exam, name = "归档测试",
            seats = new[] { new { seatId = Seat, agentId = Agent, machineId = Machine, displayName = "学员", studentId = "s01" } },
        });
        r.EnsureSuccessStatusCode();
    }

    private static async Task<WebSocket> HelloAsync(TestApp app)
    {
        WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        return ws;
    }

    private static async Task SendEventAsync(WebSocket ws, SignalType type, Dictionary<string, object?> payload,
        int risk, long seq, string? evidenceImageId = null)
    {
        // 各事件独立自洽(hashPrev=GENESIS),足以过 ingest 验签 + 哈希复验(归档不看链连续)。
        string json = Ws.SignedEvent(Exam, Seat, Agent, Machine, type, payload, risk, seq, evidenceImageId: evidenceImageId);
        await Ws.SendAsync(ws, json);
        await Ws.ReceiveAsync(ws);   // ack
    }

    private static async Task<string> UploadImageAsync(HttpClient http, string trigger, string clientId, long seq)
    {
        byte[] webp = Encoding.ASCII.GetBytes("RIFF-webp-" + clientId);
        const string phash = "aabbccddeeff0011", ts = "1750000000.100";
        string canon = Auth.ImageCanonicalHeaders(Exam, Seat, Agent, seq, trigger, phash, ts, clientId);
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);

        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images") { Content = new ByteArrayContent(webp) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Horus-Exam", Exam);
        req.Headers.Add("X-Horus-Seat", Seat);
        req.Headers.Add("X-Horus-Agent", Agent);
        req.Headers.Add("X-Horus-Seq", seq.ToString());
        req.Headers.Add("X-Horus-Trigger", trigger);
        req.Headers.Add("X-Horus-Phash", phash);
        req.Headers.Add("X-Horus-Ts", ts);
        req.Headers.Add("X-Horus-Sig", sig);
        req.Headers.Add("X-Horus-Image-Id", clientId);
        HttpResponseMessage resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return clientId;
    }

    private static long OpenArchiveScalar(string archivePath, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = archivePath, Pooling = false }.ToString());
        conn.Open();
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = sql;
        object? v = c.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    [Fact]
    public async Task 到龄考试_关键入archive_非关键清理_置archived()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        const string cidEv = "img_evidence0001", cidBase = "img_baseline0001";

        using (WebSocket ws = await HelloAsync(app))
        {
            // 证据图先到,随后高危事件引用它 → 服务器标 is_evidence=1(关键)
            await UploadImageAsync(http, "event:browser", cidEv, 10);
            await SendEventAsync(ws, SignalType.BrowserUrl,
                new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1, evidenceImageId: cidEv);
            // 低危事件(非关键)
            await SendEventAsync(ws, SignalType.WindowFocus, new() { ["title"] = "题目.pdf", ["process"] = "acrobat" }, 0, 2);
            // 随机基线图(非关键,应被清理)
            await UploadImageAsync(http, "baseline_random", cidBase, 11);
        }

        // 裁决那条 web_ai → confirmed(否则 pending 会挡归档),同时成为一条归档裁决
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        long sid = susp[0].GetProperty("id").GetInt64();
        (await http.PostAsJsonAsync($"/api/suspicious/{sid}/decide", new { status = "confirmed", reviewer = "监考A" })).EnsureSuccessStatusCode();

        // 结束考试
        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        // 归档:传入"31 天后"的 now → 该考试到龄
        var archive = app.Services.GetRequiredService<ArchiveService>();
        var storage = app.Services.GetRequiredService<Storage>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 31 * Day;
        ArchiveService.Report report = archive.RunOnce(now);

        Assert.Equal(1, report.Scanned);
        Assert.Equal(1, report.Archived);
        Assert.Equal(0, report.Skipped);

        // live 端:该考试事件 / 图片 / 可疑全清空,status=archived
        JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events");
        Assert.Equal(0, events.GetArrayLength());
        JsonElement all = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/suspicious?status=all");
        Assert.Equal(0, all.GetArrayLength());
        JsonElement exams = await http.GetFromJsonAsync<JsonElement>("/api/exams");
        Assert.Equal("archived", exams[0].GetProperty("status").GetString());

        // archive 库:1 关键事件、1 证据图、1 裁决、1 考试汇总
        string ap = archive.ArchivePath;
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_events"));
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_images"));
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_adjudications"));
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_exams"));
        Assert.Equal(80, OpenArchiveScalar(ap, "SELECT risk FROM archive_events"));

        // 文件:证据图迁入 archive/ 冷存(live 原图不在);基线图被删
        string evLive = storage.Resolve(Storage.RelPath(Exam, Seat, cidEv))!;
        string evCold = storage.Resolve(Storage.ArchiveRelPath(Exam, Seat, cidEv))!;
        string baseLive = storage.Resolve(Storage.RelPath(Exam, Seat, cidBase))!;
        Assert.False(File.Exists(evLive), "证据图应已从 live 迁走");
        Assert.True(File.Exists(evCold), "证据图应在 archive 冷存");
        Assert.False(File.Exists(baseLive), "非关键基线图应被清理");

        // F3(第三轮):归档后证据图/元数据仍可经 HTTP 取证读取(此前 live 行删掉后 /api/images 一律 404)。
        HttpResponseMessage imgResp = await http.GetAsync($"/api/images/{cidEv}");
        Assert.Equal(HttpStatusCode.OK, imgResp.StatusCode);
        Assert.True((await imgResp.Content.ReadAsByteArrayAsync()).Length > 0);   // 冷存字节送出
        JsonElement meta = await http.GetFromJsonAsync<JsonElement>($"/api/images/{cidEv}/meta");
        Assert.True(meta.GetProperty("archived").GetBoolean());
        // 归档考试只读复核端点:汇总 + 裁决 + 关键事件 + 证据图都可读回
        JsonElement arc = await http.GetFromJsonAsync<JsonElement>($"/api/archive/exams/{Exam}");
        Assert.True(arc.GetProperty("archived").GetBoolean());
        Assert.Equal(1, arc.GetProperty("adjudications").GetArrayLength());
        Assert.Equal(1, arc.GetProperty("images").GetArrayLength());
        Assert.True(arc.GetProperty("events").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task 击键定罪证据_随confirmed裁决归档_不丢证据()
    {
        // 闭合审计 C1:被 confirmed 裁决引用的击键样本必须进 archive,而非在清理时被无条件删除。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        // 判题网页上报"空窗后突现整段代码"→ keystroke_samples 落库 + 入可疑队列(large_paste/ide_plugin_suspect, risk=70)
        HttpResponseMessage ks = await http.PostAsJsonAsync("/ingest/keystroke", new
        {
            examId = Exam, seatId = Seat, submissionId = "sub1", ts = 1750000000.0,
            timeline = new[] { 12, 340, 355, 361 },
            features = new { idleThenBlock = true, pasteCount = 1, maxBurstCharsPerSec = 200 },
        });
        ks.EnsureSuccessStatusCode();

        // 监考员裁决 confirmed —— 其唯一证据就是这条击键时间线
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        long sid = susp[0].GetProperty("id").GetInt64();
        (await http.PostAsJsonAsync($"/api/suspicious/{sid}/decide", new { status = "confirmed", reviewer = "监考A" })).EnsureSuccessStatusCode();

        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        var archive = app.Services.GetRequiredService<ArchiveService>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 31 * Day;
        ArchiveService.Report report = archive.RunOnce(now);
        Assert.Equal(1, report.Archived);

        // archive 库:击键证据 + 裁决都在,timeline 字节完好
        string ap = archive.ArchivePath;
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_keystroke_samples"));
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_adjudications"));
        Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_keystroke_samples WHERE timeline IS NOT NULL AND length(timeline)>0"));

        // live 已清空(击键证据已安全落 archive)
        JsonElement all = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/suspicious?status=all");
        Assert.Equal(0, all.GetArrayLength());
    }

    [Fact]
    public async Task 有pending未裁决_跳过归档()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using (WebSocket ws = await HelloAsync(app))
            await SendEventAsync(ws, SignalType.BrowserUrl,
                new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1);
        // 不裁决 → 留 pending
        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        var archive = app.Services.GetRequiredService<ArchiveService>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 31 * Day;
        ArchiveService.Report report = archive.RunOnce(now);

        Assert.Equal(1, report.Scanned);
        Assert.Equal(0, report.Archived);
        Assert.Equal(1, report.Skipped);
        Assert.Contains("pending", report.Exams[0].Detail);

        // 未 purge:事件仍在,status 仍 ended
        JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events");
        Assert.Equal(1, events.GetArrayLength());
        JsonElement exams = await http.GetFromJsonAsync<JsonElement>("/api/exams");
        Assert.Equal("ended", exams[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task 未到龄考试_不归档()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        var archive = app.Services.GetRequiredService<ArchiveService>();
        // now = 真实此刻(考试刚结束)→ 未过 30 天留存 → 不候选
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        ArchiveService.Report report = archive.RunOnce(now);
        Assert.Equal(0, report.Scanned);

        JsonElement exams = await http.GetFromJsonAsync<JsonElement>("/api/exams");
        Assert.Equal("ended", exams[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task 幂等_二次运行no_op()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using (WebSocket ws = await HelloAsync(app))
            await SendEventAsync(ws, SignalType.WindowFocus, new() { ["title"] = "题目.pdf" }, 0, 1);
        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        var archive = app.Services.GetRequiredService<ArchiveService>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 31 * Day;

        ArchiveService.Report r1 = archive.RunOnce(now);
        Assert.Equal(1, r1.Archived);
        // 二次:考试已 archived,不再是 ended 候选 → 扫描 0
        ArchiveService.Report r2 = archive.RunOnce(now);
        Assert.Equal(0, r2.Scanned);
        Assert.Equal(0, r2.Archived);
    }
}

/// 文件库归档(非 :memory:):走真只读连接池 + 真 VACUUM + 冷存文件移动。既有 ArchiveTests 全 :memory: 回退单连接,
/// 此类补文件库端到端盲区(含墓碑 'archiving' 态崩溃续跑)。
public class FileDbArchiveTests
{
    private const double Day = 86400.0;

    private static string TempDir()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "horus-farch-" + Guid.NewGuid().ToString("N")[..10])).FullName;

    private static ServerConfig Cfg() => new()
    {
        RetentionDays = 30, ArchiveCriticalRisk = 50, ArchiveDbPath = "arch.db", ArchiveEnabled = false,
    };

    private static void SeedEndedExam(Db db, string status, double endedAt)
    {
        db.Write(conn =>
        {
            using (SqliteCommand c = conn.Cmd(
                "INSERT INTO exams (exam_id,name,status,started_at,ended_at,created_at) VALUES ('E1','x',@st,@s,@e,@s)",
                ("@st", status), ("@s", endedAt), ("@e", endedAt))) c.ExecuteNonQuery();
            using (SqliteCommand c = conn.Cmd("INSERT INTO seats (exam_id,seat_id,agent_id) VALUES ('E1','A01','ag')")) c.ExecuteNonQuery();
            using (SqliteCommand c = conn.Cmd(
                @"INSERT INTO events (exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,server_risk,hash_self,sig)
                  VALUES ('E1','A01','ag','PC',1,@ts,@ts,'browser_url','{""url"":""x""}',80,80,'h','s')", ("@ts", endedAt))) c.ExecuteNonQuery();
        });
    }

    private static long OpenArchiveScalar(string archivePath, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = archivePath, Pooling = false }.ToString());
        conn.Open();
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = sql;
        object? v = c.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    [Fact]
    public void 文件库到龄归档_关键入档_live清空置archived_VACUUM不抛()
    {
        string dir = TempDir();
        try
        {
            using var db = new Db(Path.Combine(dir, "live.db"));
            var storage = new Storage(dir);
            SeedEndedExam(db, "ended", 1000.0);

            var svc = new ArchiveService(db, storage, Cfg(), NullLogger<ArchiveService>.Instance);
            ArchiveService.Report report = svc.RunOnce(1000.0 + 40 * Day);

            Assert.Equal(1, report.Archived);
            long liveEvents = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT COUNT(*) FROM events WHERE exam_id='E1'"); return Convert.ToInt64(c.ExecuteScalar()); });
            Assert.Equal(0, liveEvents);   // live 清空(且 VACUUM 已在文件库上跑过,未抛)
            string status = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT status FROM exams WHERE exam_id='E1'"); return (string)c.ExecuteScalar()!; });
            Assert.Equal("archived", status);
            Assert.Equal(1, OpenArchiveScalar(Path.Combine(dir, "arch.db"), "SELECT COUNT(*) FROM archive_events"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 墓碑archiving态_崩溃续跑_收敛为archived()
    {
        // 模拟上次崩在归档中途:status='archiving' 且 live 数据仍在。候选查询含 'archiving' → 本次续跑跑完。
        string dir = TempDir();
        try
        {
            using var db = new Db(Path.Combine(dir, "live.db"));
            var storage = new Storage(dir);
            SeedEndedExam(db, "archiving", 1000.0);

            var svc = new ArchiveService(db, storage, Cfg(), NullLogger<ArchiveService>.Instance);
            ArchiveService.Report report = svc.RunOnce(1000.0 + 40 * Day);

            Assert.Equal(1, report.Archived);   // 墓碑态被拾起续跑
            string status = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT status FROM exams WHERE exam_id='E1'"); return (string)c.ExecuteScalar()!; });
            Assert.Equal("archived", status);
            Assert.Equal(1, OpenArchiveScalar(Path.Combine(dir, "arch.db"), "SELECT COUNT(*) FROM archive_events"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private const string ImgId = "img_ev010000";
    private const string PayloadText = "{\"note\":\"x\"}";

    /// 播种一条**合法签名**的关键事件(risk80·引用证据图)+ 其证据图(is_evidence=1),event.id 显式=1(便于模拟崩溃续跑)。
    /// 返回该事件 hash_self(供独立复验)。status/是否写 live 图文件可配。
    private static string SeedSignedEvidence(Db db, Storage storage, string status, double t, bool writeImgFile)
    {
        var core = new AgentEvent
        {
            ExamId = "E1", SeatId = "A01", AgentId = "ag", MachineId = "PC",
            Ts = t, Type = SignalType.BrowserUrl, Payload = new() { ["note"] = "x" },
            Risk = 80, EvidenceImageId = ImgId, Seq = 1,
        };
        string hs = EventCanonical.HashSelf("GENESIS", core, 1);
        string sig = EventCanonical.Sig(TestApp.Psk, hs, 1);
        if (writeImgFile)
            storage.SaveWebpAsync("E1", "A01", ImgId, Encoding.ASCII.GetBytes("webp-" + ImgId)).GetAwaiter().GetResult();
        db.Write(conn =>
        {
            using (SqliteCommand c = conn.Cmd(
                @"INSERT INTO exams (exam_id,name,status,started_at,ended_at,created_at) VALUES ('E1','x',@st,@t,@t,@t)
                  ON CONFLICT(exam_id) DO UPDATE SET status=@st, ended_at=@t",   // 用 UPDATE 而非 REPLACE:避免隐式 DELETE 触发 seats FK
                ("@st", status), ("@t", t))) c.ExecuteNonQuery();
            using (SqliteCommand c = conn.Cmd("INSERT OR IGNORE INTO seats (exam_id,seat_id,agent_id) VALUES ('E1','A01','ag')")) c.ExecuteNonQuery();
            using (SqliteCommand c = conn.Cmd(
                @"INSERT INTO events (id,exam_id,seat_id,agent_id,machine_id,seq,ts,recv_ts,type,payload,risk,evidence_image_id,hash_prev,hash_self,sig)
                  VALUES (1,'E1','A01','ag','PC',1,@t,@t,'browser_url',@p,80,@img,'GENESIS',@hs,@sig)",
                ("@t", t), ("@p", PayloadText), ("@img", ImgId), ("@hs", hs), ("@sig", sig))) c.ExecuteNonQuery();
            using (SqliteCommand c = conn.Cmd(
                @"INSERT INTO images (image_id,exam_id,seat_id,agent_id,ts,recv_ts,trigger,phash,file_path,format,is_evidence)
                  VALUES (@img,'E1','A01','ag',@t,@t,'event:browser','0000000000000000',@fp,'webp',1)",
                ("@img", ImgId), ("@t", t), ("@fp", Storage.RelPath("E1", "A01", ImgId)))) c.ExecuteNonQuery();
        });
        return hs;
    }

    [Fact]
    public void 归档事件保留machineId与原始risk_锚点可独立逐字节复验()
    {
        // #item3:archive_events 补 machine_id + 存原始 agent risk → 归档后凭落档字段独立复算 hash_self 应一致。
        string dir = TempDir();
        try
        {
            using var db = new Db(Path.Combine(dir, "live.db"));
            var storage = new Storage(dir);
            string hs = SeedSignedEvidence(db, storage, "ended", 1000.0, writeImgFile: true);

            var svc = new ArchiveService(db, storage, Cfg(), NullLogger<ArchiveService>.Instance);
            Assert.Equal(1, svc.RunOnce(1000.0 + 40 * Day).Archived);

            // 从 archive 库读回落档字段,独立复算 hash_self
            using var arc = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "arch.db"), Pooling = false }.ToString());
            arc.Open();
            using SqliteCommand c = arc.CreateCommand();
            c.CommandText = "SELECT machine_id,risk,payload,hash_self FROM archive_events WHERE id=1";
            using SqliteDataReader r = c.ExecuteReader();
            Assert.True(r.Read());
            string machineId = r.GetString(0);
            int risk = r.GetInt32(1);
            string payload = r.GetString(2);
            string hashSelf = r.GetString(3);
            Assert.Equal("PC", machineId);
            Assert.Equal(80, risk);                 // 原始 agent risk(非 max)
            Assert.Equal(hs, hashSelf);
            bool ok = EventCanonical.VerifyHashSelf(
                "GENESIS", "E1", "A01", "ag", machineId, 1000.0, "browser_url", payload, risk, ImgId, 1, hashSelf);
            Assert.True(ok, "归档锚点应可凭落档字段独立复算 hash_self");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void copy后delete前崩溃_重跑不重复归档且live清空()
    {
        // #item4:模拟"copy 已完成(archive 有数据 + 证据图已移冷存)、delete 未执行(live 数据还在·status='archiving')"崩溃态。
        // 真实崩溃下 live 事件 id 不变,故用显式 id=1 复播,重跑应凭 ON CONFLICT(id)/MoveToArchive 容忍已迁而收敛、不产生重复。
        string dir = TempDir();
        try
        {
            using var db = new Db(Path.Combine(dir, "live.db"));
            var storage = new Storage(dir);
            string ap = Path.Combine(dir, "arch.db");

            // 第一次:完整归档(copy+文件移冷存+delete+archived)
            SeedSignedEvidence(db, storage, "ended", 1000.0, writeImgFile: true);
            var svc = new ArchiveService(db, storage, Cfg(), NullLogger<ArchiveService>.Instance);
            Assert.Equal(1, svc.RunOnce(1000.0 + 40 * Day).Archived);
            Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_events"));
            Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_images"));

            // 模拟崩溃残留:同 id 复播 live 行 + 置 archiving(证据图此刻已在冷存,不重写 live 文件)
            SeedSignedEvidence(db, storage, "archiving", 1000.0, writeImgFile: false);

            // 重跑 → 收敛:INSERT OR IGNORE 不重复、MoveToArchive 容忍源已迁、删 live、置 archived
            Assert.Equal(1, svc.RunOnce(1000.0 + 40 * Day).Archived);
            Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_events"));   // **无重复**
            Assert.Equal(1, OpenArchiveScalar(ap, "SELECT COUNT(*) FROM archive_images"));   // **无重复**
            long liveEv = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT COUNT(*) FROM events WHERE exam_id='E1'"); return Convert.ToInt64(c.ExecuteScalar()); });
            Assert.Equal(0, liveEv);   // live 清空
            string status = db.Read(conn => { using SqliteCommand c = conn.Cmd("SELECT status FROM exams WHERE exam_id='E1'"); return (string)c.ExecuteScalar()!; });
            Assert.Equal("archived", status);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
