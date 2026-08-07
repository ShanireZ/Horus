using System.Security.Cryptography;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·S3:授权码换 token → 验 id_token → ECDH 派生 K_sess → 建会话。**Server-Broker**:client_secret 只在本服务器,
/// Agent 从不经手(见 docs/m4-identity-oidc.md §3)。Agent 拿 loopback 收到的 code + PKCE verifier + 自己的 ECDH 公钥来换。
/// 考试派发:Agent **不自报 exam/seat** —— examId 由服务端指派(当前活跃考试),seatId 由 OIDC 身份派生(username)。
public sealed class OidcExchange
{
    private readonly HttpClient _http;
    private readonly OidcTokenValidator _validator;
    private readonly SessionStore _sessions;
    private readonly ServerConfig _cfg;
    private readonly string _clientSecret;
    private readonly Db _db;
    private readonly ILogger<OidcExchange> _log;

    public OidcExchange(HttpClient http, OidcTokenValidator validator, SessionStore sessions, ServerConfig cfg, string clientSecret, Db db, ILogger<OidcExchange> log)
    {
        _http = http; _validator = validator; _sessions = sessions; _cfg = cfg; _clientSecret = clientSecret; _db = db; _log = log;
    }

    public sealed record Request(
        string Code, string CodeVerifier, string RedirectUri, string? Nonce, string AgentEcdhPub,
        string AgentId, string? MachineId);

    public sealed record Result(bool Ok, string? Error, HorusSession? Session, string? ServerEcdhPub);

    public async Task<Result> ExchangeAsync(Request req, double now, CancellationToken ct)
    {
        // 0) 服务端派发考试:无活跃考试即拒(先于 token 交换 —— 不白耗一次性授权码,Agent 可回待命重试)
        ExamDispatch.ActiveExam? exam = ExamDispatch.ResolveActive(_db);
        if (exam is null) return new Result(false, "no_active_exam", null, null);

        // 1) 授权码 → token(client_secret_post·带 PKCE code_verifier)。瞬时 TLS/网络失败自动重试(见 OidcHttp)。
        // ★ access_token 也要留下 —— 姓名与用户名只在 userinfo 拿得到(见 Userinfo 的类注释)。
        string? idToken;
        string? accessToken;
        try
        {
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = req.Code,
                ["redirect_uri"] = req.RedirectUri,
                ["client_id"] = _cfg.OidcClientId!,
                ["client_secret"] = _clientSecret,
                ["code_verifier"] = req.CodeVerifier,
            };
            (bool ok, int status, string body) = await OidcHttp.PostFormWithRetryAsync(_http, _cfg.OidcTokenEndpoint!, fields, _log, ct);
            if (!ok)
            {
                _log.LogWarning("OIDC token 端点非 200:{Status}", status);
                return new Result(false, "token_endpoint_error", null, null);
            }
            using JsonDocument doc = JsonDocument.Parse(body);
            idToken = doc.RootElement.TryGetProperty("id_token", out JsonElement it) ? it.GetString() : null;
            accessToken = doc.RootElement.TryGetProperty("access_token", out JsonElement at) ? at.GetString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OIDC token 交换失败(已重试)");
            return new Result(false, "token_exchange_failed", null, null);
        }
        if (string.IsNullOrEmpty(idToken)) return new Result(false, "no_id_token", null, null);
        if (string.IsNullOrEmpty(accessToken)) return new Result(false, "no_access_token", null, null);

        // 2) 离线验 id_token(签名/iss/aud/exp/nonce)→ 只得到 sub
        OidcSubject subject;
        try { subject = _validator.Validate(idToken!, req.Nonce, now); }
        catch (OidcValidationException ex) { _log.LogWarning("id_token 验证失败:{Msg}", ex.Message); return new Result(false, "invalid_id_token", null, null); }

        // 2.5) 取 userinfo 补齐姓名与用户名。★ **这一步不能省** —— 贝塔通的 id_token 里没有这两项,
        //      少了它座位号会对每个人静默回退成 sub(见 Userinfo 的类注释)。取不到即登录失败,不放行空身份。
        OidcClaims claims;
        try { claims = await Userinfo.FetchAsync(_http, _cfg.OidcUserinfoEndpoint!, accessToken!, subject, _log, ct); }
        catch (OidcValidationException ex) { _log.LogWarning("userinfo 获取失败:{Msg}", ex.Message); return new Result(false, "userinfo_failed", null, null); }

        // 3) ECDH:服务器出一把临时公钥,与 Agent 公钥派生同一 K_sess(私钥不过网 → 闭合 A1/A2)
        byte[] kSess;
        string serverPub;
        try
        {
            using ECDiffieHellman serverKey = SessionCrypto.NewEphemeralKey();
            serverPub = SessionCrypto.ExportPublicKeyB64(serverKey);
            kSess = SessionCrypto.DeriveKey(serverKey, req.AgentEcdhPub);
        }
        catch (Exception) { return new Result(false, "bad_agent_pubkey", null, null); }

        // 4) 建会话(绑定身份到 exam/seat/agent)。seat := username(不安全/为空回退 sub)—— 身份即座位,学员无法自报。
        string seatId = ExamDispatch.SeatFrom(claims);
        HorusSession session = _sessions.Create(exam.ExamId, seatId, req.AgentId, req.MachineId, claims, kSess, now, _cfg.OidcSessionMinutes);
        _log.LogInformation("OIDC 会话建立 exam={Exam} seat={Seat} sub={Sub} user={User}", exam.ExamId, seatId, claims.Sub, claims.Username);
        return new Result(true, null, session, serverPub);
    }
}
