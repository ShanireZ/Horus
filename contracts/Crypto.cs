using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honus.Contracts;

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

    /// 常量时间比较十六进制签名,避免计时侧信道。
    public static bool FixedTimeEquals(string aHex, string bHex)
    {
        if (aHex.Length != bHex.Length) return false;
        int diff = 0;
        for (int i = 0; i < aHex.Length; i++) diff |= aHex[i] ^ bHex[i];
        return diff == 0;
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
    /// WebSocket 握手头 X-Honus-Auth = HMAC(PSK, examId|seatId|agentId)。
    public static string Handshake(byte[] psk, string examId, string seatId, string agentId)
        => Crypto.HmacHex(psk, $"{examId}|{seatId}|{agentId}");

    /// 图片上传头 X-Honus-Sig = HMAC(PSK, canonicalHeaders + "\n" + sha256(body))。
    /// canonicalHeaders 采用固定顺序的 "key:value" 换行拼接(见 ImageCanonicalHeaders)。
    public static string ImageSig(byte[] psk, string canonicalHeaders, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(psk, canonicalHeaders + "\n" + bodyHash);
    }

    /// 图片上传的规范化头串(两端必须一致)。顺序: exam, seat, agent, seq, trigger, phash, ts。
    public static string ImageCanonicalHeaders(
        string examId, string seatId, string agentId, long seq, string trigger, string phash, string ts)
        => string.Join("\n", new[]
        {
            "exam:" + examId,
            "seat:" + seatId,
            "agent:" + agentId,
            "seq:" + seq,
            "trigger:" + trigger,
            "phash:" + phash,
            "ts:" + ts,
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
