using System.Text.Json;
using Horus.Server.Config;

namespace Horus.Server.Identity;

/// 撤权通知令牌的验签器。
///
/// ★★ **为什么单独一个类、且做成单例**:撤权令牌的 `aud` 可能是本机两个 client_id 中的**任意一个**
///   (`horus-client` 采集面 / `horus-dashboard` 监考台,贝塔通 P83),而
///   <see cref="OidcTokenValidator"/> 构造时就绑死了单一 `aud`,所以要按候选各备一个。
///   ★ 早先的写法是**每次请求现 new** —— 那等于每一发撤权通知都重新解析一遍 JWKS 并
///   `RSA.Create()` 若干把,而那些 RSA 句柄**从不 Dispose**,只能等 GC。
///   端点频率低所以不会立刻出事,但这是典型的「不出症状因此不会有人来修」——
///   预建一次、常驻,既省掉重复解析也不再泄句柄。
///
/// ★ 与验 id_token **共用同一套公钥**(贝塔通就是同一套签名密钥):零新增密钥材料,
///   签名密钥轮转时这条链路自动跟着走 —— 这正是贝塔通 P44 选签名 JWT 而非共享密钥的判据。
public sealed class BetapassRevokeVerifier
{
    /// 候选:(client_id, 绑定该 aud 的验证器)。顺序无所谓,全部试完才算失败。
    private readonly (string ClientId, OidcTokenValidator Validator)[] _candidates;

    public BetapassRevokeVerifier(string jwksJson, ServerConfig cfg)
    {
        string[] clientIds = [.. new[] { cfg.OidcClientId, cfg.OidcDashboardClientId }
            .Where(one => !string.IsNullOrEmpty(one)).Select(one => one!).Distinct(StringComparer.Ordinal)];
        _candidates = [.. clientIds.Select(id => (id, new OidcTokenValidator(jwksJson, cfg.OidcIssuer!, id)))];
    }

    /// 本机登记了几个可接收撤权通知的客户端。0 表示这台机器根本收不了通知。
    public int CandidateCount => _candidates.Length;

    /// 验签并取出报文三要素。
    ///
    /// ★ `aud` 必须是**本机登记的 client_id 之一**;不是就拒 —— 这一条就是
    ///   「谁都能踢人下线」与「只有贝塔通能踢」的全部差别。
    /// ★ 撤权令牌**没有 nonce**(它不是登录流程),故跳过 nonce 校验;
    ///   `alg`(PS256)/ 签名 / `iss` / `aud` / `exp` **五项一个不少**。
    public BetapassRevokeEndpoint.Notice Verify(string token)
    {
        if (_candidates.Length == 0) throw new OidcValidationException("本机没有登记任何 client_id");

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        foreach ((string clientId, OidcTokenValidator validator) in _candidates)
        {
            JsonElement payload;
            try
            {
                payload = validator.ValidatePayload(token, expectedNonce: null, now);
            }
            catch (OidcValidationException)
            {
                continue;   // `aud` 不是这一个,试下一个;全部试完仍不过才算失败
            }

            string jti = BetapassRevokeEndpoint.Opt(payload, "jti")
                ?? throw new OidcValidationException("撤权令牌缺 jti");
            string sub = BetapassRevokeEndpoint.Opt(payload, "sub")
                ?? throw new OidcValidationException("撤权令牌缺 sub");
            // 令牌里的 `purpose` 与报文里的 `reason` 同源,用哪个都行(契约明写)。
            string reason = BetapassRevokeEndpoint.Opt(payload, "purpose") ?? "";
            return new BetapassRevokeEndpoint.Notice(jti, sub, clientId, reason);
        }
        throw new OidcValidationException("撤权令牌的 aud 不是本机任何一个 client_id");
    }
}
