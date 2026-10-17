using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Horus.Contracts;
using Horus.Server.Analysis.Vision;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Horus.Server.Tests;

/// M5:視覺判定映射 + 視覺分析服務寫入資料庫連線路徑。
public class VisionTests
{
    [Theory]
    [InlineData("web_ai", "web_ai")]
    [InlineData("search", "search")]
    [InlineData("ide_plugin", "ide_plugin_suspect")]
    [InlineData("remote_tool", "remote_tool")]
    [InlineData("other", "suspect")]
    [InlineData("none", "suspect")]
    public void VisionVerdict_Kind映射(string category, string kind)
        => Assert.Equal(kind, new VisionVerdict { Category = category }.Kind());

    [Fact]
    public void VisionVerdict_可疑标记()
    {
        Assert.True(new VisionVerdict { Suspicious = true, Category = "web_ai" }.Suspicious);
        Assert.False(new VisionVerdict { Suspicious = false, Category = "none" }.Suspicious);
    }

    [Fact]
    public async Task 视觉分析服务_证据图经mock分析_落ocr与终态()
    {
        using var app = new TestApp(adminAuth: true, visionMock: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http, "E1");
        byte[] png = MakePng();
        await PostImage(http, "img_v000001", png, 1);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using SqliteCommand c = conn.Cmd("UPDATE images SET is_evidence=1 WHERE image_id='img_v000001'"); c.ExecuteNonQuery(); });

        var svc = app.Services.GetRequiredService<VisionAnalysisService>();
        svc.Enqueue("img_v000001");

        // 輪詢等待後台分析寫入資料庫(最長 ~4s)
        bool ok = false;
        for (int i = 0; i < 40; i++)
        {
            ok = db.Read(conn =>
            {
                using SqliteCommand c = conn.Cmd("SELECT 1 FROM ocr_results WHERE image_id='img_v000001'");
                return c.ExecuteScalar() is not null;
            });
            if (ok) break;
            await Task.Delay(100);
        }
        Assert.True(ok, "ocr_results 应在超时前落库");

        int state = db.Read(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT analysis_state FROM images WHERE image_id='img_v000001'");
            return Convert.ToInt32(c.ExecuteScalar());
        });
        Assert.Equal(1, state);   // 終結態(分析完成)
    }

    // ---- helpers ----
    private static async Task CreateExam(HttpClient http, string examId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/exams")
        { Content = JsonContent.Create(new { examId, name = "T", seats = new[] { new { seatId = "A07" } } }) };
        req.Headers.Add("X-Horus-Admin", TestApp.AdminToken);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }
    private static byte[] MakePng()
    {
        using var img = new Image<Rgb24>(64, 48, new Rgb24(10, 20, 30));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }
    private static async Task PostImage(HttpClient http, string cid, byte[] webp, long seq)
    {
        const string phash = "aabbccddeeff0011", ts = "1750000000.1";
        string canon = Auth.ImageCanonicalHeaders("E1", "A07", "ag-A07", seq, "event:browser", phash, ts, cid);
        string sig = Auth.ImageSig(TestApp.Psk, canon, webp);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/images") { Content = new ByteArrayContent(webp) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
        req.Headers.Add("X-Horus-Exam", "E1"); req.Headers.Add("X-Horus-Seat", "A07"); req.Headers.Add("X-Horus-Agent", "ag-A07");
        req.Headers.Add("X-Horus-Seq", seq.ToString()); req.Headers.Add("X-Horus-Trigger", "event:browser");
        req.Headers.Add("X-Horus-Phash", phash); req.Headers.Add("X-Horus-Ts", ts); req.Headers.Add("X-Horus-Sig", sig);
        req.Headers.Add("X-Horus-Image-Id", cid);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }
}
