using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 身分 claims 的**唯一**來源:貝塔通的 userinfo 端點(`/me`)。
///
/// ★★ **為什麼不從 id_token 取。** 貝塔通把 `conformIdTokenClaims` 留在 `oidc-provider` 的上游
///   預設值 `true`,授權碼流下 **id_token 只帶 `sub` 與協定 claims**(`acr` / `sid` / `auth_time` / `iss`),
///   身分 claims 一律只在 userinfo 出現。其 `docs/rp-contract.md` 的接入清單把這條寫成了一行:
///   「按『用 ID Token claims 建檔』實作會拿到一個只有 subject 的空快照」。
///
/// ★★ **舊 wentian 不是這樣,而那條豁免不隨遷移過來。** 它在 `server/oidc/provider.js` 裡專門
///   為本專案設了 `conformIdTokenClaims: false`,註解寫著「Horus Server 純區域網路靠離線驗
///   id_token 拿富畫像」。指向貝塔通後照舊從 id_token 取,表現是:`Name` 與 `Username` 恆為空串 →
///   <see cref="ExamDispatch.SeatFrom"/> 對**每一個**學生退回成 `sub`,座位號全變成不可讀的 id,
///   看板與取證也沒有姓名。**不回報錯誤、不拋異常、測試全綠** —— 因為測試裡的 id_token 是自己簽的,
///   手動塞了那兩個 claim。這正是「本機/測試自己造的權杖與真 IdP 不一樣」那一類。
///
/// ★ **多一次線上往返不破壞 R5 拓撲**(docs/m4-identity-oidc.md §10.3):換 token 本來就是線上動作,
///   userinfo 緊隨其後走同一條連接。真正離線的是**驗簽**(預置 JWKS),不是取身分。
///
/// ★ **取不到就是登入失敗,不退回到空身分**。fail-closed 與 R3 同一取向:貝塔通不可達時寧可登不進去,
///   也不要一個「進得去但誰也認不出是誰」的考場 —— 後者的取證材料是廢的,而它看上去一切正常。
public static class Userinfo
{
    /// 拉 userinfo 併合成完整身分。
    /// <param name="subject">id_token 驗簽得到的 subject —— 用來比對 userinfo 的 `sub`。</param>
    /// 任何失敗(網路、非 2xx、回應不是 JSON、`sub` 不符)一律拋 <see cref="OidcValidationException"/>。
    public static async Task<OidcClaims> FetchAsync(
        HttpClient http, string endpoint, string accessToken, OidcSubject subject,
        ILogger log, CancellationToken ct)
    {
        string body;
        try
        {
            (bool ok, int status, string payload) =
                await OidcHttp.GetWithBearerAndRetryAsync(http, endpoint, accessToken, log, ct).ConfigureAwait(false);
            if (!ok) throw new OidcValidationException($"userinfo 端点非 2xx({status})");
            body = payload;
        }
        catch (OidcValidationException) { throw; }
        catch (Exception ex)
        {
            throw new OidcValidationException($"userinfo 请求失败(已重试):{ex.Message}");
        }
        return Parse(body, subject);
    }

    /// 解析 userinfo 回應體。抽成公開方法是為了讓迴歸測試直接打這一層,不必架一個假 HTTP 端點。
    public static OidcClaims Parse(string json, OidcSubject subject)
    {
        JsonElement root;
        try { using JsonDocument doc = JsonDocument.Parse(json); root = doc.RootElement.Clone(); }
        catch
        {
            // ★ 貝塔通不給用戶端登記 `userinfo_signed_response_alg`,所以回應是 JSON 而不是 JWT。
            //   真收到 JWT 說明後台登記被人改過 —— 這裡如實報出來,別靜默當成「沒有 claims」。
            throw new OidcValidationException("userinfo 响应不是 JSON(登记了 userinfo_signed_response_alg?)");
        }

        // ★★ OIDC Core 5.3.2 的硬性要求:userinfo 的 `sub` **必須**與 id_token 的 `sub` 相同,不同即丟棄。
        //   不比這一下,「權杖是誰的」與「資料是誰的」就成了兩件事 —— 權杖替換攻擊正落在這條縫裡。
        string? sub = Str(root, "sub");
        if (sub is null || !string.Equals(sub, subject.Sub, StringComparison.Ordinal))
            throw new OidcValidationException("userinfo 的 sub 与 id_token 不符(OIDC Core 5.3.2),已丢弃");

        return new OidcClaims(
            Sub: subject.Sub,
            // `profile` scope 的 `name` = 真實姓名(運營方錄入)。
            Name: Str(root, "name") ?? "",
            // ★ 座位標識用它(見 ExamDispatch.SeatFrom)。貝塔通對**未設定使用者名的帳號直接省略這個 claim**
            //   (不發空串),所以這裡恆可能為空 —— SeatFrom 已有退回 sub 的分支,不要在這裡編一個預設值。
            Username: Str(root, "preferred_username") ?? "");
    }

    private static string? Str(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}

/// 一次登入確定下來的身分。**只有 `sub`、真實姓名與使用者名**,三項都出自標準 scope
/// (`openid` + `profile`),且後兩項**只能**由 <see cref="Userinfo"/> 從 userinfo 取回。
///
/// ★ 此前還帶 `UserType` / `Nickname` / `DaoName` / `Avatar` / `Realm` / `RealmLevel` / `CombatPower`
///   七項,全部來自 wentian 的自訂 scope `horus_profile`。貝塔通 **P81** 停發它們:
///   那些是問天錄的業務欄位,身分中心只管「帳號 × 平台 → 能否訪問」。
/// ★ `Username` 留下了,但換了來源:從 `horus_profile` 換成標準 `profile` 的 `preferred_username` ——
///   它是**座位標識**的依據(<see cref="ExamDispatch.SeatFrom"/>),不是顯示欄位。
/// ★★ 其中 `UserType` 不是顯示欄位而是**判據** —— 看板存取資格此前唯一靠它。
///   現在改由貝塔通的 **`horus-admin` 平台開關**回答(**P83**),見 <see cref="AdminOidcFlow"/>。
public sealed record OidcClaims(string Sub, string Name, string Username);
