using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Analysis.Search;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// A2/M3:API 契約測試(鑑權門 + 關鍵讀寫 + 錯誤碼 + 新端點形態)。
public class EndpointsTests
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
        { Content = JsonContent.Create(new { examId, name = "T", seats = new[] { new { seatId = "A07" } } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private static long InsertSuspicious(Db db, string examId, string kind, string source, int score = 50)
    {
        return db.Write<long>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO suspicious_queue (exam_id,seat_id,ts,kind,score,status,refs,source)
                  VALUES (@e,@s,@ts,@k,@sc,'pending','[]',@src)",
                ("@e", examId), ("@s", "A07"), ("@ts", 1750000000.0), ("@k", kind), ("@sc", score), ("@src", source));
            c.ExecuteNonQuery();
            using SqliteCommand idc = conn.Cmd("SELECT last_insert_rowid()");
            return Convert.ToInt64(idc.ExecuteScalar());
        });
    }

    [Fact]
    public async Task 受保护端点_无token返回401()
    {
        using var app = new TestApp(adminAuth: true);
        HttpResponseMessage resp = await app.CreateClient().GetAsync("/api/exams");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task authmode_带采集面模式与按图搜图开关()
    {
        using var app = new TestApp(adminAuth: true);
        JsonElement am = JsonSerializer.Deserialize<JsonElement>(
            await app.CreateClient().GetStringAsync("/api/authmode"));
        Assert.Equal("psk", am.GetProperty("collectAuthMode").GetString());
        Assert.True(am.TryGetProperty("imageSearchEnabled", out JsonElement ise));
        Assert.False(ise.GetBoolean());   // 預設未配嵌入器
    }

    [Fact]
    public async Task 创建考试_出现在列表()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E2");
        JsonElement list = await AdminGet(http, "/api/exams");
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("examId").GetString() == "E2");
    }

    [Fact]
    public async Task 裁决_状态机_confirmed_且非法状态400()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        Db db = app.Services.GetRequiredService<Db>();
        long id = InsertSuspicious(db, "E1", "web_ai", "suspicion");

        var bad = new HttpRequestMessage(HttpMethod.Post, $"/api/suspicious/{id}/decide")
        { Content = JsonContent.Create(new { status = "bogus" }) };
        bad.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(bad)).StatusCode);

        var ok = new HttpRequestMessage(HttpMethod.Post, $"/api/suspicious/{id}/decide")
        { Content = JsonContent.Create(new { status = "confirmed", reviewer = "t" }) };
        ok.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ok)).StatusCode);

        bool confirmed = db.Read(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT status FROM suspicious_queue WHERE id=@id", ("@id", id));
            return (string?)c.ExecuteScalar() == "confirmed";
        });
        Assert.True(confirmed);
    }

    [Fact]
    public async Task 裁决_health项不可裁决_返回400()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        Db db = app.Services.GetRequiredService<Db>();
        long id = InsertSuspicious(db, "E1", "screen_obscured", "health");

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/suspicious/{id}/decide")
        { Content = JsonContent.Create(new { status = "confirmed" }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task health端点_返回health项_且suspicious默认排除()
    {
        using var app = new TestApp(adminAuth: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        Db db = app.Services.GetRequiredService<Db>();
        InsertSuspicious(db, "E1", "screen_obscured", "health", 60);
        InsertSuspicious(db, "E1", "web_ai", "suspicion", 80);

        JsonElement health = await AdminGet(http, "/api/exams/E1/health");
        Assert.Equal(1, health.GetArrayLength());
        Assert.Equal("screen_obscured", health[0].GetProperty("kind").GetString());

        JsonElement susp = await AdminGet(http, "/api/exams/E1/suspicious");
        Assert.Equal(1, susp.GetArrayLength());
        Assert.Equal("web_ai", susp[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task search_image_形状_ok且含results()
    {
        using var app = new TestApp(adminAuth: true, embedMock: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        byte[] webp = System.Text.Encoding.ASCII.GetBytes("RIFF-same-image-bytes-xyz-0001");
        await PostImage(http, "img_aaaa1111", webp, 1);
        await PostImage(http, "img_bbbb2222", webp, 2);
        var embed = app.Services.GetRequiredService<ImageEmbedService>();
        Assert.NotNull(await embed.EmbedImageAsync("img_aaaa1111", default));
        Assert.NotNull(await embed.EmbedImageAsync("img_bbbb2222", default));

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/exams/E1/search-image")
        { Content = JsonContent.Create(new { imageId = "img_aaaa1111", topN = 5 }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        HttpResponseMessage resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement j = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.True(j.GetProperty("results").GetArrayLength() >= 1);
    }

    // 簡化投圖(與 ImageSearchTests.PostImage 同邏輯)
    private static async Task PostImage(HttpClient http, string cid, byte[] webp, long seq)
    {
        const string phash = "aabbccddeeff0011", ts = "1750000000.1";
        string canon = Auth.ImageCanonicalHeaders("E1", "A07", "ag-A07", seq, "event:browser", phash, ts, cid);
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images") { Content = new ByteArrayContent(webp) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Horus-Exam", "E1"); req.Headers.Add("X-Horus-Seat", "A07"); req.Headers.Add("X-Horus-Agent", "ag-A07");
        req.Headers.Add("X-Horus-Seq", seq.ToString()); req.Headers.Add("X-Horus-Trigger", "event:browser");
        req.Headers.Add("X-Horus-Phash", phash); req.Headers.Add("X-Horus-Ts", ts); req.Headers.Add("X-Horus-Sig", sig);
        req.Headers.Add("X-Horus-Image-Id", cid);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }
}
