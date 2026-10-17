using Horus.Server.Config;

namespace Horus.Server.Identity;

/// M4·S4/S5:採集通道鑑權解析(PSK ↔ OIDC 工作階段共存)。給定連接/請求攜帶的 sessionId + 自報身分,
/// 決定用哪把 HMAC 金鑰驗籤、以及是否強制身分一致(閉合跨身分栽贓 A1 / seq 搶佔 A2)。
///
/// 規則(見 docs/m4-identity-oidc.md §6 authMode）:
///   • 攜 `X-Horus-Session`(OIDC 路徑):須 oidc/both 模式;工作階段有效且**綁定身分 == 自報 (exam,seat,agent)**,
///     否則拒(invalid_session / identity_mismatch)。驗籤金鑰 = 工作階段 K_sess。
///   • 無 session(PSK 路徑):psk/both 模式且配了 PSK → 用 PSK 驗籤(legacy·不強制身分,原 A1/A2 殘留僅存於 psk 模式)。
///   • 兩者皆無(整合測試·未配任何鑑權)→ Key=null(跳過驗籤)。
///   • oidc-only 模式卻無 session → 拒(session_required)。
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
            return new(true, null, s.KSess, s);   // 用 K_sess 驗籤;身分已強制一致
        }

        // 無 session → PSK 路徑
        if (cfg.PskAcceptedForIngest && cfg.AuthEnabled) return new(true, null, cfg.Psk, null);
        if (!cfg.AuthEnabled && !cfg.OidcEnabled) return new(true, null, null, null);   // 整合測試:未配任何鑑權,跳過驗籤
        return new(false, "session_required", null, null);   // oidc-only 卻無 session
    }
}
