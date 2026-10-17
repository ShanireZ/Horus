using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// OIDC 與貝塔通通訊的 HTTP 小工具:對 token 端點 POST、userinfo 端點 GET 加**瞬時失敗重試**。
/// 真機曾遇:瀏覽器能到 betaoi.cn,但伺服器端 .NET HttpClient 去 POST /oauth/token 偶發
/// 「SSL handshake 收到 0 位元組 / unexpected EOF」(殺毒 TLS 攔截 / Cloudflare bot 防護 / 網路抖動)。
/// 對 TLS/網路類異常與 502/503/504 重試若干次(每次新建 FormUrlEncodedContent → 新連接),扛過偶發抖動;
/// 若是穩定攔截(殺毒/WAF)則重試也救不了,須從環境層解決(見 docs/部署与真机验收.md 故障排查)。
public static class OidcHttp
{
    /// 結果:ok=2xx;status=HTTP 狀態碼;body=回應體。僅在**重試耗盡後**才拋出最後一次的瞬時異常(呼叫方 catch)。
    public static async Task<(bool ok, int status, string body)> PostFormWithRetryAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string> fields,
        ILogger log, CancellationToken ct, int maxAttempts = 3)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // 每次新建 content:FormUrlEncodedContent 只能發一次;失敗換新連接重發。
                using var form = new FormUrlEncodedContent(fields);
                using HttpResponseMessage resp = await http.PostAsync(url, form, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int status = (int)resp.StatusCode;

                // 閘道瞬時錯誤:重試(非末次)。
                if (status is 502 or 503 or 504 && attempt < maxAttempts)
                {
                    log.LogWarning("OIDC token 端点 {Status}(网关瞬时),第 {Attempt}/{Max} 次将重试", status, attempt, maxAttempts);
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return (resp.IsSuccessStatusCode, status, body);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < maxAttempts)
            {
                last = ex;
                log.LogWarning("OIDC token 交换第 {Attempt}/{Max} 次瞬时失败,重试:{Msg}", attempt, maxAttempts, ex.Message);
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }
        // 末次仍失敗:把最後一次異常拋給呼叫方(其 catch 落 token_exchange_failed)。
        throw last ?? new HttpRequestException("OIDC token 交换失败(重试耗尽)");
    }

    /// userinfo 端點 GET(Bearer 訪問權杖),重試口徑與上面的 token POST 完全相同。
    /// ★ 共用同一套判據是有意的:同一條連線路徑上的同一類抖動(殺毒 TLS 攔截 / WAF / 網路),
    ///   兩處各寫一套遲早會分家,而分家時表現是「換 token 扛得過、取 userinfo 扛不過」這種極難認的偏科。
    public static async Task<(bool ok, int status, string body)> GetWithBearerAndRetryAsync(
        HttpClient http, string url, string accessToken,
        ILogger log, CancellationToken ct, int maxAttempts = 3)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // 每次新建 request:HttpRequestMessage 只能發一次;失敗換新連接重發。
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int status = (int)resp.StatusCode;

                if (status is 502 or 503 or 504 && attempt < maxAttempts)
                {
                    log.LogWarning("OIDC userinfo 端点 {Status}(网关瞬时),第 {Attempt}/{Max} 次将重试", status, attempt, maxAttempts);
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return (resp.IsSuccessStatusCode, status, body);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < maxAttempts)
            {
                last = ex;
                log.LogWarning("OIDC userinfo 第 {Attempt}/{Max} 次瞬时失败,重试:{Msg}", attempt, maxAttempts, ex.Message);
                await BackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new HttpRequestException("OIDC userinfo 请求失败(重试耗尽)");
    }

    /// TLS/網路類瞬時異常(可重試);使用者主動取消不算。
    private static bool IsTransient(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested
           && (ex is HttpRequestException or IOException
               || (ex is TaskCanceledException && !ct.IsCancellationRequested));   // HttpClient 超時拋 TaskCanceledException

    private static Task BackoffAsync(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);   // 300ms / 600ms 遞增,給瞬時故障一點恢復時間
}
