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
        Assert.Equal(1, evAck.GetProperty("seq").GetInt64());   // 逐條 ack 本條 seq

        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("browser_url", events[0].GetProperty("type").GetString());
        Assert.Equal(80, events[0].GetProperty("risk").GetInt32());
        // payload 是嵌套物件(非字串)
        Assert.Equal("https://chat.openai.com/", events[0].GetProperty("payload").GetProperty("url").GetString());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("web_ai", susp[0].GetProperty("kind").GetString());
        Assert.Equal(80, susp[0].GetProperty("score").GetInt32());
        Assert.Equal("pending", susp[0].GetProperty("status").GetString());

        // 重傳同一 (agentId, seq, type) → 冪等,不重複寫入資料庫/入隊
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
        Assert.Equal(9, ack.GetProperty("seq").GetInt64());       // 確認的是本條 seq
        Assert.False(ack.TryGetProperty("upto", out _));          // 不再有範圍 upto(否則空洞會誤刪)
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
        // risk=40 < 閾值50,但 note=url_unreadable → 仍須入隊(強制人工看截圖的備援)
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
        // 事件先到,引用一個此刻尚不存在的證據圖
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false },
            80, 1, evidenceImageId: imgId));
        await Ws.ReceiveAsync(ws); // ack

        // 圖片後到(帶同一 client id)→ 應回填 is_evidence
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

        // 用錯誤 PSK 簽名 → sig 不匹配
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

        // 相同 phash 再傳 → 去重(duplicate=true,重複使用 imageId)
        string dupId = await UploadImageAsync(http, webp, exam, seat, agent, 6, trigger, phash, ts, expectDuplicate: true);
        Assert.Equal(imageId, dupId);

        // 取回位元組
        HttpResponseMessage img = await http.GetAsync($"/api/images/{imageId}");
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/webp", img.Content.Headers.ContentType!.MediaType);
        Assert.Equal(webp, await img.Content.ReadAsByteArrayAsync());

        // 中繼資料
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

        // 無簽名 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(bodyBytes, null))).StatusCode);
        // 錯簽名 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(bodyBytes, "deadbeef"))).StatusCode);
        // 栽贓:改 seatId 為 B99 但拿原 body 的簽名 → 401(內容綁定被破壞)
        byte[] tampered = Encoding.UTF8.GetBytes(bodyJson.Replace("A07", "B99"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(tampered, goodSig))).StatusCode);

        // 有效簽名 → 200 + 寫入資料庫(risk=70)
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

        // 初始:離線
        JsonElement seats0 = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/seats");
        Assert.Equal(1, seats0.GetArrayLength());
        Assert.False(seats0[0].GetProperty("online").GetBoolean());

        using WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07");
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        // 心跳事件(ts 用當前時鐘,才落在線上視窗內)
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

        // 無權杖 → 401(擋住學員機調 config/decide/讀資料)
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);

        // 錯權杖 → 401
        var bad = new HttpRequestMessage(HttpMethod.Get, "/api/exams");
        bad.Headers.Add("X-Horus-Admin", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);

        // 對權杖 → 放行(200)
        var ok = new HttpRequestMessage(HttpMethod.Get, "/api/exams");
        ok.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ok)).StatusCode);

        // 圖片位元組端點用 ?t= 查詢權杖(<img> 無法設頭):對權杖過鑑權(404 而非 401),無權杖 401
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/images/nope?t={TestApp.AdminToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/images/nope")).StatusCode);

        // /ingest/* 不受管理權杖影響(採集面走 PSK,不走 admin token)
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await http.PostAsync("/ingest/keystroke",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task 管理端点_cookie登录放行_登出后401()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();   // WebApplicationFactory 預設 HandleCookies=true,跨請求保持 cookie

        // 未登入 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);

        // 錯權杖登入 → 401,不種 cookie
        HttpResponseMessage badLogin = await http.PostAsJsonAsync("/api/login", new { token = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);

        // 正確權杖登入 → 200 + Set-Cookie(HttpOnly)
        HttpResponseMessage login = await http.PostAsJsonAsync("/api/login", new { token = TestApp.AdminToken });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers, h => h.Key == "Set-Cookie" &&
            h.Value.Any(v => v.Contains("horus_admin") && v.Contains("httponly", StringComparison.OrdinalIgnoreCase)));

        // 帶 cookie → 放行(不再需要 X-Horus-Admin 頭)
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/exams")).StatusCode);

        // 登出 → 清 cookie → 再訪問 401
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/exams")).StatusCode);
    }

    [Fact]
    public async Task 安全响应头_CSP指令值到位_而非仅存在()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        HttpResponseMessage r = await http.GetAsync("/api/exams");
        Assert.True(r.Headers.Contains("Content-Security-Policy"), "缺 CSP 头");
        Assert.True(r.Headers.Contains("X-Content-Type-Options"), "缺 nosniff 头");
        Assert.True(r.Headers.Contains("X-Frame-Options"), "缺 X-Frame-Options 头");
        // 斷言 CSP **具體指令**(防有人誤改成 unsafe-inline / 刪關鍵收緊項而測試仍綠):
        string csp = string.Join(" ", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);       // 同源指令碼 + 下方明確允許的 Cloudflare 分析來源
        Assert.Contains("https://static.cloudflareinsights.com", csp);
        Assert.Contains("connect-src 'self' https://challenges.cloudflare.com https://cloudflareinsights.com", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("script-src 'unsafe-inline'", csp);   // 絕不放開內聯指令碼
    }

    [Fact]
    public async Task 图片_客户端预生成id_幂等沿用()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();

        byte[] webp = Encoding.ASCII.GetBytes("RIFF-webp-client-id-test");
        string cid = "img_" + Guid.NewGuid().ToString("N");

        // 帶用戶端 id 上傳 → 伺服器沿用該 id
        string id1 = await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 5, "event:browser",
            "aabbccddeeff0011", "1750000000.100", expectDuplicate: false, clientId: cid);
        Assert.Equal(cid, id1);

        // 同 id 重傳(續傳)→ 冪等,不另存
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

        // 學員機持 PSK,把"訪問 AI 站"事件合法簽成 risk=0 + whitelisted=true 試圖逃逸可疑佇列
        await Ws.SendAsync(ws, Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = true }, 0, seq: 1));
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());     // 簽名合法,照收(不誤傷)

        // 事件寫入資料庫:Agent 自報 risk=0 原樣留證,但伺服器獨立復判 serverRisk=80
        JsonElement events = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/events?seatId=A07");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal(0, events[0].GetProperty("risk").GetInt32());
        Assert.Equal(80, events[0].GetProperty("serverRisk").GetInt32());

        // 仍入可疑佇列:score=有效風險 80,kind=web_ai,note 記 agent_risk_understated(篡改取證)
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

        // agent 謊報 TeamViewer 為白名單 risk=0,但命中伺服器遠控黑名單 → 獨立判 70 入隊
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
        Assert.Equal(0, susp.GetArrayLength());   // 良性事件不入隊,不製造假陽性
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

        // agent 把 5000 字元大段粘貼簽成 large=false, risk=0;伺服器從 len/lines 獨立復判 → 60 入隊 + 篡改標記
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

        // 原樣重放 → 冪等:不另存、不重複入隊
        HttpResponseMessage r2 = await http.PostAsJsonAsync("/ingest/keystroke", body);
        r2.EnsureSuccessStatusCode();
        JsonElement b2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(b2.GetProperty("stored").GetBoolean());
        Assert.True(b2.GetProperty("duplicate").GetBoolean());

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());   // 只入隊一次
    }

    [Fact]
    public async Task 视觉分析_识别AI界面_落库并入可疑队列()
    {
        using var app = new TestApp(visionMock: true);
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        // 上傳一張(觸發型)含 AICHAT 標記的圖 → 後台 Mock 視覺分析判 web_ai(置信 95)
        byte[] webp = Encoding.ASCII.GetBytes("RIFF....WEBP AICHAT on screen");
        string cid = "img_" + Guid.NewGuid().ToString("N");
        await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 5, "event:browser",
            "aabbccddeeff0011", "1750000000.100", expectDuplicate: false, clientId: cid);

        // 後台非同步:等可疑佇列出現 web_ai(vision)
        await WaitForAsync(async () =>
        {
            JsonElement s = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
            return s.GetArrayLength() == 1 && s[0].GetProperty("kind").GetString() == "web_ai";
        });

        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal("web_ai", susp[0].GetProperty("kind").GetString());
        Assert.Equal(95, susp[0].GetProperty("score").GetInt32());
        Assert.Contains("vision:", susp[0].GetProperty("note").GetString());

        // 圖中繼資料:已分析(終結)+ 標為證據。mock 不出網 → uploadedToOcr 恆 false(隱私稽核真出網信號,不再撒謊)。
        JsonElement meta = await http.GetFromJsonAsync<JsonElement>($"/api/images/{cid}/meta");
        Assert.True(meta.GetProperty("analyzed").GetBoolean());
        Assert.False(meta.GetProperty("uploadedToOcr").GetBoolean());
        Assert.True(meta.GetProperty("isEvidence").GetBoolean());
    }

    [Fact]
    public async Task 视觉分析_良性截图_已分析但不入队()
    {
        using var app = new TestApp(visionMock: true);
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        byte[] webp = Encoding.ASCII.GetBytes("RIFF....WEBP normal code editor view");
        string cid = "img_" + Guid.NewGuid().ToString("N");
        await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 5, "event:browser",
            "aabbccddeeff0011", "1750000000.100", expectDuplicate: false, clientId: cid);

        // 等分析跑完(analyzed=true),再確認無可疑(良性圖不製造假陽性)
        await WaitForAsync(async () =>
        {
            JsonElement m = await http.GetFromJsonAsync<JsonElement>($"/api/images/{cid}/meta");
            return m.GetProperty("analyzed").GetBoolean();
        });
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(0, susp.GetArrayLength());
    }

    [Fact]   // F5(第三輪):事件+證據圖連線路徑,視覺命中不再另插重複 pending,而是併入 ingest 期那條(避免同一事兩條、裁決兩次)。
    public async Task 视觉命中并入既有pending_不重复入队()
    {
        using var app = new TestApp(visionMock: true);
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        string cid = "img_" + Guid.NewGuid().ToString("N");
        // 1) 先發一條訪問 AI 站的 browser_url 事件,攜 evidenceImageId=cid → ingest 期入隊一條(effRisk 80)。
        using (WebSocket ws = await app.ConnectEventsAsync("E1", "A07", "ag-A07"))
        {
            string ev = Ws.SignedEvent("E1", "A07", "ag-A07", "PC-A07", SignalType.BrowserUrl,
                new() { ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1,
                evidenceImageId: cid);
            await Ws.SendAsync(ws, ev);
            await Ws.ReceiveAsync(ws);   // ack
        }
        await WaitForAsync(async () =>
        {
            JsonElement s = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
            return s.GetArrayLength() == 1;
        });

        // 2) 上傳該證據圖(含 AICHAT 標記 → mock 判 web_ai)→ 觸發視覺分析。
        byte[] webp = Encoding.ASCII.GetBytes("RIFF....WEBP AICHAT on screen");
        await UploadImageAsync(http, webp, "E1", "A07", "ag-A07", 5, "event:browser",
            "aabbccddeeff0011", "1750000000.100", expectDuplicate: false, clientId: cid);

        // 3) 等視覺證據併入(note 出現 vision:),斷言**仍只有一條** pending(去重生效)。
        await WaitForAsync(async () =>
        {
            JsonElement s = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
            return s.GetArrayLength() == 1 && (s[0].GetProperty("note").GetString() ?? "").Contains("vision:");
        });
        JsonElement susp = await http.GetFromJsonAsync<JsonElement>("/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());   // 未翻倍
    }

    private static async Task WaitForAsync(Func<Task<bool>> cond, int timeoutMs = 8000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            if (await cond()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("条件未在超时内满足");
    }

    // ---- 圖片上傳小工具 ----
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
