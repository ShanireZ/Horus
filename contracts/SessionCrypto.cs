using System.Security.Cryptography;
using System.Text;

namespace Horus.Contracts;

/// M4 身份层:采集会话密钥协商(取代共享 PSK)。**Agent 与 Server 共用**,保证两端派生同一把 K_sess。
///
/// 设计(见 docs/m4-identity-oidc.md §3.2):OIDC 登录确立身份后,Agent 生成**临时 ECDH 密钥对**,把**公钥**随
/// /oidc/exchange 上报(该请求由一次性 code 保护);Server 也出一把临时 ECDH 公钥,两端各自 ECDH 得**同一 K_sess**。
/// 之后握手 / 事件签名沿用既有 HMAC(只把密钥从共享 PSK 换成 K_sess) —— **私钥永不过网**:LAN 嗅探者只见公钥,
/// 无从派生 K_sess,故无法伪造他人事件(闭合 §10.1 事件通道跨身份栽赃 A1 / seq 抢占 A2)。
///
/// 曲线 P-256(BCL 原生 ECDiffieHellman,无第三方依赖);共享密钥经 HKDF-SHA256 派生 32 字节 K_sess。
public static class SessionCrypto
{
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("horus-session-key-v1");

    /// 生成一把临时 ECDH(P-256)密钥对。调用方用完 Dispose。
    public static ECDiffieHellman NewEphemeralKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// 导出公钥为 base64(SubjectPublicKeyInfo·X.509 SPKI DER)。两端互传的就是这个串。
    public static string ExportPublicKeyB64(ECDiffieHellman key)
        => Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo());

    /// 从对端 base64(SPKI)公钥 + 本方私钥派生 K_sess(32 字节)。两端交换公钥后各算一次,结果一致。
    /// 畸形公钥 → 抛 CryptographicException/FormatException,调用方按鉴权失败处理。
    public static byte[] DeriveKey(ECDiffieHellman ownKey, string peerPublicKeyB64)
    {
        byte[] spki = Convert.FromBase64String(peerPublicKeyB64);
        using ECDiffieHellman peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(spki, out _);
        // SHA256(sharedSecret ‖ info) → 定长 32 字节对称密钥(NIST 800-56A concat KDF 风格·两端逐字节一致)。
        return ownKey.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, secretPrepend: null, secretAppend: HkdfInfo);
    }
}
