using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// OIDC 与贝塔通通信的 HTTP 小工具:对 token 端点 POST、userinfo 端点 GET 加**瞬时失败重试**。
/// 真机曾遇:浏览器能到 betaoi.cn,但服务端 .NET HttpClient 去 POST /oauth/token 偶发
/// 「SSL handshake 收到 0 字节 / unexpected EOF」(杀毒 TLS 拦截 / Cloudflare bot 防护 / 网络抖动)。
/// 对 TLS/网络类异常与 502/503/504 重试若干次(每次新建 FormUrlEncodedContent → 新连接),扛过偶发抖动;
/// 若是稳定拦截(杀毒/WAF)则重试也救不了,须从环境层解决(见 docs/部署与真机验收.md 故障排查)。
public static class OidcHttp
{
    /// 结果:ok=2xx;status=HTTP 状态码;body=响应体。仅在**重试耗尽后**才抛出最后一次的瞬时异常(调用方 catch)。
    public static async Task<(bool ok, int status, string body)> PostFormWithRetryAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string> fields,
        ILogger log, CancellationToken ct, int maxAttempts = 3)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // 每次新建 content:FormUrlEncodedContent 只能发一次;失败换新连接重发。
                using var form = new FormUrlEncodedContent(fields);
                using HttpResponseMessage resp = await http.PostAsync(url, form, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int status = (int)resp.StatusCode;

                // 网关瞬时错误:重试(非末次)。
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
        // 末次仍失败:把最后一次异常抛给调用方(其 catch 落 token_exchange_failed)。
        throw last ?? new HttpRequestException("OIDC token 交换失败(重试耗尽)");
    }

    /// userinfo 端点 GET(Bearer 访问令牌),重试口径与上面的 token POST 完全相同。
    /// ★ 共用同一套判据是有意的:同一条链路上的同一类抖动(杀毒 TLS 拦截 / WAF / 网络),
    ///   两处各写一套迟早会分家,而分家时表现是「换 token 扛得过、取 userinfo 扛不过」这种极难认的偏科。
    public static async Task<(bool ok, int status, string body)> GetWithBearerAndRetryAsync(
        HttpClient http, string url, string accessToken,
        ILogger log, CancellationToken ct, int maxAttempts = 3)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // 每次新建 request:HttpRequestMessage 只能发一次;失败换新连接重发。
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

    /// TLS/网络类瞬时异常(可重试);用户主动取消不算。
    private static bool IsTransient(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested
           && (ex is HttpRequestException or IOException
               || (ex is TaskCanceledException && !ct.IsCancellationRequested));   // HttpClient 超时抛 TaskCanceledException

    private static Task BackoffAsync(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);   // 300ms / 600ms 递增,给瞬时故障一点恢复时间
}
