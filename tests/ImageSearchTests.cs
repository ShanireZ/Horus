using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Analysis.Search;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Horus.Server.Tests;

/// M3 CLIP 按圖搜圖:向量數學 + Mock 嵌入器 + 回應解析(純邏輯單測)。
public class VecMathTests
{
    [Fact]
    public void 余弦_自身为1()
    {
        var a = new float[] { 1, 2, 3, 4 };
        Assert.Equal(1.0, VecMath.Cosine(a, a), 6);
    }

    [Fact]
    public void 余弦_正交为0()
        => Assert.Equal(0.0, VecMath.Cosine(new float[] { 1, 0 }, new float[] { 0, 1 }), 6);

    [Fact]
    public void 余弦_维度不一致_返回负1()
        => Assert.Equal(-1, VecMath.Cosine(new float[] { 1, 2 }, new float[] { 1, 2, 3 }));

    [Fact]
    public void 字节往返_无损()
    {
        var v = new float[] { 0.5f, -1.25f, 3.0f, 42.0f };
        Assert.Equal(v, VecMath.FromBytes(VecMath.ToBytes(v)));
    }

    [Fact]
    public void 单位化_模为1()
    {
        var v = new float[] { 3, 4 };   // |v|=5
        VecMath.Normalize(v);
        Assert.Equal(0.6, v[0], 5);
        Assert.Equal(0.8, v[1], 5);
    }
}

public class MockEmbedderTests
{
    [Fact]
    public async Task 确定性_同字节同向量()
    {
        var e = new MockImageEmbedder(512);
        byte[] img = Encoding.ASCII.GetBytes("hello-image");
        float[]? a = await e.EmbedAsync(img, default);
        float[]? b = await e.EmbedAsync(img, default);
        Assert.NotNull(a);
        Assert.Equal(512, a!.Length);
        Assert.Equal(a, b);                                   // 同位元組 → 同向量
        Assert.True(VecMath.Cosine(a, b!) > 0.999);
    }

    [Fact]
    public async Task 不同字节_不同向量()
    {
        var e = new MockImageEmbedder(512);
        float[]? a = await e.EmbedAsync(Encoding.ASCII.GetBytes("img-A"), default);
        float[]? b = await e.EmbedAsync(Encoding.ASCII.GetBytes("img-B"), default);
        Assert.True(VecMath.Cosine(a!, b!) < 0.5);            // 偽隨機 → 近正交
    }

    [Fact]
    public async Task 单位向量()
    {
        float[]? v = await new MockImageEmbedder(256).EmbedAsync(Encoding.ASCII.GetBytes("x"), default);
        double n = 0; foreach (float f in v!) n += f * (double)f;
        Assert.Equal(1.0, Math.Sqrt(n), 4);
    }
}

public class EmbeddingParseTests
{
    [Fact]
    public void 解析OpenAI响应()
    {
        float[]? v = OpenAiImageEmbedder.ParseEmbedding("{\"data\":[{\"embedding\":[0.1,0.2,0.3]}]}");
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, v);
    }

    [Fact]
    public void 解析畸形_返回null()
    {
        Assert.Null(OpenAiImageEmbedder.ParseEmbedding("not json"));
        Assert.Null(OpenAiImageEmbedder.ParseEmbedding("{\"data\":[]}"));
    }
}

/// M3 本機 ONNX CLIP 預處理:圖像 → CHW 張量 + CLIP 歸一化(純像素·可測·不碰 ONNX 執行環境)。
public class ClipPreprocessTests
{
    [Fact]
    public void 纯红图_张量维度与CLIP归一化正确()
    {
        byte[] png;
        using (var img = new Image<Rgb24>(300, 200, new Rgb24(255, 0, 0)))
        using (var ms = new MemoryStream()) { img.SaveAsPng(ms); png = ms.ToArray(); }

        float[] t = ClipPreprocess.ToTensor(png);
        Assert.Equal(3 * 224 * 224, t.Length);       // CHW 224²
        int plane = 224 * 224;
        Assert.Equal((1f - 0.48145466f) / 0.26862954f, t[0], 3);          // R 通道歸一化
        Assert.Equal((0f - 0.4578275f) / 0.26130258f, t[plane], 3);       // G
        Assert.Equal((0f - 0.40821073f) / 0.27577711f, t[2 * plane], 3);  // B
    }
}

/// M3 本機 ONNX:輸出選擇 + 向量提取(CLIP vision 多輸出場景·純邏輯可測)。
public class OnnxOutputTests
{
    [Fact]
    public void 选输出_显式优先()
        => Assert.Equal("x", OnnxClipEmbedder.SelectOutputName(new[] { "a", "b" }, "x"));

    [Fact]
    public void 选输出_优先embed再pool再首个()
    {
        Assert.Equal("image_embeds", OnnxClipEmbedder.SelectOutputName(new[] { "last_hidden_state", "image_embeds" }, null));
        Assert.Equal("pooler_output", OnnxClipEmbedder.SelectOutputName(new[] { "last_hidden_state", "pooler_output" }, null));
        Assert.Equal("out0", OnnxClipEmbedder.SelectOutputName(new[] { "out0", "out1" }, null));   // 都不含 → 首個
    }

    [Fact]
    public void 取向量_pooled取全部()
    {
        var flat = new float[] { 1, 2, 3, 4 };
        Assert.Equal(flat, OnnxClipEmbedder.ExtractEmbedding(flat, new[] { 1, 4 }));   // [1,4] → 全 4
    }

    [Fact]
    public void 取向量_序列取CLS前D()
    {
        // [1,2,3] 序列:2 個位置 × 3 維 = 6 元;取首位(CLS)前 3 個。
        var flat = new float[] { 10, 11, 12, 20, 21, 22 };
        Assert.Equal(new float[] { 10, 11, 12 }, OnnxClipEmbedder.ExtractEmbedding(flat, new[] { 1, 2, 3 }));
    }
}

public class TopNTests
{
    [Fact]
    public void 排序降序_排除自身()
    {
        var q = new float[] { 1, 0, 0 };
        var corpus = new List<(string, float[])>
        {
            ("self", new float[] { 1, 0, 0 }),
            ("near", new float[] { 0.9f, 0.1f, 0 }),
            ("far", new float[] { 0, 0, 1 }),
        };
        List<(string id, double score)> top = ImageSearchStore.TopN(q, corpus, 5, excludeId: "self");
        Assert.Equal(2, top.Count);           // self 被排除
        Assert.Equal("near", top[0].id);      // 最相似在前
        Assert.Equal("far", top[1].id);
    }

    [Fact]
    public void 余弦下限过滤近正交帧()
    {
        var q = new float[] { 1, 0, 0 };
        var corpus = new List<(string, float[])>
        {
            ("self", new float[] { 1, 0, 0 }),
            ("near", new float[] { 0.9f, 0.1f, 0 }),
            ("orth", new float[] { 0, 1, 0 }),   // 餘弦 0
        };
        List<(string id, double score)> top = ImageSearchStore.TopN(q, corpus, 10, excludeId: "self", cosineFloor: 0.2);
        Assert.DoesNotContain(top, t => t.id == "orth");   // 近正交被濾掉
        Assert.Contains(top, t => t.id == "near");
    }

    [Fact]
    public void topN上界钳制()
    {
        var q = new float[] { 1, 0, 0 };
        var corpus = new List<(string, float[])>();
        for (int i = 0; i < 200; i++) corpus.Add(("n" + i, new float[] { 1, 0, 0 }));
        // cosineFloor=-1 全過;n=1000 → 鉗到 100
        List<(string id, double score)> top = ImageSearchStore.TopN(q, corpus, 1000, excludeId: "n0", cosineFloor: -1);
        Assert.Equal(100, top.Count);
    }
}

/// 端到端:兩張相同位元組圖(不同 clientId)→ 嵌入 → 按圖搜圖回傳餘弦≈1 的相似影格。
public class ImageSearchE2ETests
{
    private static async Task CreateExam(HttpClient http)
        => (await http.PostAsJsonAsync("/api/exams", new { examId = "E1", name = "T", seats = new[] { new { seatId = "A07" } } }))
            .EnsureSuccessStatusCode();

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

    [Fact]
    public async Task 按图搜图_相同帧_余弦近1()
    {
        using var app = new TestApp(embedMock: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        byte[] webp = Encoding.ASCII.GetBytes("RIFF-same-image-bytes-xyz-0001");
        await PostImage(http, "img_aaaa1111", webp, 1);
        await PostImage(http, "img_bbbb2222", webp, 2);   // 相同位元組·不同 clientId → 兩行(跳過 pHash 去重)

        var embed = app.Services.GetRequiredService<ImageEmbedService>();
        Assert.NotNull(await embed.EmbedImageAsync("img_aaaa1111", default));
        Assert.NotNull(await embed.EmbedImageAsync("img_bbbb2222", default));

        HttpResponseMessage resp = await http.PostAsJsonAsync("/api/exams/E1/search-image", new { imageId = "img_aaaa1111", topN = 5 });
        JsonElement j = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(j.GetProperty("ok").GetBoolean());
        JsonElement results = j.GetProperty("results");
        Assert.True(results.GetArrayLength() >= 1);
        Assert.Equal("img_bbbb2222", results[0].GetProperty("imageId").GetString());
        Assert.True(results[0].GetProperty("score").GetDouble() > 0.99);   // 相同位元組 → 相同向量 → cosine≈1
    }

    [Fact]
    public async Task 补嵌_只嵌证据图()
    {
        using var app = new TestApp(embedMock: true);
        HttpClient http = app.CreateClient();
        await CreateExam(http);
        await PostImage(http, "img_ev000001", Encoding.ASCII.GetBytes("evidence-img-1"), 1);

        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn => { using SqliteCommand c = conn.Cmd("UPDATE images SET is_evidence=1 WHERE image_id='img_ev000001'"); c.ExecuteNonQuery(); });

        await app.Services.GetRequiredService<ImageEmbedService>().RunOnceAsync(default);
        Assert.True(app.Services.GetRequiredService<ImageSearchStore>().Has("img_ev000001"));   // 證據圖被嵌入
    }
}
