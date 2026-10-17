using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Horus.Server.Config;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·RBAC·S8:監考員看板 **OIDC 登入**(wentian dashboard web client·標準伺服器端授權碼 + PKCE 流)。
/// 與採集面 <see cref="OidcExchange"/> 的差異:用 dashboard client(aud 獨立、**歸屬 `horus-admin` 平台**)、建 <see cref="AdminSession"/>(無 ECDH/無 exam-seat 綁定)。
/// ★ 存取資格不再由 claim 判據決定,而由身分中心的平台開關決定(貝塔通 P83),見 <see cref="CompleteAsync"/> 裡的說明。
/// 拓撲 R5:回呼走 https(遠端監考工作站可達),client_secret 只在伺服器。見 docs/m4-identity-oidc.md §10.3。
public sealed class AdminOidcFlow
{
    private readonly HttpClient _http;
    private readonly OidcTokenValidator _validator;   // aud = dashboard client_id
    private readonly AdminSessionStore _sessions;
    private readonly ServerConfig _cfg;
    private readonly string _clientSecret;
    private readonly ILogger<AdminOidcFlow> _log;

    // 登入 pending:state → (PKCE verifier, nonce, 建立時刻)。單次使用 + 10min 過期,防重放/CSRF。
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private const double PendingTtlSeconds = 600;

    private readonly record struct Pending(string Verifier, string Nonce, double CreatedAt);

    public AdminOidcFlow(HttpClient http, OidcTokenValidator dashboardValidator, AdminSessionStore sessions,
        ServerConfig cfg, string clientSecret, ILogger<AdminOidcFlow> log)
    {
        _http = http; _validator = dashboardValidator; _sessions = sessions; _cfg = cfg; _clientSecret = clientSecret; _log = log;
    }

    /// 起登入:生成 state+nonce+PKCE,存 pending,回傳要重定向到的 wentian 授權 URL。
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
            // ★ P81:只要 openid + profile(真實姓名)。**不要請求 `horus_profile`** —— 那是 wentian 的自訂 scope,
            //   貝塔通不登記它;請求了會被裁掉,而「請求成功但 claims 是空的」正是最難查的形態。
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

    /// 完成登入:檢核 state → 換 token(dashboard secret + PKCE)→ 驗 id_token(aud=dashboard·nonce)→ 建管理工作階段。
    /// ★ 「是不是監考員」由身分中心在**授權階段**回答(平台 `horus-admin`),走到這裡就說明已經是了。
    public async Task<Result> CompleteAsync(string code, string state, double now, CancellationToken ct)
    {
        Prune(now);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return new Result(false, "missing_code_or_state", null);
        if (!_pending.TryRemove(state, out Pending p)) return new Result(false, "unknown_state", null);   // 單次使用·防重放/CSRF
        if (now - p.CreatedAt > PendingTtlSeconds) return new Result(false, "state_expired", null);

        // 換 token(client_secret_post + PKCE code_verifier)。瞬時 TLS/網路失敗自動重試(見 OidcHttp)。
        // ★ access_token 也要留下 —— 真實姓名只在 userinfo 拿得到(見 Userinfo 的類註解)。
        string? idToken;
        string? accessToken;
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
            accessToken = doc.RootElement.TryGetProperty("access_token", out JsonElement at) ? at.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "监考员 OIDC token 交换失败(已重试)");
            return new Result(false, "token_exchange_failed", null);
        }
        if (string.IsNullOrEmpty(idToken)) return new Result(false, "no_id_token", null);
        if (string.IsNullOrEmpty(accessToken)) return new Result(false, "no_access_token", null);

        // 離線驗籤(dashboard aud·nonce)→ 只得到 sub
        OidcSubject subject;
        try { subject = _validator.Validate(idToken!, p.Nonce, now); }
        catch (OidcValidationException ex) { _log.LogWarning("监考员 id_token 验证失败:{Msg}", ex.Message); return new Result(false, "invalid_id_token", null); }

        // 取 userinfo 補齊真實姓名。★ 貝塔通的 id_token 裡沒有它 —— 少了這一步,看板與取證的
        //   「監考員是誰」會恆為空,而登入本身照常成功(見 Userinfo 的類註解)。
        OidcClaims claims;
        try { claims = await Userinfo.FetchAsync(_http, _cfg.OidcUserinfoEndpoint!, accessToken!, subject, _log, ct); }
        catch (OidcValidationException ex) { _log.LogWarning("监考员 userinfo 获取失败:{Msg}", ex.Message); return new Result(false, "userinfo_failed", null); }

        // ★★ **存取資格判據已上移到身分中心**(貝塔通 P83)。
        //
        // 此前這裡判 `claims.UserType == "elder"`,而那個 claim 來自 wentian 的自訂 scope
        // `horus_profile` —— 貝塔通 P81 停發它之後,那條判據就不存在了。
        //
        // 現在:看板用戶端 `horus-dashboard` 歸屬平台 **`horus-admin`**,採集端 `horus-client`
        // 歸屬平台 `horus`。**沒有 `horus-admin` 權限的人在貝塔通的授權階段就被拒**
        // (其 §3.2:未開通平台權限的帳號在授權階段拒,不是登入後再拒),因此**根本換不到 code**,
        // 走不到這一行。判據只有一處、在身分中心,這正是把身分抽離出去的本意。
        //
        // ★ **這條改動與 IdP 側的平台拆分是一個原子事實**:若把 IdP 換回「採集端與看板同屬一個
        //   平台」的形態(如舊 wentian),這裡就沒有任何東西攔著考生進看板 —— 考生為了跑採集端
        //   本來就有那個平台的權限。**換 IdP 前先確認它按平台把關。**
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
