using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Horus.Server.Identity;

/// M4·S1:cpplearn OIDC **id_token 离线验签**(RS256 JWT)。**纯 BCL,无第三方 JWT 依赖**——
/// 局域网服务器预置 cpplearn JWKS(RSA 公钥)后即可离线验,不必每次回调 cpplearn(见 docs/m4-identity-oidc.md §5 S1)。
///
/// 验:①header.alg=RS256 且 kid 命中 JWKS ②RSA-PKCS1-SHA256 签名 over "header.payload" ③iss==配置 issuer
///     ④aud 含本 client_id ⑤exp 未过(含 60s 容差)⑥nonce==登录时下发(防重放)。
/// 通过则返回 <see cref="OidcClaims"/>(sub + Horus 富画像);任何不符抛 <see cref="OidcValidationException"/>。
public sealed class OidcTokenValidator
{
    private readonly Dictionary<string, RSA> _keysByKid;
    private readonly string _issuer;
    private readonly string _audience;
    private const int ClockSkewSeconds = 60;

    /// jwksJson:cpplearn `/.well-known/jwks.json` 的原文({keys:[{kty:RSA,n,e,kid,alg}]})。
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
        string[] parts = idToken.Split('.');
        if (parts.Length != 3) throw new OidcValidationException("id_token 结构非法(非 3 段 JWT)");

        JsonElement header = ParseSegment(parts[0], "header");
        if (Str(header, "alg") != "RS256") throw new OidcValidationException("id_token alg 非 RS256");
        string? kid = Str(header, "kid");
        if (kid is null || !_keysByKid.TryGetValue(kid, out RSA? rsa))
            throw new OidcValidationException($"id_token kid 未命中 JWKS(kid={kid ?? "null"});密钥可能已轮换,需同步新 JWKS");

        // 验签:RSA-PKCS1-SHA256 over ASCII("header.payload")。
        byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        byte[] sig = Base64UrlDecode(parts[2]);
        if (!rsa.VerifyData(signed, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
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
        // nonce(防重放:必须等于登录时下发的)
        if (expectedNonce is not null && Str(payload, "nonce") != expectedNonce)
            throw new OidcValidationException("id_token nonce 不符(疑重放 / 非本次登录)");

        string? sub = Str(payload, "sub");
        if (string.IsNullOrEmpty(sub)) throw new OidcValidationException("id_token 缺 sub");

        return new OidcClaims(
            Sub: sub!,
            Username: Str(payload, "username") ?? "",
            Nickname: Str(payload, "nickname") ?? Str(payload, "name") ?? "",
            DaoName: Str(payload, "dao_name") ?? "",
            Avatar: Str(payload, "avatar") ?? "",
            Realm: Str(payload, "realm") ?? "",
            RealmLevel: Int(payload, "realm_level"),
            CombatPower: Int(payload, "combat_power"));
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

    private static int Int(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : 0;
}

/// 验签通过后的 cpplearn 身份 + Horus 富画像(见 cpplearn claims horus_profile)。
public sealed record OidcClaims(
    string Sub, string Username, string Nickname, string DaoName, string Avatar, string Realm, int RealmLevel, int CombatPower);

public sealed class OidcValidationException(string message) : Exception(message);
