using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:监考员看板 **OIDC 登录**(cpplearn dashboard web client·标准服务器端授权码 + PKCE 流)。
/// 与采集面 <see cref="OidcExchange"/> 的差异:用 dashboard client(aud 独立)、**要求 user_type='elder'**、建 <see cref="AdminSession"/>(无 ECDH/无 exam-seat 绑定)。
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

    /// 起登录:生成 state+nonce+PKCE,存 pending,返回要重定向到的 cpplearn 授权 URL。
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
            ["scope"] = "openid horus_profile",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
        };
        string query = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return _cfg.OidcAuthorizeEndpoint!.TrimEnd('/') + "?" + query;
    }

    public sealed record Result(bool Ok, string? Error, AdminSession? Session);

    /// 完成登录:校验 state → 换 token(dashboard secret + PKCE)→ 验 id_token(aud=dashboard·nonce)→ **须 elder** → 建管理会话。
    public async Task<Result> CompleteAsync(string code, string state, double now, CancellationToken ct)
    {
        Prune(now);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return new Result(false, "missing_code_or_state", null);
        if (!_pending.TryRemove(state, out Pending p)) return new Result(false, "unknown_state", null);   // 单次使用·防重放/CSRF
        if (now - p.CreatedAt > PendingTtlSeconds) return new Result(false, "state_expired", null);

        // 换 token(client_secret_post + PKCE code_verifier)
        string? idToken;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _cfg.OidcDashboardRedirectUri!,
                ["client_id"] = _cfg.OidcDashboardClientId!,
                ["client_secret"] = _clientSecret,
                ["code_verifier"] = p.Verifier,
            });
            using HttpResponseMessage resp = await _http.PostAsync(_cfg.OidcTokenEndpoint!, form, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("监考员 OIDC token 端点非 200:{Status}", (int)resp.StatusCode);
                return new Result(false, "token_endpoint_error", null);
            }
            using JsonDocument doc = JsonDocument.Parse(body);
            idToken = doc.RootElement.TryGetProperty("id_token", out JsonElement it) ? it.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "监考员 OIDC token 交换失败");
            return new Result(false, "token_exchange_failed", null);
        }
        if (string.IsNullOrEmpty(idToken)) return new Result(false, "no_id_token", null);

        // 离线验签(dashboard aud·nonce)→ claims
        OidcClaims claims;
        try { claims = _validator.Validate(idToken!, p.Nonce, now); }
        catch (OidcValidationException ex) { _log.LogWarning("监考员 id_token 验证失败:{Msg}", ex.Message); return new Result(false, "invalid_id_token", null); }

        // **RBAC 核心:仅长老(elder)可进管理端**;弟子(考生)登录被拒,不建会话。
        if (!string.Equals(claims.UserType, "elder", StringComparison.Ordinal))
        {
            _log.LogWarning("非监考员尝试登录管理端:sub={Sub} user={User} type={Type} → 拒", claims.Sub, claims.Username, claims.UserType);
            return new Result(false, "not_proctor", null);
        }

        AdminSession s = _sessions.Create(claims, now, _cfg.AdminSessionMinutes);
        _log.LogInformation("监考员登录成功 sub={Sub} user={User}", claims.Sub, claims.Username);
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
