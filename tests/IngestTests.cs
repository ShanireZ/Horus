using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Xunit;

namespace Horus.Server.Tests;

public class IngestTests
{
    private static async Task CreateExamAsync(HttpClient http, string examId = "E1", string seatId = "A07")
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId,
            name = "测试考试",
            seats = new[] { new { seatId, agentId = "ag-" + seatId, machineId = "PC-" + seatId, displayName = "学员", studentId = "s01" } },
        });
        r.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task 事件_落库_入可疑队列_且幂等去重()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");

        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\",\"agentId\":\"ag-A07\"}");
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("hello_ack", ack.GetProperty("type").GetString());
        Assert.Equal(0, ack.GetProperty("maxSeq").GetInt64());

        string evt = Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, seq: 1);
        await Ws.SendAsync(ws, evt);
        JsonElement evAck = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", evAck.GetProperty("type").GetString());
        Assert.Equal(1, evAck.GetProperty("seq").GetInt64());   // 逐条 ack 本条 seq

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("browser_url", events[0].GetProperty("type").GetString());
        Assert.Equal(80, events[0].GetProperty("risk").GetInt32());
        // payload 是嵌套对象(非字符串)
        Assert.Equal("https://chat.openai.com/", events[0].GetProperty("payload").GetProperty("url").GetString());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("web_ai", susp[0].GetProperty("kind").GetString());
        Assert.Equal(80, susp[0].GetProperty("score").GetInt32());
        Assert.Equal("pending", susp[0].GetProperty("status").GetString());

        // 重传同一 (agentId, seq, type) → 幂等,不重复落库/入队
        await Ws.SendAsync(ws, evt);
        await Ws.ReceiveAsync(ws); // ack
        JsonElement events2 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events2.GetArrayLength());
        JsonElement susp2 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp2.GetArrayLength());
    }

    [Fact]
    public async Task ack_逐条确认本条seq_非MAX水位()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws); // hello_ack

        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, seq: 9));
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());
        Assert.Equal(9, ack.GetProperty("seq").GetInt64());       // 确认的是本条 seq
        Assert.False(ack.TryGetProperty("upto", out _));          // 不再有范围 upto(否则空洞会误删)
    }

    [Fact]
    public async Task browser读不到URL_无视阈值_强制入可疑队列()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        // risk=40 < 阈值50,但 note=url_unreadable → 仍须入队(强制人工看截图的兜底)
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = null, ["note"] = "url_unreadable" }, 40, 1));
        await Ws.ReceiveAsync(ws); // ack

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("browser_unreadable", susp[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task 事件先于图片到达_图片后到时回填is_evidence()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);
        const string imgId = "img_0011223344556677";

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        // 事件先到,引用一个此刻尚不存在的证据图
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false },
            80, 1, evidenceImageId: imgId));
        await Ws.ReceiveAsync(ws); // ack

        // 图片后到(带同一 client id)→ 应回填 is_evidence
        byte[] webp = Encoding.ASCII.GetBytes("RIFF-late-arriving-evidence");
        await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 2, "event:browser",
            "aabbccddeeff0011", "1750000000.500", expectDuplicate: false, clientId: imgId);

        JsonElement meta = await http.GetFromJsonAsync<JsonElement>($"/api/images/{imgId}/meta");
        Assert.True(meta.GetProperty("isEvidence").GetBoolean());
    }

    [Fact]
    public async Task 验签失败_拒绝落库()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\",\"agentId\":\"ag-A07\"}");
        await Ws.ReceiveAsync(ws); // hello_ack

        // 用错误 PSK 签名 → sig 不匹配
        byte[] wrongPsk = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        string bad = Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1, psk: wrongPsk);
        await Ws.SendAsync(ws, bad);
        JsonElement err = await Ws.ReceiveAsync(ws);
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Equal("bad_sig", err.GetProperty("code").GetString());

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(0, events.GetArrayLength());
    }

    [Fact]
    public async Task 握手鉴权失败_拒绝连接()
    {
        using var app = new TestApp();
        await Assert.ThrowsAnyAsync<Exception>(
            () => app.ConnectEventsAsync("E1", "A07", "ag-A07", goodAuth: false));
    }

    [Fact]
    public async Task 图片上传_存盘_去重_可取回()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();

        byte[] webp = Encoding.ASCII.GetBytes("RIFF....WEBP-fake-bytes-0123456789");
        const string exam = "E1", seat = "A07", agent = "ag-A07", trigger = "event:browser";
        const string phash = "9f3c1a22b0e4d7f1", ts = "1750000000.456";
        const long seq = 5;

        string imageId = await UploadImageAsync(http, webp, exam, seat, agent, seq, trigger, phash, ts, expectDuplicate: false);

        // 相同 phash 再传 → 去重(duplicate=true,复用 imageId)
        string dupId = await UploadImageAsync(http, webp, exam, seat, agent, 6, trigger, phash, ts, expectDuplicate: true);
        Assert.Equal(imageId, dupId);

        // 取回字节
        HttpResponseMessage img = await http.GetAsync($"/api/images/{imageId}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/webp", img.Content.Headers.ContentType!.MediaType);
        Assert.Equal(webp, await img.Content.ReadAsByteArrayAsync());

        // 元数据
        JsonElement meta = await http.GetFromJsonAsync<JsonElement>($"/api/images/{imageId}/meta");
        Assert.Equal(phash, meta.GetProperty("phash").GetString());
        Assert.Equal(webp.Length, meta.GetProperty("bytes").GetInt64());
        Assert.Equal(trigger, meta.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task 击键节奏_空窗后突现整段_入可疑()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        HttpResponseMessage r = await http.PostAsJsonAsync("/ingest/keystroke", new
        {
            examId = "E1", seatId = "A07", submissionId = "sub1", ts = 1750000000.0,
            timeline = new[] { 1, 2, 3 },
            features = new { idleThenBlock = true, pasteCount = 0, maxBurstCharsPerSec = 30 },
        });
        r.EnsureSuccessStatusCode();
        JsonElement body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("stored").GetBoolean());
        Assert.Equal(70, body.GetProperty("risk").GetInt32());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("ide_plugin_suspect", susp[0].GetProperty("kind").GetString());
        Assert.Equal(70, susp[0].GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task 击键鉴权_无签名或篡改401_有效签名放行()
    {
        using var app = new TestApp(keystrokeAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        const string bodyJson = "{\"examId\":\"E1\",\"seatId\":\"A07\",\"submissionId\":\"sub1\",\"ts\":1750000000.0," +
                                "\"timeline\":[1,2,3],\"features\":{\"idleThenBlock\":true}}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        string goodSig = Auth.KeystrokeSig(TestApp.Ksk, bodyBytes);

        static HttpRequestMessage Req(byte[] b, string? sig)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/ingest/keystroke") { Content = new ByteArrayContent(b) };
            m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (sig is not null) m.Headers.Add("X-Horus-KSig", sig);
            return m;
        }

        // 无签名 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(bodyBytes, null))).StatusCode);
        // 错签名 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(bodyBytes, "deadbeef"))).StatusCode);
        // 栽赃:改 seatId 为 B99 但拿原 body 的签名 → 401(内容绑定被破坏)
        byte[] tampered = Encoding.UTF8.GetBytes(bodyJson.Replace("A07", "B99"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(tampered, goodSig))).StatusCode);

        // 有效签名 → 200 + 落库(risk=70)
        HttpResponseMessage ok = await http.SendAsync(Req(bodyBytes, goodSig));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        JsonElement b = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(70, b.GetProperty("risk").GetInt32());
    }

    [Fact]
    public async Task 人工裁决_确认后移出待复核()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.ProcessStart,
            new() { ["name"] = "cmd.exe", ["pid"] = 4321, ["whitelisted"] = false }, 70, 1));
        await Ws.ReceiveAsync(ws);

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        long id = susp[0].GetProperty("id").GetInt64();
        Assert.Equal("non_whitelist_proc", susp[0].GetProperty("kind").GetString());

        HttpResponseMessage dec = await http.PostAsJsonAsync($"/api/suspicious/{id}/decide",
            new { status = "confirmed", reviewer = "监考A", note = "确认使用命令行" });
        dec.EnsureSuccessStatusCode();
        JsonElement decBody = await dec.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("confirmed", decBody.GetProperty("item").GetProperty("status").GetString());

        JsonElement pending = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious?status=pending");
        Assert.Equal(0, pending.GetArrayLength());
        JsonElement confirmed = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious?status=confirmed");
        Assert.Equal(1, confirmed.GetArrayLength());
    }

    [Fact]
    public async Task 座位在线_心跳后热力反映()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        // 初始:离线
        JsonElement seats0 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.Equal(1, seats0.GetArrayLength());
        Assert.False(seats0[0].GetProperty("online").GetBoolean());

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        // 心跳事件(ts 用当前时钟,才落在在线窗口内)
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        string hb = Ws.SignedEventTs("E1", "A07", "ag-A07", "PC-A07", SignalType.Heartbeat,
            new() { ["status"] = "alive" }, 0, 1, now);
        await Ws.SendAsync(ws, hb);
        await Ws.ReceiveAsync(ws);

        JsonElement seats1 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.True(seats1[0].GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task 管理端点_无令牌401_有令牌放行_图片用查询令牌()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();

        // 无令牌 → 401(挡住学员机调 config/decide/读数据)
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);

        // 错令牌 → 401
        var bad = new HttpRequestMessage(HttpMethod.Get, "/api/exams");
        bad.Headers.Add("X-Horus-Admin", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);

        // 对令牌 → 放行(200)
        var ok = new HttpRequestMessage(HttpMethod.Get, "/api/exams");
        ok.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ok)).StatusCode);

        // 图片字节端点用 ?t= 查询令牌(<img> 无法设头):对令牌过鉴权(404 而非 401),无令牌 401
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/images/nope?t={TestApp.AdminToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/images/nope")).StatusCode);

        // /ingest/* 不受管理令牌影响(采集面走 PSK,不走 admin token)
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await http.PostAsync("/ingest/keystroke",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task 管理端点_cookie登录放行_登出后401()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();   // WebApplicationFactory 默认 HandleCookies=true,跨请求保持 cookie

        // 未登录 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);

        // 错令牌登录 → 401,不种 cookie
        HttpResponseMessage badLogin = await http.PostAsJsonAsync("/api/login", new { token = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);

        // 正确令牌登录 → 200 + Set-Cookie(HttpOnly)
        HttpResponseMessage login = await http.PostAsJsonAsync("/api/login", new { token = TestApp.AdminToken });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers, h => h.Key == "Set-Cookie" &&
            h.Value.Any(v => v.Contains("horus_admin") && v.Contains("httponly", StringComparison.OrdinalIgnoreCase)));

        // 带 cookie → 放行(不再需要 X-Horus-Admin 头)
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/exams")).StatusCode);

        // 登出 → 清 cookie → 再访问 401
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);
    }

    [Fact]
    public async Task 安全响应头_CSP与nosniff存在()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        HttpResponseMessage r = await http.GetAsync("/api/exams");
        Assert.True(r.Headers.Contains("Content-Security-Policy"), "缺 CSP 头");
        Assert.True(r.Headers.Contains("X-Content-Type-Options"), "缺 nosniff 头");
        Assert.True(r.Headers.Contains("X-Frame-Options"), "缺 X-Frame-Options 头");
    }

    [Fact]
    public async Task 图片_客户端预生成id_幂等沿用()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();

        byte[] webp = Encoding.ASCII.GetBytes("RIFF-webp-client-id-test");
        string cid = "img_" + Guid.NewGuid().ToString("N");

        // 带客户端 id 上传 → 服务器沿用该 id
        string id1 = await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 5, "event:browser",
            "aabbccddeeff0011", "1750000000.100", expectDuplicate: false, clientId: cid);
        Assert.Equal(cid, id1);

        // 同 id 重传(续传)→ 幂等,不另存
        string id2 = await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 6, "event:browser",
            "aabbccddeeff0011", "1750000000.200", expectDuplicate: true, clientId: cid);
        Assert.Equal(cid, id2);

        HttpResponseMessage img = await http.GetAsync($"/api/images/{cid}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
    }

    [Fact]
    public async Task 服务器侧risk重算_agent谎报risk0仍入队并记篡改()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        // 学员机持 PSK,把"访问 AI 站"事件合法签成 risk=0 + whitelisted=true 试图逃逸可疑队列
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = true }, 0, seq: 1));
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());     // 签名合法,照收(不误伤)

        // 事件落库:Agent 自报 risk=0 原样留证,但服务器独立复判 serverRisk=80
        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal(0, events[0].GetProperty("risk").GetInt32());
        Assert.Equal(80, events[0].GetProperty("serverRisk").GetInt32());

        // 仍入可疑队列:score=有效风险 80,kind=web_ai,note 记 agent_risk_understated(篡改取证)
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("web_ai", susp[0].GetProperty("kind").GetString());
        Assert.Equal(80, susp[0].GetProperty("score").GetInt32());
        Assert.Contains("agent_risk_understated", susp[0].GetProperty("note").GetString());
    }

    [Fact]
    public async Task 服务器侧risk重算_远控工具进程独立判高危()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        // agent 谎报 TeamViewer 为白名单 risk=0,但命中服务器远控黑名单 → 独立判 70 入队
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.ProcessStart,
            new() { ["name"] = "TeamViewer.exe", ["pid"] = 999, ["whitelisted"] = true }, 0, seq: 1));
        await Ws.ReceiveAsync(ws);

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("non_whitelist_proc", susp[0].GetProperty("kind").GetString());
        Assert.Equal(70, susp[0].GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task 服务器侧risk重算_良性事件不入队_serverRisk为0()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.WindowFocus,
            new() { ["title"] = "题目.pdf", ["process"] = "acrobat" }, 0, seq: 1));
        await Ws.ReceiveAsync(ws);

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(0, events[0].GetProperty("serverRisk").GetInt32());
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(0, susp.GetArrayLength());   // 良性事件不入队,不制造假阳性
    }

    [Fact]
    public async Task 服务器侧risk重算_谎报large为false的大段粘贴仍判高危()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        // agent 把 5000 字符大段粘贴签成 large=false, risk=0;服务器从 len/lines 独立复判 → 60 入队 + 篡改标记
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.Clipboard,
            new() { ["len"] = 5000, ["lines"] = 80, ["large"] = false }, 0, seq: 1));
        await Ws.ReceiveAsync(ws);

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("large_paste", susp[0].GetProperty("kind").GetString());
        Assert.Equal(60, susp[0].GetProperty("score").GetInt32());
        Assert.Contains("agent_risk_understated", susp[0].GetProperty("note").GetString());
    }

    [Fact]
    public async Task 击键_原样重放_幂等不重复入队()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        var body = new
        {
            examId = "E1", seatId = "A07", submissionId = "sub1", ts = 1750000000.0,
            timeline = new[] { 1, 2, 3 }, features = new { idleThenBlock = true },
        };

        HttpResponseMessage r1 = await http.PostAsJsonAsync("/ingest/keystroke", body);
        r1.EnsureSuccessStatusCode();
        Assert.True((await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        // 原样重放 → 幂等:不另存、不重复入队
        HttpResponseMessage r2 = await http.PostAsJsonAsync("/ingest/keystroke", body);
        r2.EnsureSuccessStatusCode();
        JsonElement b2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(b2.GetProperty("stored").GetBoolean());
        Assert.True(b2.GetProperty("duplicate").GetBoolean());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());   // 只入队一次
    }

    // ---- 图片上传小工具 ----
    private static async Task<string> UploadImageAsync(HttpClient http, byte[] webp,
        string exam, string seat, string agent, long seq, string trigger, string phash, string ts,
        bool expectDuplicate, string? clientId = null)
    {
        string canon = Auth.ImageCanonicalHeaders(exam, seat, agent, seq, trigger, phash, ts, clientId ?? "");
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);

        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images");
        req.Content = new ByteArrayContent(webp);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Horus-Exam", exam);
        req.Headers.Add("X-Horus-Seat", seat);
        req.Headers.Add("X-Horus-Agent", agent);
        req.Headers.Add("X-Horus-Seq", seq.ToString());
        req.Headers.Add("X-Horus-Trigger", trigger);
        req.Headers.Add("X-Horus-Phash", phash);
        req.Headers.Add("X-Horus-Ts", ts);
        req.Headers.Add("X-Horus-Sig", sig);
        if (clientId is not null) req.Headers.Add("X-Horus-Image-Id", clientId);

        HttpResponseMessage resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        JsonElement body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectDuplicate, body.GetProperty("duplicate").GetBoolean());
        return body.GetProperty("imageId").GetString()!;
    }
}
