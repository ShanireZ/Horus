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
///
/// ★★ **它同时服务两个入站端点**:`/internal/revoke`(撤权通知)与 `/internal/health`(探活,其 P94)。
///   两者的令牌是**同一套签名密钥、同一个 `aud`(本机 client_id)、同一段 `signServiceToken`**
///   签出来的(BetaPass `src/main.ts` 的 `signToken` 与 `revocation/notifier.ts` 并排可见),
///   因此**验签这一层共用**,区别只在调用方还要不要报文语义 —— 见 <see cref="Verify"/> 与
///   <see cref="VerifyProbe"/>。
///   ★ 这一点与 rp-contract「多个入站端点按 `aud` 区分」**不冲突**:那条说的是
///   「撤权令牌」与「资源 audience 的推送令牌」之间要互斥,而探活令牌与撤权令牌本就
///   **有意同 aud**(贝塔通探的就是「会收撤权通知的那一端」)。真要拿探活令牌去打
///   `/internal/revoke`,还得过报文比对那一关(`sub` 得是 client_id,清的是查无此人的会话)。
public sealed class BetapassRevokeVerifier
{
    /// **撤权**候选:(client_id, 绑定该 aud 的验证器)。顺序无所谓,全部试完才算失败。
    private readonly (string ClientId, OidcTokenValidator Validator)[] _candidates;

    /// **探活**候选。★★ **与撤权候选分开是这次改动的全部要点** ——
    ///   共用一份的话,给探活加一个可接受的 `aud` 会**连带放宽 `/internal/revoke`**,
    ///   方向正好反了。回归 `探活专属aud的令牌打撤权端点_拒` 钉住这条。
    private readonly (string ClientId, OidcTokenValidator Validator)[] _probeCandidates;

    /// 探活当前接受哪些 `aud`(给预检显示用,判断过渡期能不能收掉)。
    public IReadOnlyList<string> ProbeAudiences { get; }

    public BetapassRevokeVerifier(string jwksJson, ServerConfig cfg)
    {
        string[] clientIds = [.. new[] { cfg.OidcClientId, cfg.OidcDashboardClientId }
            .Where(one => !string.IsNullOrEmpty(one)).Select(one => one!).Distinct(StringComparer.Ordinal)];
        _candidates = [.. clientIds.Select(id => (id, new OidcTokenValidator(jwksJson, cfg.OidcIssuer!, id)))];

        // 探活:新口径 `<client_id><后缀>`;过渡期同时接受旧口径 `<client_id>` 本身。
        // ★ 元组第一项仍是**归属的 client_id**(不是那个 aud 字面量),因为调用方要的是
        //   「这一发探的是哪个客户端」—— 新旧两种 aud 对这个答案没有影响。
        var probe = clientIds
            .Select(id => (id, Aud: id + cfg.OidcHealthAudienceSuffix)).ToList();
        if (cfg.OidcHealthAudienceAcceptLegacy)
            probe.AddRange(clientIds.Select(id => (id, Aud: id)));
        _probeCandidates = [.. probe.Select(p => (p.id, new OidcTokenValidator(jwksJson, cfg.OidcIssuer!, p.Aud)))];
        ProbeAudiences = [.. probe.Select(p => p.Aud)];
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
        (string clientId, JsonElement payload) = VerifyCore(token, "撤权", _candidates);

        string jti = BetapassRevokeEndpoint.Opt(payload, "jti")
            ?? throw new OidcValidationException("撤权令牌缺 jti");
        string sub = BetapassRevokeEndpoint.Opt(payload, "sub")
            ?? throw new OidcValidationException("撤权令牌缺 sub");
        // 令牌里的 `purpose` 与报文里的 `reason` 同源,用哪个都行(契约明写)。
        string reason = BetapassRevokeEndpoint.Opt(payload, "purpose") ?? "";
        return new BetapassRevokeEndpoint.Notice(jti, sub, clientId, reason);
    }

    /// 验一枚**探活**令牌(贝塔通 P94 的 `GET /internal/health`),返回它打的是本机哪个 client_id。
    ///
    /// ★ 与 <see cref="Verify"/> 的唯一差别是**不读任何报文语义** —— 探活令牌的 claims 只有
    ///   `iss` / `aud` / `sub`(= client_id 自己)/ `jti` / `iat` / `exp`,没有 `purpose`,
    ///   也没有要处置的对象。**签名 / `alg` / `iss` / `aud` / `exp` 五项一个不少**,
    ///   因为「验不验签」就是「这份接入拓扑是不是白送给任何能打到这个地址的人」的全部差别(其 P94)。
    ///
    /// ★★ **探活令牌走的是自己的候选集** <see cref="_probeCandidates"/>,与撤权分开。
    ///   owner 2026-08-11 拍板:由贝塔通给探活令牌一个**专属 `aud`**(`&lt;client_id&gt;#health`),
    ///   把契约里「多个入站端点唯一的区分是 `aud`」从一条带例外的规则恢复成**真不变量**。
    ///
    /// ★★★ **拆候选集是这次改动的全部要点**:两者共用一份的话,给探活加一个可接受的 `aud`
    ///   会**连带放宽 `/internal/revoke`** —— 方向正好反了。回归
    ///   `探活专属aud的令牌打撤权端点_拒` 正是钉这条。
    ///
    /// ⏳ **过渡期**:贝塔通尚未落地,今天发的仍是旧口径(`aud` = client_id 本身)。
    ///   故默认**新旧都收**(`OidcHealthAudienceAcceptLegacy`)。★★ 对侧落地后要记得置 false ——
    ///   不收掉的话「`aud` 拦不住这一对」的例外就永久留着,而那正是本次要消灭的东西。
    ///   `GET /api/preflight` 的 `health_audience` 一项会一直提示它还开着。
    ///
    /// ★ **`/internal/revoke` 那条「令牌 `sub` == 报文 `sub`」不因此撤掉**(见
    ///   <see cref="BetapassRevokeEndpoint.BodyMatches"/>):它降级为双保险,但**本身是自洽的
    ///   语义约束**、不依赖对侧保持任何字段 —— 而这次改动恰恰依赖对侧。两道都留着。
    public string VerifyProbe(string token) => VerifyCore(token, "探活", _probeCandidates).ClientId;

    /// 共用的验签内核:逐个候选 `aud` 试,全部试完仍不过才算失败。
    /// ★ `expectedNonce: null` —— 这两类令牌都不是登录流程,本就没有 nonce。
    /// ★★ **候选集由调用方传入**,不再读某个字段 —— 「撤权用哪一份、探活用哪一份」
    ///   因此是**调用点上看得见的事实**,而不是要去读构造函数才知道的约定。
    private (string ClientId, JsonElement Payload) VerifyCore(
        string token, string what, (string ClientId, OidcTokenValidator Validator)[] candidates)
    {
        if (candidates.Length == 0) throw new OidcValidationException("本机没有登记任何 client_id");

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        foreach ((string clientId, OidcTokenValidator validator) in candidates)
        {
            try
            {
                return (clientId, validator.ValidatePayload(token, expectedNonce: null, now));
            }
            catch (OidcValidationException)
            {
                // `aud` 不是这一个,试下一个;全部试完仍不过才算失败
            }
        }
        throw new OidcValidationException($"{what}令牌的 aud 不是本机任何一个 client_id");
    }
}
