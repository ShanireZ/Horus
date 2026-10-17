using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Analysis.Vision;

/// OpenAI 相容 /chat/completions 視覺 adapter。**DeepSeek-V4 / 小米 MiMo-V2.5(vLLM)/ Qwen-VL / GLM-4V 皆 OpenAI 相容**
/// → 換供應商 = 換 baseUrl + model + key 三個 config,程式碼零改。圖以 data:image/webp;base64 內聯;要求模型只回 JSON。
/// 任何失敗回傳 null(fail-open:分析失敗不阻斷採集,交人工/後續復看),並記 Warning 供維運發現端點異常。
public sealed class OpenAiCompatibleVisionAnalyzer : IVisionAnalyzer
{
    private const string SystemPrompt =
        "你是考试监考图像审查助手。判断这张考试机屏幕截图是否显示作弊迹象:AI 对话界面(ChatGPT/DeepSeek/豆包/Kimi/文心 等)、" +
        "搜索引擎结果页、IDE 里的 AI 代码补全(灰色幽灵文本或整段凭空出现的代码)、远程协助/远控工具。只输出 JSON,不要任何解释。";

    private const string UserPrompt =
        "分析这张截图,只返回如下 JSON(不要代码块围栏):" +
        "{\"suspicious\":true/false,\"category\":\"web_ai|search|ide_plugin|remote_tool|other|none\"," +
        "\"confidence\":0-100,\"hits\":[\"标签\"],\"evidence\":\"一句话中文证据\",\"text\":\"截图里的关键可见文字(可空)\"}";

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly ILogger? _log;

    public OpenAiCompatibleVisionAnalyzer(HttpClient http, string baseUrl, string model, string apiKey, ILogger? log = null)
    {
        _http = http;
        _url = baseUrl.TrimEnd('/') + "/chat/completions";
        _model = model;
        _apiKey = apiKey;
        _log = log;
    }

    public string Engine => "openai:" + _model;

    public bool SendsOffNetwork => true;   // 送雲端點 → 送前必須剝中繼資料、派生失敗寧跳過不洩原圖(§5)

    public async Task<VisionVerdict?> AnalyzeAsync(byte[] imageBytes, VisionContext ctx, CancellationToken ct)
    {
        try
        {
            string dataUri = "data:image/webp;base64," + Convert.ToBase64String(imageBytes);
            var body = new
            {
                model = _model,
                temperature = 0,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = new object[]
                    {
                        new { type = "text", text = UserPrompt },
                        new { type = "image_url", image_url = new { url = dataUri } },
                    } },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.LogWarning("视觉端点非 200:{Status} image={Image}", (int)resp.StatusCode, ctx.ImageId);
                return null;
            }
            string json = await resp.Content.ReadAsStringAsync(ct);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement choice0 = doc.RootElement.GetProperty("choices")[0];
            if (choice0.TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind == JsonValueKind.String
                && fr.GetString() == "length")
                _log?.LogWarning("视觉端点返回被截断(finish_reason=length),JSON 可能不完整 image={Image}", ctx.ImageId);

            string? content = choice0.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) { _log?.LogWarning("视觉端点返回空 content image={Image}", ctx.ImageId); return null; }
            VisionVerdict? v = Parse(content!);
            if (v is null) _log?.LogWarning("视觉返回无法解析为 JSON image={Image}", ctx.ImageId);
            return v;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { _log?.LogWarning(ex, "视觉端点调用失败 image={Image}", ctx.ImageId); return null; }
    }

    /// 解析模型回傳的 JSON。容忍 ```json 圍欄 / 前後噪聲:截取首個 '{' 到末個 '}'。
    /// confidence/suspicious 容忍供應商回傳的多種表述(整數/小數/字串數字/字串布爾/0-1),避免漏報。
    public static VisionVerdict? Parse(string content)
    {
        int a = content.IndexOf('{'), b = content.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            using JsonDocument d = JsonDocument.Parse(content[a..(b + 1)]);
            JsonElement r = d.RootElement;
            return new VisionVerdict
            {
                Suspicious = ParseBool(r, "suspicious"),
                Category = Str(r, "category") ?? "none",
                Confidence = ParseConfidence(r, "confidence"),
                Hits = StrArr(r, "hits"),
                Evidence = Str(r, "evidence") ?? "",
                Text = Str(r, "text"),
            };
        }
        catch { return null; }
    }

    /// suspicious 容忍:true / "true"/"yes"/"1" / 數字非 0。
    private static bool ParseBool(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out JsonElement e)) return false;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.TryGetDouble(out double d) && d != 0,
            JsonValueKind.String => e.GetString() is string s &&
                (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s == "1"),
            _ => false,
        };
    }

    /// confidence 容忍整數 / 小數(95.0)/ 字串("95")/ 0-1 機率(0.95→95),統一鉗到 0-100 整數,避免誤歸零致漏報。
    /// 0-1 機率放大邊界(BUG#2 修復):用原始 token 是否含小數點區分「整數 1(=1%,保持 F9 不放大以防假陽性)」
    /// 與「1.0(=100% 歸一化機率,放大防漏報)」—— JSON 解析後二者皆為 double 1.0,無法靠值區分,故看原始文字。
    private static int ParseConfidence(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out JsonElement e)) return 0;
        double d;
        bool hasDecimal;
        if (e.ValueKind == JsonValueKind.Number)
        {
            if (!e.TryGetDouble(out d)) return 0;
            hasDecimal = e.GetRawText().IndexOf('.') >= 0;   // "1" vs "1.0" 區分依據
        }
        else if (e.ValueKind == JsonValueKind.String &&
                 double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
        {
            d = sd;
            hasDecimal = (e.GetString() ?? "").IndexOf('.') >= 0;
        }
        else return 0;
        // 嚴格 (0,1) 機率 → 放大成百分比;恰好 1.0 且為小數(token 含 '.')→ 視為歸一化機率放大成 100(修復漏報);
        // 整數 1(無小數點)保持 1(=1%,F9:避免把"1%"誤放大成 100% 致假陽性)。
        if (d > 0 && (d < 1 || (d == 1.0 && hasDecimal))) d *= 100;
        return (int)Math.Clamp(Math.Round(d), 0, 100);
    }

    private static string? Str(JsonElement o, string k)
        => o.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string[] StrArr(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out JsonElement e) || e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (JsonElement it in e.EnumerateArray())
            if (it.ValueKind == JsonValueKind.String) list.Add(it.GetString()!);
        return list.ToArray();
    }
}
