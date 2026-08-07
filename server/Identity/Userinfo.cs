using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Horus.Server.Identity;

/// 身份 claims 的**唯一**来源:贝塔通的 userinfo 端点(`/me`)。
///
/// ★★ **为什么不从 id_token 取。** 贝塔通把 `conformIdTokenClaims` 留在 `oidc-provider` 的上游
///   默认值 `true`,授权码流下 **id_token 只带 `sub` 与协议 claims**(`acr` / `sid` / `auth_time` / `iss`),
///   身份 claims 一律只在 userinfo 出现。其 `docs/rp-contract.md` 的接入清单把这条写成了一行:
///   「按『用 ID Token claims 建档』实现会拿到一个只有 subject 的空快照」。
///
/// ★★ **旧 wentian 不是这样,而那条豁免不随迁移过来。** 它在 `server/oidc/provider.js` 里专门
///   为本项目设了 `conformIdTokenClaims: false`,注释写着「Horus Server 纯局域网靠离线验
///   id_token 拿富画像」。指向贝塔通后照旧从 id_token 取,表现是:`Name` 与 `Username` 恒为空串 →
///   <see cref="ExamDispatch.SeatFrom"/> 对**每一个**学生回退成 `sub`,座位号全变成不可读的 id,
///   看板与取证也没有姓名。**不报错、不抛异常、测试全绿** —— 因为测试里的 id_token 是自己签的,
///   手工塞了那两个 claim。这正是「本地/测试自己造的令牌与真 IdP 不一样」那一类。
///
/// ★ **多一次在线往返不破坏 R5 拓扑**(docs/m4-identity-oidc.md §10.3):换 token 本来就是在线动作,
///   userinfo 紧随其后走同一条连接。真正离线的是**验签**(预置 JWKS),不是取身份。
///
/// ★ **取不到就是登录失败,不回退到空身份**。fail-closed 与 R3 同一取向:贝塔通不可达时宁可登不进去,
///   也不要一个「进得去但谁也认不出是谁」的考场 —— 后者的取证材料是废的,而它看上去一切正常。
public static class Userinfo
{
    /// 拉 userinfo 并合成完整身份。
    /// <param name="subject">id_token 验签得到的 subject —— 用来比对 userinfo 的 `sub`。</param>
    /// 任何失败(网络、非 2xx、响应不是 JSON、`sub` 不符)一律抛 <see cref="OidcValidationException"/>。
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

    /// 解析 userinfo 响应体。抽成公开方法是为了让回归测试直接打这一层,不必架一个假 HTTP 端点。
    public static OidcClaims Parse(string json, OidcSubject subject)
    {
        JsonElement root;
        try { using JsonDocument doc = JsonDocument.Parse(json); root = doc.RootElement.Clone(); }
        catch
        {
            // ★ 贝塔通不给客户端登记 `userinfo_signed_response_alg`,所以响应是 JSON 而不是 JWT。
            //   真收到 JWT 说明后台登记被人改过 —— 这里如实报出来,别静默当成「没有 claims」。
            throw new OidcValidationException("userinfo 响应不是 JSON(登记了 userinfo_signed_response_alg?)");
        }

        // ★★ OIDC Core 5.3.2 的硬性要求:userinfo 的 `sub` **必须**与 id_token 的 `sub` 相同,不同即丢弃。
        //   不比这一下,「令牌是谁的」与「资料是谁的」就成了两件事 —— 令牌替换攻击正落在这条缝里。
        string? sub = Str(root, "sub");
        if (sub is null || !string.Equals(sub, subject.Sub, StringComparison.Ordinal))
            throw new OidcValidationException("userinfo 的 sub 与 id_token 不符(OIDC Core 5.3.2),已丢弃");

        return new OidcClaims(
            Sub: subject.Sub,
            // `profile` scope 的 `name` = 真实姓名(运营方录入)。
            Name: Str(root, "name") ?? "",
            // ★ 座位标识用它(见 ExamDispatch.SeatFrom)。贝塔通对**未设置用户名的账号直接省略这个 claim**
            //   (不发空串),所以这里恒可能为空 —— SeatFrom 已有回退 sub 的分支,不要在这里编一个默认值。
            Username: Str(root, "preferred_username") ?? "");
    }

    private static string? Str(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}

/// 一次登录确定下来的身份。**只有 `sub`、真实姓名与用户名**,三项都出自标准 scope
/// (`openid` + `profile`),且后两项**只能**由 <see cref="Userinfo"/> 从 userinfo 取回。
///
/// ★ 此前还带 `UserType` / `Nickname` / `DaoName` / `Avatar` / `Realm` / `RealmLevel` / `CombatPower`
///   七项,全部来自 wentian 的自定义 scope `horus_profile`。贝塔通 **P81** 停发它们:
///   那些是问天录的业务字段,身份中心只管「账号 × 平台 → 能否访问」。
/// ★ `Username` 留下了,但换了来源:从 `horus_profile` 换成标准 `profile` 的 `preferred_username` ——
///   它是**座位标识**的依据(<see cref="ExamDispatch.SeatFrom"/>),不是显示字段。
/// ★★ 其中 `UserType` 不是显示字段而是**判据** —— 看板准入此前唯一靠它。
///   现在改由贝塔通的 **`horus-admin` 平台开关**回答(**P83**),见 <see cref="AdminOidcFlow"/>。
public sealed record OidcClaims(string Sub, string Name, string Username);
