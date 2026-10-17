using System.Text.Json;
using Horus.Server.Config;

namespace Horus.Server.Identity;

/// 撤權通知權杖的驗籤器。
///
/// ★★ **為什麼單獨一個類、且做成單例**:撤權權杖的 `aud` 可能是本機兩個 client_id 中的**任意一個**
///   (`horus-client` 採集面 / `horus-dashboard` 監考臺,貝塔通 P83),而
///   <see cref="OidcTokenValidator"/> 建構時就綁死了單一 `aud`,所以要按候選各備一個。
///   ★ 早先的寫法是**每次請求現 new** —— 那等於每一發撤權通知都重新解析一遍 JWKS 並
///   `RSA.Create()` 若干把,而那些 RSA 句柄**從不 Dispose**,只能等 GC。
///   端點頻率低所以不會立刻出事,但這是典型的「不出症狀因此不會有人來修」——
///   預建一次、常駐,既省掉重複解析也不再洩句柄。
///
/// ★ 與驗 id_token **共用同一套公鑰**(貝塔通就是同一套簽名金鑰):零新增金鑰材料,
///   簽名金鑰輪轉時這條連線路徑自動跟著走 —— 這正是貝塔通 P44 選簽名 JWT 而非共享金鑰的判據。
///
/// ★★ **它同時服務兩個入站端點**:`/internal/revoke`(撤權通知)與 `/internal/health`(探活,其 P94)。
///   兩者的權杖是**同一套簽名金鑰、同一個 `aud`(本機 client_id)、同一段 `signServiceToken`**
///   簽出來的(BetaPass `src/main.ts` 的 `signToken` 與 `revocation/notifier.ts` 並排可見),
///   因此**驗籤這一層共用**,區別只在呼叫方還要不要訊息語義 —— 見 <see cref="Verify"/> 與
///   <see cref="VerifyProbe"/>。
///   ★ 這一點與 rp-contract「多個入站端點按 `aud` 區分」**不衝突**:那條說的是
///   「撤權權杖」與「資源 audience 的推送權杖」之間要互斥,而探活權杖與撤權權杖本就
///   **有意同 aud**(貝塔通探的就是「會收撤權通知的那一端」)。真要拿探活權杖去打
///   `/internal/revoke`,還得過訊息比對那一關(`sub` 得是 client_id,清的是查無此人的工作階段)。
public sealed class BetapassRevokeVerifier
{
    /// **撤權**候選:(client_id, 綁定該 aud 的驗證器)。順序無所謂,全部試完才算失敗。
    private readonly (string ClientId, OidcTokenValidator Validator)[] _candidates;

    /// **探活**候選。★★ **與撤權候選分開是這次改動的全部要點** ——
    ///   共用一份的話,給探活加一個可接受的 `aud` 會**連帶放寬 `/internal/revoke`**,
    ///   方向正好反了。迴歸 `探活专属aud的令牌打撤权端点_拒` 釘住這條。
    private readonly (string ClientId, OidcTokenValidator Validator)[] _probeCandidates;

    /// 探活當前接受哪些 `aud`(給預檢顯示用,判斷過渡期能不能收掉)。
    public IReadOnlyList<string> ProbeAudiences { get; }

    public BetapassRevokeVerifier(string jwksJson, ServerConfig cfg)
    {
        string[] clientIds = [.. new[] { cfg.OidcClientId, cfg.OidcDashboardClientId }
            .Where(one => !string.IsNullOrEmpty(one)).Select(one => one!).Distinct(StringComparer.Ordinal)];
        _candidates = [.. clientIds.Select(id => (id, new OidcTokenValidator(jwksJson, cfg.OidcIssuer!, id)))];

        // 探活:新口徑 `<client_id><后缀>`;過渡期同時接受舊口徑 `<client_id>` 本身。
        // ★ 元組第一項仍是**歸屬的 client_id**(不是那個 aud 字面值),因為呼叫方要的是
        //   「這一發探的是哪個用戶端」—— 新舊兩種 aud 對這個答案沒有影響。
        var probe = clientIds
            .Select(id => (id, Aud: id + cfg.OidcHealthAudienceSuffix)).ToList();
        if (cfg.OidcHealthAudienceAcceptLegacy)
            probe.AddRange(clientIds.Select(id => (id, Aud: id)));
        _probeCandidates = [.. probe.Select(p => (p.id, new OidcTokenValidator(jwksJson, cfg.OidcIssuer!, p.Aud)))];
        ProbeAudiences = [.. probe.Select(p => p.Aud)];
    }

    /// 本機登記了幾個可接收撤權通知的用戶端。0 表示這臺機器根本收不了通知。
    public int CandidateCount => _candidates.Length;

    /// 驗籤並取出訊息三要素。
    ///
    /// ★ `aud` 必須是**本機登記的 client_id 之一**;不是就拒 —— 這一條就是
    ///   「誰都能踢人下線」與「只有貝塔通能踢」的全部差別。
    /// ★ 撤權權杖**沒有 nonce**(它不是登入流程),故跳過 nonce 檢核;
    ///   `alg`(PS256)/ 簽名 / `iss` / `aud` / `exp` **五項一個不少**。
    public BetapassRevokeEndpoint.Notice Verify(string token)
    {
        (string clientId, JsonElement payload) = VerifyCore(token, "撤权", _candidates);

        string jti = BetapassRevokeEndpoint.Opt(payload, "jti")
            ?? throw new OidcValidationException("撤权令牌缺 jti");
        string sub = BetapassRevokeEndpoint.Opt(payload, "sub")
            ?? throw new OidcValidationException("撤权令牌缺 sub");
        // 權杖裡的 `purpose` 與訊息裡的 `reason` 同源,用哪個都行(契約明寫)。
        string reason = BetapassRevokeEndpoint.Opt(payload, "purpose") ?? "";
        return new BetapassRevokeEndpoint.Notice(jti, sub, clientId, reason);
    }

    /// 驗一枚**探活**權杖(貝塔通 P94 的 `GET /internal/health`),回傳它打的是本機哪個 client_id。
    ///
    /// ★ 與 <see cref="Verify"/> 的唯一差別是**不讀任何訊息語義** —— 探活權杖的 claims 只有
    ///   `iss` / `aud` / `sub`(= client_id 自己)/ `jti` / `iat` / `exp`,沒有 `purpose`,
    ///   也沒有要處置的物件。**簽名 / `alg` / `iss` / `aud` / `exp` 五項一個不少**,
    ///   因為「驗不驗籤」就是「這份接入拓撲是不是白送給任何能打到這個位址的人」的全部差別(其 P94)。
    ///
    /// ★★ **探活權杖走的是自己的候選集** <see cref="_probeCandidates"/>,與撤權分開。
    ///   owner 2026-08-11 拍板:由貝塔通給探活權杖一個**專屬 `aud`**(`&lt;client_id&gt;#health`),
    ///   把契約裡「多個入站端點唯一的區分是 `aud`」從一條帶例外的規則恢復成**真不變數**。
    ///
    /// ★★★ **拆候選集是這次改動的全部要點**:兩者共用一份的話,給探活加一個可接受的 `aud`
    ///   會**連帶放寬 `/internal/revoke`** —— 方向正好反了。迴歸
    ///   `探活专属aud的令牌打撤权端点_拒` 正是釘這條。
    ///
    /// ⏳ **過渡期**:貝塔通尚未完成實作,今天發的仍是舊口徑(`aud` = client_id 本身)。
    ///   故預設**新舊都收**(`OidcHealthAudienceAcceptLegacy`)。★★ 對側完成實作後要記得置 false ——
    ///   不收掉的話「`aud` 攔不住這一對」的例外就永久留著,而那正是本次要消滅的東西。
    ///   `GET /api/preflight` 的 `health_audience` 一項會一直提示它還開著。
    ///
    /// ★ **`/internal/revoke` 那條「權杖 `sub` == 訊息 `sub`」不因此撤掉**(見
    ///   <see cref="BetapassRevokeEndpoint.BodyMatches"/>):它降級為雙保險,但**本身是自洽的
    ///   語義約束**、不相依對側保持任何欄位 —— 而這次改動恰恰相依對側。兩道都留著。
    public string VerifyProbe(string token) => VerifyCore(token, "探活", _probeCandidates).ClientId;

    /// 共用的驗籤核心:逐個候選 `aud` 試,全部試完仍不過才算失敗。
    /// ★ `expectedNonce: null` —— 這兩類權杖都不是登入流程,本就沒有 nonce。
    /// ★★ **候選集由呼叫方傳入**,不再讀某個欄位 —— 「撤權用哪一份、探活用哪一份」
    ///   因此是**呼叫點上看得見的事實**,而不是要去讀建構函式才知道的約定。
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
                // `aud` 不是這一個,試下一個;全部試完仍不過才算失敗
            }
        }
        throw new OidcValidationException($"{what}令牌的 aud 不是本机任何一个 client_id");
    }
}
