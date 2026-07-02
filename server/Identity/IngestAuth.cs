using Horus.Server.Config;

namespace Horus.Server.Identity;

/// M4·S4/S5:采集通道鉴权解析(PSK ↔ OIDC 会话共存)。给定连接/请求携带的 sessionId + 自报身份,
/// 决定用哪把 HMAC 密钥验签、以及是否强制身份一致(闭合跨身份栽赃 A1 / seq 抢占 A2)。
///
/// 规则(见 docs/m4-identity-oidc.md §6 authMode）:
///   • 携 `X-Horus-Session`(OIDC 路径):须 oidc/both 模式;会话有效且**绑定身份 == 自报 (exam,seat,agent)**,
///     否则拒(invalid_session / identity_mismatch)。验签密钥 = 会话 K_sess。
///   • 无 session(PSK 路径):psk/both 模式且配了 PSK → 用 PSK 验签(legacy·不强制身份,原 A1/A2 残留仅存于 psk 模式)。
///   • 两者皆无(联调·未配任何鉴权)→ Key=null(跳过验签)。
///   • oidc-only 模式却无 session → 拒(session_required)。
public static class IngestAuth
{
    public sealed record Resolved(bool Ok, string? Error, byte[]? Key, HorusSession? Session);

    public static Resolved Resolve(
        ServerConfig cfg, SessionStore sessions, string? sessionId,
        string examId, string seatId, string agentId, double now)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (!cfg.OidcEnabled) return new(false, "oidc_disabled", null, null);
            HorusSession? s = sessions.Get(sessionId!, now);
            if (s is null) return new(false, "invalid_session", null, null);
            if (!s.IdentityMatches(examId, seatId, agentId)) return new(false, "identity_mismatch", null, null);
            return new(true, null, s.KSess, s);   // 用 K_sess 验签;身份已强制一致
        }

        // 无 session → PSK 路径
        if (cfg.PskAcceptedForIngest && cfg.AuthEnabled) return new(true, null, cfg.Psk, null);
        if (!cfg.AuthEnabled && !cfg.OidcEnabled) return new(true, null, null, null);   // 联调:未配任何鉴权,跳过验签
        return new(false, "session_required", null, null);   // oidc-only 却无 session
    }
}
