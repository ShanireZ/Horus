using System.Security.Cryptography;
using System.Text;

namespace Horus.Contracts;

/// M4 身分層:採集工作階段金鑰協商(取代共享 PSK)。**Agent 與 Server 共用**,保證兩端派生同一把 K_sess。
///
/// 設計(見 docs/m4-identity-oidc.md §3.2):OIDC 登入確立身分後,Agent 生成**臨時 ECDH 金鑰對**,把**公鑰**隨
/// /oidc/exchange 上報(該請求由一次性 code 保護);Server 也出一把臨時 ECDH 公鑰,兩端各自 ECDH 得**同一 K_sess**。
/// 之後握手 / 事件簽名沿用既有 HMAC(只把金鑰從共享 PSK 換成 K_sess) —— **私鑰永不過網**:LAN 嗅探者只見公鑰,
/// 無從派生 K_sess,故無法偽造他人事件(閉合 §10.1 事件通道跨身分栽贓 A1 / seq 搶佔 A2)。
///
/// 曲線 P-256(BCL 原生 ECDiffieHellman,無第三方相依);共享金鑰經 HKDF-SHA256 派生 32 位元組 K_sess。
public static class SessionCrypto
{
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("horus-session-key-v1");

    /// 生成一把臨時 ECDH(P-256)金鑰對。呼叫方用完 Dispose。
    public static ECDiffieHellman NewEphemeralKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// 匯出公鑰為 base64(SubjectPublicKeyInfo·X.509 SPKI DER)。兩端互傳的就是這個串。
    public static string ExportPublicKeyB64(ECDiffieHellman key)
        => Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo());

    /// 從對端 base64(SPKI)公鑰 + 本方私鑰派生 K_sess(32 位元組)。兩端交換公鑰後各算一次,結果一致。
    /// 畸形公鑰 → 拋 CryptographicException/FormatException,呼叫方按鑑權失敗處理。
    public static byte[] DeriveKey(ECDiffieHellman ownKey, string peerPublicKeyB64)
    {
        byte[] spki = Convert.FromBase64String(peerPublicKeyB64);
        using ECDiffieHellman peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(spki, out _);
        // SHA256(sharedSecret ‖ info) → 定長 32 位元組對稱金鑰(NIST 800-56A concat KDF 風格·兩端逐位元組一致)。
        return ownKey.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, secretPrepend: null, secretAppend: HkdfInfo);
    }
}
