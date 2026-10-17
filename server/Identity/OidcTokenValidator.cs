using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Horus.Server.Identity;

/// 貝塔通 OIDC **id_token 離線驗籤**(**PS256** JWT)。**純 BCL,無第三方 JWT 相依**——
/// 區域網路伺服器預置貝塔通 JWKS(RSA 公鑰)後即可離線驗,不必每次回呼 IdP(見 docs/m4-identity-oidc.md §5 S1)。
///
/// ★★ **演算法固定為 PS256(RSA-PSS · SHA-256 · 鹽長 = 摘要長 32B)**,這是**允許清單隻有一項**,
///   不是「優先 PS256」。判據有兩條:
///   ① 貝塔通的金鑰集**只有 PS256**(其 P58),別的演算法它一枚都籤不出來;
///   ② 允許清單越窄越好 —— 讓權杖自己挑填充方案等於把選擇權交給攻擊者。
///   ★ 此前這裡寫死 RS256(RSA-PKCS1),那是對著舊 wentian 的。指向貝塔通後**每一枚 id_token
///   都會在 alg 那一行被拒**,而回報錯誤只說「alg 非 …」,很容易被當成權杖壞了去查 IdP。
///
/// 驗:①header.alg=PS256 且 kid 命中 JWKS ②RSA-PSS-SHA256 簽名 over "header.payload" ③iss==設定 issuer
///     ④aud 含本 client_id ⑤exp 未過(含 60s 容差)⑥nonce==登入時下發(防重放)。
/// 通過則回傳 <see cref="OidcSubject"/>;任何不符拋 <see cref="OidcValidationException"/>。
///
/// ★★ **它只給得出 `sub`,給不出姓名與使用者名** —— 那兩項在貝塔通只走 userinfo,
///   見 <see cref="Userinfo"/> 的類註解。此前這裡直接從 id_token 裡取 `name` /
///   `preferred_username` 並回傳一個「完整」的 <see cref="OidcClaims"/>,對著貝塔通跑會**恆取到空串**。
public sealed class OidcTokenValidator
{
    /// 唯一接受的簽名演算法。貝塔通的金鑰集只有它(其 P58),RP 側還須在用戶端中繼資料裡
    /// 顯式聲明 `id_token_signed_response_alg: 'PS256'` —— 不聲明時上游按 OIDC Core 取預設值 RS256,
    /// 會在**授權請求**那一步就 400 `invalid_client_metadata`,連交互都建立不出來。
    public const string SigningAlg = "PS256";

    private readonly Dictionary<string, RSA> _keysByKid;
    private readonly string _issuer;
    private readonly string _audience;
    private const int ClockSkewSeconds = 60;

    /// jwksJson:貝塔通 `/jwks` 的原文({keys:[{kty:RSA,n,e,kid,alg:PS256,use:sig}]})。
    /// ★ PS256 的 JWK `kty` 仍是 `RSA`(PSS 是填充方案不是金鑰類型),所以下面按 kty 過濾照舊成立。
    public OidcTokenValidator(string jwksJson, string issuer, string audience)
    {
        _issuer = issuer;
        _audience = audience;
        _keysByKid = LoadRsaKeys(jwksJson);
        if (_keysByKid.Count == 0)
            throw new OidcValidationException("JWKS 中无可用 RSA 公钥");
    }

    public OidcSubject Validate(string idToken, string? expectedNonce, double nowUnix)
    {
        JsonElement payload = ValidatePayload(idToken, expectedNonce, nowUnix);
        return new OidcSubject(Str(payload, "sub")!);   // ValidatePayload 已確認 sub 非空
    }

    /// 只驗籤與協定 claims,回傳原始 payload。
    /// ★ 抽出來是給**撤權通知**用的(見 <see cref="BetapassRevokeEndpoint"/>):那枚權杖走同一套
    ///   公鑰與同樣的 iss/aud/exp/alg 檢核,但載荷是 `jti`/`purpose` 而不是身分 claims,
    ///   且**沒有 nonce**(不是登入流程)。**共用這一段而不是另寫一份驗籤**,是為了不讓
    ///   「兩處各有一套驗籤」這種最容易分家的形態出現。
    public JsonElement ValidatePayload(string idToken, string? expectedNonce, double nowUnix)
    {
        string[] parts = idToken.Split('.');
        if (parts.Length != 3) throw new OidcValidationException("id_token 结构非法(非 3 段 JWT)");

        JsonElement header = ParseSegment(parts[0], "header");
        // ★ 允許清單隻有 PS256。**不要改成「RS256 或 PS256 都收」** —— 同一把 RSA 公鑰兩種填充都驗得通,
        //   放寬等於憑空多一條路,而貝塔通根本籤不出 RS256(其 P58),多出來的那條只服務於攻擊者。
        if (Str(header, "alg") != SigningAlg)
            throw new OidcValidationException($"id_token alg 非 {SigningAlg}(收到 {Str(header, "alg") ?? "null"});贝塔通只签 {SigningAlg}");
        string? kid = Str(header, "kid");
        if (kid is null || !_keysByKid.TryGetValue(kid, out RSA? rsa))
            throw new OidcValidationException($"id_token kid 未命中 JWKS(kid={kid ?? "null"});密钥可能已轮换,需同步新 JWKS");

        // 驗籤:RSA-PSS-SHA256 over ASCII("header.payload")。
        // ★ .NET 的 RSASignaturePadding.Pss 鹽長恆 = 摘要長(SHA-256 → 32B),與 JOSE 對 PS256 的規定一致,
        //   因此這裡不需要也無法另行指定鹽長。
        byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        byte[] sig = Base64UrlDecode(parts[2]);
        if (!rsa.VerifyData(signed, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new OidcValidationException("id_token 签名验证失败(疑伪造 / 密钥不符)");

        JsonElement payload = ParseSegment(parts[1], "payload");

        // iss
        if (Str(payload, "iss") != _issuer)
            throw new OidcValidationException($"id_token iss 不符(期望 {_issuer})");
        // aud(可為字串或陣列)
        if (!AudienceContains(payload, _audience))
            throw new OidcValidationException($"id_token aud 不含本 client_id({_audience})");
        // exp
        if (!payload.TryGetProperty("exp", out JsonElement expEl) || !expEl.TryGetDouble(out double exp))
            throw new OidcValidationException("id_token 缺 exp");
        if (nowUnix > exp + ClockSkewSeconds)
            throw new OidcValidationException("id_token 已过期");
        // nbf(若簽發方給了"未生效時間"則檢核·同 exp 的時鐘偏移容差·縱深防禦)
        if (payload.TryGetProperty("nbf", out JsonElement nbfEl) && nbfEl.TryGetDouble(out double nbf)
            && nowUnix + ClockSkewSeconds < nbf)
            throw new OidcValidationException("id_token 尚未生效(nbf)");
        // nonce(防重放:必須等於登入時下發的)
        if (expectedNonce is not null && Str(payload, "nonce") != expectedNonce)
            throw new OidcValidationException("id_token nonce 不符(疑重放 / 非本次登录)");

        if (string.IsNullOrEmpty(Str(payload, "sub"))) throw new OidcValidationException("id_token 缺 sub");
        return payload;
    }

    // ---- JWKS 解析 ----
    private static Dictionary<string, RSA> LoadRsaKeys(string jwksJson)
    {
        var map = new Dictionary<string, RSA>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(jwksJson);
        if (!doc.RootElement.TryGetProperty("keys", out JsonElement keys) || keys.ValueKind != JsonValueKind.Array)
            return map;
        foreach (JsonElement k in keys.EnumerateArray())
        {
            if (Str(k, "kty") != "RSA") continue;
            string? kid = Str(k, "kid");
            string? n = Str(k, "n"), e = Str(k, "e");
            if (kid is null || n is null || e is null) continue;
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = Base64UrlDecode(n), Exponent = Base64UrlDecode(e) });
            map[kid] = rsa;
        }
        return map;
    }

    private static JsonElement ParseSegment(string seg, string what)
    {
        try { return JsonDocument.Parse(Base64UrlDecode(seg)).RootElement.Clone(); }
        catch { throw new OidcValidationException($"id_token {what} 非合法 base64url JSON"); }
    }

    private static bool AudienceContains(JsonElement payload, string aud)
    {
        if (!payload.TryGetProperty("aud", out JsonElement a)) return false;
        if (a.ValueKind == JsonValueKind.String) return a.GetString() == aud;
        if (a.ValueKind == JsonValueKind.Array)
            foreach (JsonElement it in a.EnumerateArray())
                if (it.ValueKind == JsonValueKind.String && it.GetString() == aud) return true;
        return false;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4) { case 2: b += "=="; break; case 3: b += "="; break; }
        return Convert.FromBase64String(b);
    }

    private static string? Str(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;

}

/// id_token 驗籤通過後**能拿到的全部東西**:一個 `sub`。
///
/// ★★ **故意不含姓名與使用者名。** 貝塔通把 `conformIdTokenClaims` 留在上游預設值 `true`,
///   授權碼流下 id_token 只帶 `sub` 與協定 claims —— 身分 claims 一律只在 userinfo 出現
///   (其 `docs/rp-contract.md`)。此前 <see cref="OidcTokenValidator"/> 直接從 id_token 取那兩項、
///   回傳一個看起來完整的 <see cref="OidcClaims"/>,於是「忘了調 userinfo」**沒有任何症狀**:
///   姓名與使用者名恆為空串,座位號對每個人都靜默退回成 `sub`。
///   現在類型上就拿不到,要拿只能經 <see cref="Userinfo.FetchAsync"/>。
public sealed record OidcSubject(string Sub);

public sealed class OidcValidationException(string message) : Exception(message);
