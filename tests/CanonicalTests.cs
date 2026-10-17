using Horus.Contracts;
using Xunit;

namespace Horus.Server.Tests;

/// 鎖定 canonical / 雜湊 / 簽名的**逐位元組**格式。Agent 與 Server 共用 Horus.Contracts 的這套實作,
/// 此測試即兩端一致性的黃金基準——任何欄位順序 / 命名 / 枚舉編碼漂移都會被抓到。
public class CanonicalTests
{
    private static AgentEvent Sample() => new()
    {
        ExamId = "E1", SeatId = "A07", AgentId = "ag-A07", MachineId = "PC-A07",
        Ts = 1750000000.5, Type = SignalType.BrowserUrl,
        Payload = new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false },
        Risk = 80, Seq = 1287,
    };

    [Fact]
    public void Canonical_字段顺序_camelCase_snake枚举_省略null()
    {
        string canon = EventCanonical.Core(Sample(), 1287);
        const string expected =
            "{\"examId\":\"E1\",\"seatId\":\"A07\",\"agentId\":\"ag-A07\",\"machineId\":\"PC-A07\"," +
            "\"ts\":1750000000.5,\"type\":\"browser_url\"," +
            "\"payload\":{\"process\":\"chrome\",\"url\":\"https://chat.openai.com/\",\"whitelisted\":false}," +
            "\"risk\":80,\"seq\":1287}";   // evidenceImageId 為 null 被省略
        Assert.Equal(expected, canon);
    }

    [Fact]
    public void 哈希与签名_确定且可复验()
    {
        AgentEvent e = Sample();
        string self1 = EventCanonical.HashSelf("GENESIS", e, e.Seq);
        string self2 = EventCanonical.HashSelf("GENESIS", e, e.Seq);
        Assert.Equal(self1, self2);                 // 確定性
        Assert.Equal(64, self1.Length);             // SHA256 hex
        Assert.Matches("^[0-9a-f]{64}$", self1);

        string sig = EventCanonical.Sig(TestApp.Psk, self1, e.Seq);
        Assert.Equal(64, sig.Length);
        // 伺服器式驗籤:僅憑 hashSelf + seq 復算,不相依 canonical
        Assert.True(Crypto.FixedTimeEquals(sig, EventCanonical.Sig(TestApp.Psk, self1, e.Seq)));
    }

    [Fact]
    public void 握手签名_稳定()
    {
        string a = Auth.Handshake(TestApp.Psk, "E1", "A07", "ag-A07");
        string b = Auth.Handshake(TestApp.Psk, "E1", "A07", "ag-A07");
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    // ---- M3 完整性複驗:CoreRaw(伺服器從原始 wire 欄位復算) 必須與 Core(Agent typed 序列化) 逐位元組一致 ----
    // 任何漂移都會讓伺服器把**合法**事件誤判為雜湊不符而拒收,故此為硬門禁黃金測試。

    /// 把一個 AgentEvent 的 payload 用 Json.Wire 序列化,取得伺服器側會收到的原始 payload 文字(GetRawText 等價物)。
    private static string PayloadRaw(AgentEvent e)
        => System.Text.Json.JsonSerializer.Serialize(e.Payload, Json.Wire);

    private static string TypeSnake(AgentEvent e)
        => System.Text.Json.JsonSerializer.Serialize(e.Type, Json.Wire).Trim('"');

    [Fact]
    public void CoreRaw_与Core逐字节一致_含url与嵌套payload()
    {
        AgentEvent e = Sample();
        string raw = EventCanonical.CoreRaw(
            e.ExamId, e.SeatId, e.AgentId, e.MachineId, e.Ts, TypeSnake(e), PayloadRaw(e), e.Risk, e.EvidenceImageId, e.Seq);
        Assert.Equal(EventCanonical.Core(e, e.Seq), raw);
    }

    [Fact]
    public void CoreRaw_与Core逐字节一致_含evidenceImageId()
    {
        AgentEvent e = Sample() with { EvidenceImageId = "img_8f1c2d", Seq = 42 };
        string raw = EventCanonical.CoreRaw(
            e.ExamId, e.SeatId, e.AgentId, e.MachineId, e.Ts, TypeSnake(e), PayloadRaw(e), e.Risk, e.EvidenceImageId, e.Seq);
        Assert.Equal(EventCanonical.Core(e, e.Seq), raw);
        Assert.Contains("\"evidenceImageId\":\"img_8f1c2d\"", raw);
    }

    [Theory]
    [InlineData(SignalType.Heartbeat, 0)]
    [InlineData(SignalType.ProcessStart, 70)]
    [InlineData(SignalType.Clipboard, 60)]
    [InlineData(SignalType.AltTabBurst, 40)]
    public void CoreRaw_与Core逐字节一致_多类型多payload(SignalType type, int risk)
    {
        // 含中文(非 ASCII 轉義)、整數、布爾、小數 ts、嵌套物件 —— 覆蓋編碼器與數字格式的對齊面。
        var e = new AgentEvent
        {
            ExamId = "期末-2026", SeatId = "B12", AgentId = "ag/B12", MachineId = "PC_B12",
            Ts = 1750000000.0, Type = type,
            Payload = new()
            {
                ["name"] = "向日葵.exe", ["pid"] = 4321, ["whitelisted"] = false,
                ["nested"] = new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x&y<z>" },
                ["len"] = 250, ["lines"] = 8,
            },
            Risk = risk, Seq = 999,
        };
        string raw = EventCanonical.CoreRaw(
            e.ExamId, e.SeatId, e.AgentId, e.MachineId, e.Ts, TypeSnake(e), PayloadRaw(e), e.Risk, e.EvidenceImageId, e.Seq);
        Assert.Equal(EventCanonical.Core(e, e.Seq), raw);
    }

    [Fact]
    public void VerifyHashSelf_匹配则真_篡改payload则假()
    {
        AgentEvent e = Sample();
        string hashSelf = EventCanonical.HashSelf("GENESIS", e, e.Seq);
        // 正確 payload → 複驗通過
        Assert.True(EventCanonical.VerifyHashSelf(
            "GENESIS", e.ExamId, e.SeatId, e.AgentId, e.MachineId, e.Ts, TypeSnake(e), PayloadRaw(e), e.Risk, e.EvidenceImageId, e.Seq, hashSelf));
        // 篡改 payload(換 url)但仍用舊 hashSelf → 複驗失敗
        string tampered = PayloadRaw(e).Replace("chat.openai.com", "judge.exam.cn");
        Assert.False(EventCanonical.VerifyHashSelf(
            "GENESIS", e.ExamId, e.SeatId, e.AgentId, e.MachineId, e.Ts, TypeSnake(e), tampered, e.Risk, e.EvidenceImageId, e.Seq, hashSelf));
        // 篡改 hashPrev → 複驗失敗(hashSelf 也綁定前驅)
        Assert.False(EventCanonical.VerifyHashSelf(
            "OTHER", e.ExamId, e.SeatId, e.AgentId, e.MachineId, e.Ts, TypeSnake(e), PayloadRaw(e), e.Risk, e.EvidenceImageId, e.Seq, hashSelf));
    }
}
