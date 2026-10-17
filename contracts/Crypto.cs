using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Horus.Contracts;

/// 基礎雜湊 / HMAC 十六進制封裝(小寫)。
public static class Crypto
{
    public static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string HmacHex(byte[] key, string s)
    {
        using var h = new HMACSHA256(key);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    /// 常數時間比較(先各自 SHA256 到固定 32 位元組再比,連**長度**都不洩漏)。用於簽名 / 管理權杖。
    public static bool FixedTimeEquals(string a, string b)
    {
        Span<byte> ha = stackalloc byte[32];
        Span<byte> hb = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), ha);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hb);
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
}

/// 事件 canonical + 雜湊/簽名。**Agent 與 Server 共用同一實作,保證逐位元組一致**。見 api-contract §0.1。
///   hashSelf = SHA256(hashPrev + "\n" + canonicalCore)
///   sig      = HMAC-SHA256(PSK, hashSelf + "\n" + seq)
/// canonicalCore 欄位固定順序: examId, seatId, agentId, machineId, ts, type, payload, risk, evidenceImageId, seq
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

    /// **伺服器側**從寫入資料庫/收到的**原始 wire 欄位**復算 canonicalCore,與 <see cref="Core"/> 產出**逐位元組一致**。
    /// 關鍵點(保證與 Agent 端 typed 序列化對齊):
    ///   • payload 用收到的**原始 JSON 文字**(`JsonElement.GetRawText()` / 庫內 payload 列)原樣拼接(WriteRawValue),
    ///     不經 Dictionary 往返 —— 杜絕數字格式 / 鍵序 / 嵌套類型的重序列化漂移。
    ///   • type 用收到的 snake_case 字串(它正是枚舉 `Json.Wire` 序列化後的值,如 "browser_url")。
    ///   • evidenceImageId 為 null 時**省略**該鍵(與 Core 的 WhenWritingNull 一致)。
    ///   • Utf8JsonWriter 預設用與 JsonSerializer 相同的 `JavaScriptEncoder.Default` 轉義 + 相同的 double 最短往返格式。
    /// 用於雜湊鏈完整性複驗(閉合 architecture §10.1「伺服器不重算 canonical」)。CanonicalTests 鎖定與 Core 的逐位元組一致。
    public static string CoreRaw(
        string examId, string seatId, string agentId, string machineId,
        double ts, string typeSnake, string payloadRaw, int risk, string? evidenceImageId, long seq)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("examId", examId);
            w.WriteString("seatId", seatId);
            w.WriteString("agentId", agentId);
            w.WriteString("machineId", machineId);
            w.WriteNumber("ts", ts);
            w.WriteString("type", typeSnake);
            w.WritePropertyName("payload");
            w.WriteRawValue(string.IsNullOrEmpty(payloadRaw) ? "{}" : payloadRaw);   // 原樣拼接收到的 payload 文字
            w.WriteNumber("risk", risk);
            if (evidenceImageId is not null) w.WriteString("evidenceImageId", evidenceImageId);
            w.WriteNumber("seq", seq);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// 復算 hashSelf 並與給定值常數時間比較。true = 該 hashSelf 確實承諾(綁定)這組 payload / 欄位。
    /// 注:僅證明「hashSelf ↔ payload/欄位」內部自洽;是否鏈到真正的前驅由**連續性稽核**另判(見 IntegrityAudit)。
    public static bool VerifyHashSelf(
        string hashPrev, string examId, string seatId, string agentId, string machineId,
        double ts, string typeSnake, string payloadRaw, int risk, string? evidenceImageId, long seq, string? hashSelf)
    {
        if (string.IsNullOrEmpty(hashSelf)) return false;
        string recomputed = Crypto.Sha256Hex(hashPrev + "\n" +
            CoreRaw(examId, seatId, agentId, machineId, ts, typeSnake, payloadRaw, risk, evidenceImageId, seq));
        return Crypto.FixedTimeEquals(recomputed, hashSelf);
    }

    /// 事件簽名。**僅相依 hashSelf 字串與 seq**,故伺服器無需重算 canonical 即可驗簽(M1)。
    public static string Sig(byte[] psk, string hashSelf, long seq)
        => Crypto.HmacHex(psk, hashSelf + "\n" + seq);

    /// 複驗 sig 是否 = HMAC(PSK, hashSelf + "\n" + seq)。用於離線完整性稽核:hashSelf 是無金鑰 SHA256,
    /// 非 PSK 方改 payload 後可重算 hashSelf 使其自洽 —— 唯 sig(HMAC-PSK)能識破,故稽核須驗 sig。
    public static bool VerifySig(byte[] psk, string? hashSelf, long seq, string? sig)
        => !string.IsNullOrEmpty(sig) && hashSelf is not null && Crypto.FixedTimeEquals(Sig(psk, hashSelf, seq), sig);
}

/// 握手與圖片通道的鑑權簽名。見 api-contract §1.1 / §2.1。
public static class Auth
{
    /// WebSocket 握手頭 X-Horus-Auth = HMAC(PSK, examId|seatId|agentId)。
    public static string Handshake(byte[] psk, string examId, string seatId, string agentId)
        => Crypto.HmacHex(psk, $"{examId}|{seatId}|{agentId}");

    /// 圖片上傳頭 X-Horus-Sig = HMAC(PSK, canonicalHeaders + "\n" + sha256(body))。
    /// canonicalHeaders 採用固定順序的 "key:value" 換行拼接(見 ImageCanonicalHeaders)。
    public static string ImageSig(byte[] psk, string canonicalHeaders, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(psk, canonicalHeaders + "\n" + bodyHash);
    }

    /// 擊鍵旁路簽名 X-Horus-KSig = HMAC(KSK, "keystroke\n" + sha256(body))。
    /// 由**判題後端**(可安全持 KSK,瀏覽器不持)對整條提交體簽名;綁定 seatId/內容,防同網學員機偽造/栽贓。
    /// "keystroke\n" 域分隔前綴:防跨通道簽名重用(圖片/事件簽名不能拿來當擊鍵簽名)。
    public static string KeystrokeSig(byte[] ksk, byte[] body)
    {
        string bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return Crypto.HmacHex(ksk, "keystroke\n" + bodyHash);
    }

    /// 圖片上傳的規範化頭串(兩端必須一致)。順序: exam, seat, agent, seq, trigger, phash, ts, imageId。
    /// imageId = 用戶端預生成 id(無則傳 "");納入簽名防止 X-Horus-Image-Id 被篡改汙染證據關聯。
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

/// 事件信封:{ v, type:"event", event:{...}, seq, sig }。見 api-contract §1.2。
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
