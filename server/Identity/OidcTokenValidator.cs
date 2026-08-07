using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Horus.Server.Identity;

/// 贝塔通 OIDC **id_token 离线验签**(**PS256** JWT)。**纯 BCL,无第三方 JWT 依赖**——
/// 局域网服务器预置贝塔通 JWKS(RSA 公钥)后即可离线验,不必每次回调 IdP(见 docs/m4-identity-oidc.md §5 S1)。
///
/// ★★ **算法固定为 PS256(RSA-PSS · SHA-256 · 盐长 = 摘要长 32B)**,这是**允许清单只有一项**,
///   不是「优先 PS256」。判据有两条:
///   ① 贝塔通的密钥集**只有 PS256**(其 P58),别的算法它一枚都签不出来;
///   ② 允许清单越窄越好 —— 让令牌自己挑填充方案等于把选择权交给攻击者。
///   ★ 此前这里写死 RS256(RSA-PKCS1),那是对着旧 wentian 的。指向贝塔通后**每一枚 id_token
///   都会在 alg 那一行被拒**,而报错只说「alg 非 …」,很容易被当成令牌坏了去查 IdP。
///
/// 验:①header.alg=PS256 且 kid 命中 JWKS ②RSA-PSS-SHA256 签名 over "header.payload" ③iss==配置 issuer
///     ④aud 含本 client_id ⑤exp 未过(含 60s 容差)⑥nonce==登录时下发(防重放)。
/// 通过则返回 <see cref="OidcClaims"/>;任何不符抛 <see cref="OidcValidationException"/>。
public sealed class OidcTokenValidator
{
    /// 唯一接受的签名算法。贝塔通的密钥集只有它(其 P58),RP 侧还须在客户端元数据里
    /// 显式声明 `id_token_signed_response_alg: 'PS256'` —— 不声明时上游按 OIDC Core 取默认值 RS256,
    /// 会在**授权请求**那一步就 400 `invalid_client_metadata`,连交互都创建不出来。
    public const string SigningAlg = "PS256";

    private readonly Dictionary<string, RSA> _keysByKid;
    private readonly string _issuer;
    private readonly string _audience;
    private const int ClockSkewSeconds = 60;

    /// jwksJson:贝塔通 `/jwks` 的原文({keys:[{kty:RSA,n,e,kid,alg:PS256,use:sig}]})。
    /// ★ PS256 的 JWK `kty` 仍是 `RSA`(PSS 是填充方案不是密钥类型),所以下面按 kty 过滤照旧成立。
    public OidcTokenValidator(string jwksJson, string issuer, string audience)
    {
        _issuer = issuer;
        _audience = audience;
        _keysByKid = LoadRsaKeys(jwksJson);
        if (_keysByKid.Count == 0)
            throw new OidcValidationException("JWKS 中无可用 RSA 公钥");
    }

    public OidcClaims Validate(string idToken, string? expectedNonce, double nowUnix)
    {
        JsonElement payload = ValidatePayload(idToken, expectedNonce, nowUnix);
        return ToClaims(payload);
    }

    /// 只验签与协议 claims,返回原始 payload。
    /// ★ 抽出来是给**撤权通知**用的(见 <see cref="BetapassRevokeEndpoint"/>):那枚令牌走同一套
    ///   公钥与同样的 iss/aud/exp/alg 校验,但载荷是 `jti`/`purpose` 而不是身份 claims,
    ///   且**没有 nonce**(不是登录流程)。**共用这一段而不是另写一份验签**,是为了不让
    ///   「两处各有一套验签」这种最容易分家的形态出现。
    public JsonElement ValidatePayload(string idToken, string? expectedNonce, double nowUnix)
    {
        string[] parts = idToken.Split('.');
        if (parts.Length != 3) throw new OidcValidationException("id_token 结构非法(非 3 段 JWT)");

        JsonElement header = ParseSegment(parts[0], "header");
        // ★ 允许清单只有 PS256。**不要改成「RS256 或 PS256 都收」** —— 同一把 RSA 公钥两种填充都验得通,
        //   放宽等于凭空多一条路,而贝塔通根本签不出 RS256(其 P58),多出来的那条只服务于攻击者。
        if (Str(header, "alg") != SigningAlg)
            throw new OidcValidationException($"id_token alg 非 {SigningAlg}(收到 {Str(header, "alg") ?? "null"});贝塔通只签 {SigningAlg}");
        string? kid = Str(header, "kid");
        if (kid is null || !_keysByKid.TryGetValue(kid, out RSA? rsa))
            throw new OidcValidationException($"id_token kid 未命中 JWKS(kid={kid ?? "null"});密钥可能已轮换,需同步新 JWKS");

        // 验签:RSA-PSS-SHA256 over ASCII("header.payload")。
        // ★ .NET 的 RSASignaturePadding.Pss 盐长恒 = 摘要长(SHA-256 → 32B),与 JOSE 对 PS256 的规定一致,
        //   因此这里不需要也无法另行指定盐长。
        byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        byte[] sig = Base64UrlDecode(parts[2]);
        if (!rsa.VerifyData(signed, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new OidcValidationException("id_token 签名验证失败(疑伪造 / 密钥不符)");

        JsonElement payload = ParseSegment(parts[1], "payload");

        // iss
        if (Str(payload, "iss") != _issuer)
            throw new OidcValidationException($"id_token iss 不符(期望 {_issuer})");
        // aud(可为字符串或数组)
        if (!AudienceContains(payload, _audience))
            throw new OidcValidationException($"id_token aud 不含本 client_id({_audience})");
        // exp
        if (!payload.TryGetProperty("exp", out JsonElement expEl) || !expEl.TryGetDouble(out double exp))
            throw new OidcValidationException("id_token 缺 exp");
        if (nowUnix > exp + ClockSkewSeconds)
            throw new OidcValidationException("id_token 已过期");
        // nbf(若签发方给了"未生效时间"则校验·同 exp 的时钟偏移容差·纵深防御)
        if (payload.TryGetProperty("nbf", out JsonElement nbfEl) && nbfEl.TryGetDouble(out double nbf)
            && nowUnix + ClockSkewSeconds < nbf)
            throw new OidcValidationException("id_token 尚未生效(nbf)");
        // nonce(防重放:必须等于登录时下发的)
        if (expectedNonce is not null && Str(payload, "nonce") != expectedNonce)
            throw new OidcValidationException("id_token nonce 不符(疑重放 / 非本次登录)");

        if (string.IsNullOrEmpty(Str(payload, "sub"))) throw new OidcValidationException("id_token 缺 sub");
        return payload;
    }

    private static OidcClaims ToClaims(JsonElement payload)
    {
        string sub = Str(payload, "sub")!;
        return new OidcClaims(
            Sub: sub,
            // 真实姓名与用户名走标准 `profile` scope,贝塔通的 claims 出口里本来就有这两项。
            Name: Str(payload, "name") ?? "",
            // ★ 座位标识用它(见 ExamDispatch.SeatFrom)。贝塔通对**未设置用户名的账号直接省略这个 claim**
            //   (不发空串),所以这里恒可能为空 —— SeatFrom 已有回退 sub 的分支,不要在这里编一个默认值。
            Username: Str(payload, "preferred_username") ?? "");
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

/// 验签通过后的身份。**只有 `sub`、真实姓名与用户名**,三项都出自标准 scope。
///
/// ★ 此前还带 `UserType` / `Nickname` / `DaoName` / `Avatar` / `Realm` / `RealmLevel` / `CombatPower`
///   七项,全部来自 wentian 的自定义 scope `horus_profile`。贝塔通 **P81** 停发它们:
///   那些是问天录的业务字段,身份中心只管「账号 × 平台 → 能否访问」。
/// ★ `Username` 留下了,但换了来源:从 `horus_profile` 换成标准 `profile` 的 `preferred_username` ——
///   它是**座位标识**的依据(<see cref="ExamDispatch.SeatFrom"/>),不是显示字段。
/// ★★ 其中 `UserType` 不是显示字段而是**判据** —— 看板准入此前唯一靠它。
///   现在改由贝塔通的 **`horus-admin` 平台开关**回答(**P83**),见 <see cref="AdminOidcFlow"/>。
public sealed record OidcClaims(string Sub, string Name, string Username);

public sealed class OidcValidationException(string message) : Exception(message);
