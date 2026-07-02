using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

// 环境变量为进程全局,故禁并行,避免多 TestApp 抢 env。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Horus.Server.Tests;

/// 每个测试起一个隔离的 in-memory 服务器(独立 :memory: DB + 独立临时 dataDir + 固定 PSK)。
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly byte[] Psk = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    public static readonly string PskB64 = Convert.ToBase64String(Psk);
    public static readonly byte[] Ksk = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    public static readonly string KskB64 = Convert.ToBase64String(Ksk);
    public const string AdminToken = "test-admin-token-xyz";

    private readonly string _dataDir;

    // M4:一份最小合法 JWKS(内联·让 OIDC 模式启动不去拉 issuer)。ingest 测试直接建会话,不走真验签。
    private const string DummyJwks = "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"test\",\"n\":\"sXchDaQebHnPiGvyDOAT4saGEUetSyo9MKLOoWFsueri23bOdgWp4Dy1WlUzewbgBHod5pcM9H5UGVn9YMcJDp5c\",\"e\":\"AQAB\"}]}";

    public TestApp(bool adminAuth = false, bool keystrokeAuth = false, bool visionMock = false, string? authMode = null)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "horus-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("HORUS_DBPATH", ":memory:");
        Environment.SetEnvironmentVariable("HORUS_DATADIR", _dataDir);
        Environment.SetEnvironmentVariable("HORUS_PSK_B64", PskB64);
        Environment.SetEnvironmentVariable("HORUS_KSK_B64", keystrokeAuth ? KskB64 : null);       // null 清除 → 默认击键鉴权关
        Environment.SetEnvironmentVariable("HORUS_ADMIN_TOKEN", adminAuth ? AdminToken : null);  // null 清除 → 默认管理鉴权关
        Environment.SetEnvironmentVariable("HORUS_VISION_PROVIDER", visionMock ? "mock" : null);  // null 清除 → 默认视觉分析关
        Environment.SetEnvironmentVariable("HORUS_URLS", "http://127.0.0.1:0");                   // loopback → 不触发 fail-closed
        // M4:authMode=oidc/both 时配 OIDC(内联 JWKS 免拉取);null → 默认 psk(既有测试无感)。
        Environment.SetEnvironmentVariable("HORUS_AUTH_MODE", authMode);
        bool oidc = authMode is "oidc" or "both";
        Environment.SetEnvironmentVariable("HORUS_OIDC_ISSUER", oidc ? "https://oidc.test" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_CLIENT_ID", oidc ? "horus-client" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_JWKS", oidc ? DummyJwks : null);
    }

    /// 连接事件 WS,附带合法握手头。
    public async Task<WebSocket> ConnectEventsAsync(string examId, string seatId, string agentId, bool goodAuth = true)
    {
        WebSocketClient client = Server.CreateWebSocketClient();
        string auth = goodAuth ? Auth.Handshake(Psk, examId, seatId, agentId) : "deadbeef";
        client.ConfigureRequest = req => req.Headers["X-Horus-Auth"] = auth;
        var uri = new Uri($"ws://localhost/ingest/events?examId={examId}&seatId={seatId}&agentId={agentId}");
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { /* ignore */ }
    }
}

/// WS 收发 + 事件封装小工具(测试内复用)。
public static class Ws
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    public static Task SendAsync(WebSocket ws, string json)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Ct);

    public static async Task<JsonElement> ReceiveAsync(WebSocket ws)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buf, Ct);
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
        return doc.RootElement.Clone();
    }

    /// 构造一条已签名的事件信封 JSON(模拟 Agent 的哈希链封装)。ts 固定,便于确定性。
    public static string SignedEvent(string examId, string seatId, string agentId, string machineId,
        SignalType type, Dictionary<string, object?> payload, int risk, long seq,
        string hashPrev = "GENESIS", string? evidenceImageId = null, byte[]? psk = null)
        => SignedEventTs(examId, seatId, agentId, machineId, type, payload, risk, seq, 1750000000.123, hashPrev, evidenceImageId, psk);

    /// 同上,但 ts 由调用方指定(如心跳需落在在线窗口内)。
    public static string SignedEventTs(string examId, string seatId, string agentId, string machineId,
        SignalType type, Dictionary<string, object?> payload, int risk, long seq, double ts,
        string hashPrev = "GENESIS", string? evidenceImageId = null, byte[]? psk = null)
    {
        psk ??= TestApp.Psk;
        var core = new AgentEvent
        {
            ExamId = examId, SeatId = seatId, AgentId = agentId, MachineId = machineId,
            Ts = ts, Type = type, Payload = payload, Risk = risk,
            EvidenceImageId = evidenceImageId, Seq = seq,
        };
        string hashSelf = EventCanonical.HashSelf(hashPrev, core, seq);
        string sig = EventCanonical.Sig(psk, hashSelf, seq);
        AgentEvent stamped = core with { HashPrev = hashPrev, HashSelf = hashSelf };
        return Envelope.Serialize(stamped, sig);
    }
}
