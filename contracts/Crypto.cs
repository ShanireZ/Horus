using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Horus.Contracts;

/// 基础哈希 / HMAC 十六进制封装(小写)。
public static class Crypto
{
    public static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string HmacHex(byte[] key, string s)
    {
        using var h = new HMACSHA256(key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    /// 常量时间比较(先各自 SHA256 到固定 32 字节再比,连**长度**都不泄漏)。用于签名 / 管理令牌。
    public static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> ha = stackalloc byte[32];
        Span<byte> hb = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), ha);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hb);
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}

/// 事件 canonical + 哈希/签名。**Agent 与 Server 共用同一实现,保证逐字节一致**。见 api-contract §0.1。
///   hashSelf = SHA256(hashPrev + "\n" + canonicalCore)
///   sig      = HMAC-SHA256(PSK, hashSelf + "\n" + seq)
/// canonicalCore 字段固定顺序: examId, seatId, agentId, machineId, ts, type, payload, risk, evidenceImageId, seq
public static class EventCanonical
{
    public static string Core(AgentEvent e, long seq)
        => JsonSerializer.Serialize(new
        {
            e.ExamId, e.SeatId, e.AgentId, e.MachineId,
            e.Ts, e.Type, e.Payload, e.Risk, e.EvidenceImageId, seq,
        }, Json.Wire);

    public static string HashSelf(string hashPrev, AgentEvent e, long seq)
        => Crypto.Sha256Hex(hashPrev + "\n" + Core(e, seq));

    /// 事件签名。**仅依赖 hashSelf 字符串与 seq**,故服务器无需重算 canonical 即可验签(M1)。
    public static string Sig(byte[] psk, string hashSelf, long seq)
        => Crypto.HmacHex(psk, hashSelf + "\n" + seq);
}

/// 握手与图片通道的鉴权签名。见 api-contract §1.1 / §2.1。
public static class Auth
{
    /// WebSocket 握手头 X-Horus-Auth = HMAC(PSK, examId|seatId|agentId)。
    public static string Handshake(byte[] psk, string examId, string seatId, string agentId)
        => Crypto.HmacHex(psk, $"{examId}|{seatId}|{agentId}");

    /// 图片上传头 X-Horus-Sig = HMAC(PSK, canonicalHeaders + "\n" + sha256(body))。
    /// canonicalHeaders 采用固定顺序的 "key:value" 换行拼接(见 ImageCanonicalHeaders)。
    public static string ImageSig(byte[] psk, string canonicalHeaders, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(psk, canonicalHeaders + "\n" + bodyHash);
    }

    /// 击键旁路签名 X-Horus-KSig = HMAC(KSK, "keystroke\n" + sha256(body))。
    /// 由**判题后端**(可安全持 KSK,浏览器不持)对整条提交体签名;绑定 seatId/内容,防同网学员机伪造/栽赃。
    /// "keystroke\n" 域分隔前缀:防跨通道签名重用(图片/事件签名不能拿来当击键签名)。
    public static string KeystrokeSig(byte[] ksk, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(ksk, "keystroke\n" + bodyHash);
    }

    /// 图片上传的规范化头串(两端必须一致)。顺序: exam, seat, agent, seq, trigger, phash, ts, imageId。
    /// imageId = 客户端预生成 id(无则传 "");纳入签名防止 X-Horus-Image-Id 被篡改污染证据关联。
    public static string ImageCanonicalHeaders(
        string examId, string seatId, string agentId, long seq, string trigger, string phash, string ts, string imageId = "")
        => string.Join("\n", new[]
        {
            "exam:" + examId,
            "seat:" + seatId,
            "agent:" + agentId,
            "seq:" + seq,
            "trigger:" + trigger,
            "phash:" + phash,
            "ts:" + ts,
            "imageId:" + imageId,
        });
}

/// 事件信封:{ v, type:"event", event:{...}, seq, sig }。见 api-contract §1.2。
public static class Envelope
{
    public static string Serialize(AgentEvent e, string sig)
        => JsonSerializer.Serialize(new
        {
            v = 1,
            type = "event",
            @event = e,
            seq = e.Seq,
            sig,
        }, Json.Wire);
}
