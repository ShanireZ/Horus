using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:监考员看板 **OIDC 登录**(wentian dashboard web client·标准服务器端授权码 + PKCE 流)。
/// 与采集面 <see cref="OidcExchange"/> 的差异:用 dashboard client(aud 独立、**归属 `horus-admin` 平台**)、建 <see cref="AdminSession"/>(无 ECDH/无 exam-seat 绑定)。
/// ★ 准入不再由 claim 判据决定,而由身份中心的平台开关决定(贝塔通 P83),见 <see cref="CompleteAsync"/> 里的说明。
/// 拓扑 R5:回调走 https(远端监考工作站可达),client_secret 只在服务器。见 docs/m4-identity-oidc.md §10.3。
public sealed class AdminOidcFlow
{
    private readonly HttpClient _http;
    private readonly OidcTokenValidator _validator;   // aud = dashboard client_id
    private readonly AdminSessionStore _sessions;
    private readonly ServerConfig _cfg;
    private readonly string _clientSecret;
    private readonly ILogger<AdminOidcFlow> _log;

    // 登录 pending:state → (PKCE verifier, nonce, 创建时刻)。单次使用 + 10min 过期,防重放/CSRF。
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private const double PendingTtlSeconds = 600;

    private readonly record struct Pending(string Verifier, string Nonce, double CreatedAt);

    public AdminOidcFlow(HttpClient http, OidcTokenValidator dashboardValidator, AdminSessionStore sessions,
        ServerConfig cfg, string clientSecret, ILogger<AdminOidcFlow> log)
    {
        _http = http; _validator = dashboardValidator; _sessions = sessions; _cfg = cfg; _clientSecret = clientSecret; _log = log;
    }

    /// 起登录:生成 state+nonce+PKCE,存 pending,返回要重定向到的 wentian 授权 URL。
    public string BeginLogin(double now)
    {
        Prune(now);
        string state = RandUrl(24);
        string nonce = RandUrl(24);
        string verifier = RandUrl(48);
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        _pending[state] = new Pending(verifier, nonce, now);

        var q = new Dictionary<string, string>
        {
            ["client_id"] = _cfg.OidcDashboardClientId!,
            ["redirect_uri"] = _cfg.OidcDashboardRedirectUri!,
            ["response_type"] = "code",
            // ★ P81:只要 openid + profile(真实姓名)。**不要请求 `horus_profile`** —— 那是 wentian 的自定义 scope,
            //   贝塔通不登记它;请求了会被裁掉,而「请求成功但 claims 是空的」正是最难查的形态。
            ["scope"] = "openid profile",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
        };
        string query = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return _cfg.OidcAuthorizeEndpoint!.TrimEnd('/') + "?" + query;
    }

    public sealed record Result(bool Ok, string? Error, AdminSession? Session);

    /// 完成登录:校验 state → 换 token(dashboard secret + PKCE)→ 验 id_token(aud=dashboard·nonce)→ 建管理会话。
    /// ★ 「是不是监考员」由身份中心在**授权阶段**回答(平台 `horus-admin`),走到这里就说明已经是了。
    public async Task<Result> CompleteAsync(string code, string state, double now, CancellationToken ct)
    {
        Prune(now);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return new Result(false, "missing_code_or_state", null);
        if (!_pending.TryRemove(state, out Pending p)) return new Result(false, "unknown_state", null);   // 单次使用·防重放/CSRF
        if (now - p.CreatedAt > PendingTtlSeconds) return new Result(false, "state_expired", null);

        // 换 token(client_secret_post + PKCE code_verifier)。瞬时 TLS/网络失败自动重试(见 OidcHttp)。
        string? idToken;
        try
        {
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _cfg.OidcDashboardRedirectUri!,
                ["client_id"] = _cfg.OidcDashboardClientId!,
                ["client_secret"] = _clientSecret,
                ["code_verifier"] = p.Verifier,
            };
            (bool ok, int status, string body) = await OidcHttp.PostFormWithRetryAsync(_http, _cfg.OidcTokenEndpoint!, fields, _log, ct);
            if (!ok)
            {
                _log.LogWarning("监考员 OIDC token 端点非 200:{Status}", status);
                return new Result(false, "token_endpoint_error", null);
            }
            using JsonDocument doc = JsonDocument.Parse(body);
            idToken = doc.RootElement.TryGetProperty("id_token", out JsonElement it) ? it.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "监考员 OIDC token 交换失败(已重试)");
            return new Result(false, "token_exchange_failed", null);
        }
        if (string.IsNullOrEmpty(idToken)) return new Result(false, "no_id_token", null);

        // 离线验签(dashboard aud·nonce)→ claims
        OidcClaims claims;
        try { claims = _validator.Validate(idToken!, p.Nonce, now); }
        catch (OidcValidationException ex) { _log.LogWarning("监考员 id_token 验证失败:{Msg}", ex.Message); return new Result(false, "invalid_id_token", null); }

        // ★★ **准入判据已上移到身份中心**(贝塔通 P83)。
        //
        // 此前这里判 `claims.UserType == "elder"`,而那个 claim 来自 wentian 的自定义 scope
        // `horus_profile` —— 贝塔通 P81 停发它之后,那条判据就不存在了。
        //
        // 现在:看板客户端 `horus-dashboard` 归属平台 **`horus-admin`**,采集端 `horus-client`
        // 归属平台 `horus`。**没有 `horus-admin` 权限的人在贝塔通的授权阶段就被拒**
        // (其 §3.2:未开通平台权限的账号在授权阶段拒,不是登录后再拒),因此**根本换不到 code**,
        // 走不到这一行。判据只有一处、在身份中心,这正是把身份抽离出去的本意。
        //
        // ★ **这条改动与 IdP 侧的平台拆分是一个原子事实**:若把 IdP 换回「采集端与看板同属一个
        //   平台」的形态(如旧 wentian),这里就没有任何东西拦着考生进看板 —— 考生为了跑采集端
        //   本来就有那个平台的权限。**换 IdP 前先确认它按平台把关。**
        AdminSession s = _sessions.Create(claims, now, _cfg.AdminSessionMinutes);
        _log.LogInformation("监考员登录成功 sub={Sub}", claims.Sub);
        return new Result(true, null, s);
    }

    private void Prune(double now)
    {
        foreach (KeyValuePair<string, Pending> kv in _pending)
            if (now - kv.Value.CreatedAt > PendingTtlSeconds) _pending.TryRemove(kv.Key, out _);
    }

    private static string RandUrl(int bytes) => B64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
