using System.Security.Cryptography;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// M4·S3:授權碼換 token → 驗 id_token → ECDH 派生 K_sess → 建工作階段。**Server-Broker**:client_secret 只在本伺服器,
/// Agent 從不經手(見 docs/m4-identity-oidc.md §3)。Agent 拿 loopback 收到的 code + PKCE verifier + 自己的 ECDH 公鑰來換。
/// 考試派發:Agent **不自報 exam/seat** —— examId 由伺服器端指派(當前活躍考試),seatId 由 OIDC 身分派生(username)。
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
        // 0) 伺服器端派發考試:無活躍考試即拒(先於 token 交換 —— 不白耗一次性授權碼,Agent 可回待命重試)
        ExamDispatch.ActiveExam? exam = ExamDispatch.ResolveActive(_db);
        if (exam is null) return new Result(false, "no_active_exam", null, null);

        // 1) 授權碼 → token(client_secret_post·帶 PKCE code_verifier)。瞬時 TLS/網路失敗自動重試(見 OidcHttp)。
        // ★ access_token 也要留下 —— 姓名與使用者名只在 userinfo 拿得到(見 Userinfo 的類註解)。
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

        // 2) 離線驗 id_token(簽名/iss/aud/exp/nonce)→ 只得到 sub
        OidcSubject subject;
        try { subject = _validator.Validate(idToken!, req.Nonce, now); }
        catch (OidcValidationException ex) { _log.LogWarning("id_token 验证失败:{Msg}", ex.Message); return new Result(false, "invalid_id_token", null, null); }

        // 2.5) 取 userinfo 補齊姓名與使用者名。★ **這一步不能省** —— 貝塔通的 id_token 裡沒有這兩項,
        //      少了它座位號會對每個人靜默退回成 sub(見 Userinfo 的類註解)。取不到即登入失敗,不放行空身分。
        OidcClaims claims;
        try { claims = await Userinfo.FetchAsync(_http, _cfg.OidcUserinfoEndpoint!, accessToken!, subject, _log, ct); }
        catch (OidcValidationException ex) { _log.LogWarning("userinfo 获取失败:{Msg}", ex.Message); return new Result(false, "userinfo_failed", null, null); }

        // 3) ECDH:伺服器出一把臨時公鑰,與 Agent 公鑰派生同一 K_sess(私鑰不過網 → 閉合 A1/A2)
        byte[] kSess;
        string serverPub;
        try
        {
            using ECDiffieHellman serverKey = SessionCrypto.NewEphemeralKey();
            serverPub = SessionCrypto.ExportPublicKeyB64(serverKey);
            kSess = SessionCrypto.DeriveKey(serverKey, req.AgentEcdhPub);
        }
        catch (Exception) { return new Result(false, "bad_agent_pubkey", null, null); }

        // 4) 建工作階段(綁定身分到 exam/seat/agent)。seat := username(不安全/為空退回 sub)—— 身分即座位,學員無法自報。
        string seatId = ExamDispatch.SeatFrom(claims);
        HorusSession session = _sessions.Create(exam.ExamId, seatId, req.AgentId, req.MachineId, claims, kSess, now, _cfg.OidcSessionMinutes);
        _log.LogInformation("OIDC 会话建立 exam={Exam} seat={Seat} sub={Sub} user={User}", exam.ExamId, seatId, claims.Sub, claims.Username);
        return new Result(true, null, session, serverPub);
    }
}
