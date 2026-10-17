using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

// 環境變數為處理程序全域,故禁並行,避免多 TestApp 搶 env。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Horus.Server.Tests;

/// 每個測試起一個隔離的 in-memory 伺服器(獨立 :memory: DB + 獨立臨時 dataDir + 固定 PSK)。
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly byte[] Psk = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    public static readonly string PskB64 = Convert.ToBase64String(Psk);
    public static readonly byte[] Ksk = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    public static readonly string KskB64 = Convert.ToBase64String(Ksk);
    public const string AdminToken = "test-admin-token-xyz";

    private readonly string _dataDir;

    // M4:一份最小合法 JWKS(內聯·讓 OIDC 模式啟動不去拉 issuer)。ingest 測試直接建工作階段,不走真驗簽。
    private const string DummyJwks = "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"test\",\"n\":\"sXchDaQebHnPiGvyDOAT4saGEUetSyo9MKLOoWFsueri23bOdgWp4Dy1WlUzewbgBHod5pcM9H5UGVn9YMcJDp5c\",\"e\":\"AQAB\"}]}";

    /// ★ `jwks`:注入一份**真能驗簽**的 JWKS —— 撤權通知那類案例要用真私鑰簽權杖打進來。
    /// 留空則用 <see cref="DummyJwks"/>,它只保證「能建構出驗證器」,簽不出也驗不過任何真權杖。
    /// ★ `healthLegacyAud`:探活是否**額外**收舊口徑(`aud` = client_id 本身)。預設 **false**,
    ///   與生產預設一致(對側 P100 之後只發 `&lt;client_id&gt;#health`);傳 true 用來驗那個逃生口。
    public TestApp(bool adminAuth = false, bool keystrokeAuth = false, bool visionMock = false, string? authMode = null, bool adminOidc = false, bool embedMock = false, string? jwks = null, bool healthLegacyAud = false, bool expectRevokePush = false)
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "horus-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("HORUS_DBPATH", ":memory:");
        Environment.SetEnvironmentVariable("HORUS_DATADIR", _dataDir);
        Environment.SetEnvironmentVariable("HORUS_PSK_B64", PskB64);
        Environment.SetEnvironmentVariable("HORUS_KSK_B64", keystrokeAuth ? KskB64 : null);       // null 清除 → 預設擊鍵鑑權關
        Environment.SetEnvironmentVariable("HORUS_ADMIN_TOKEN", adminAuth ? AdminToken : null);  // null 清除 → 預設管理鑑權關
        Environment.SetEnvironmentVariable("HORUS_VISION_PROVIDER", visionMock ? "mock" : null);  // null 清除 → 預設視覺分析關
        Environment.SetEnvironmentVariable("HORUS_EMBED_PROVIDER", embedMock ? "mock" : null);    // null 清除 → 預設按圖搜圖關
        Environment.SetEnvironmentVariable("HORUS_URLS", "http://127.0.0.1:0");                   // loopback → 不觸發 fail-closed
        // M4:authMode=oidc/both 時配 OIDC(內聯 JWKS 免拉取);null → 預設 psk(既有測試無感)。
        Environment.SetEnvironmentVariable("HORUS_AUTH_MODE", authMode);
        bool oidc = authMode is "oidc" or "both";
        Environment.SetEnvironmentVariable("HORUS_OIDC_ISSUER", (oidc || adminOidc) ? "https://oidc.test" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_CLIENT_ID", oidc ? "horus-client" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_JWKS", (oidc || adminOidc) ? (jwks ?? DummyJwks) : null);
        // M4·RBAC:adminOidc → 管理端走 wentian 長老 OIDC(dashboard client),靜態權杖退役。
        Environment.SetEnvironmentVariable("HORUS_ADMIN_AUTH_MODE", adminOidc ? "oidc" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_DASHBOARD_CLIENT_ID", adminOidc ? "horus-dashboard" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_DASHBOARD_SECRET", adminOidc ? "dash-secret" : null);
        Environment.SetEnvironmentVariable("HORUS_OIDC_DASHBOARD_REDIRECT", adminOidc ? "https://horus.test/cb" : null);
        // 探活 aud 的舊口徑逃生口(owner 2026-08-11 拍板專屬 aud·對側 P100 已完成實作)。
        // null 清除 → 用生產預設 false(只認 `<client_id>#health`)。
        Environment.SetEnvironmentVariable("HORUS_OIDC_HEALTH_LEGACY_AUD", healthLegacyAud ? "true" : null);
        // 是否期待收到貝塔通的撤權廣播/探活。null 清除 → 用生產預設 false(貝塔通 P101:有意不接)。
        Environment.SetEnvironmentVariable("HORUS_BETAPASS_REVOKE_PUSH", expectRevokePush ? "true" : null);
    }

    /// 連接事件 WS,附帶合法握手頭。
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

/// WS 收發 + 事件封裝小工具(測試內重複使用)。
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

    /// 建構一條已簽名的事件信封 JSON(模擬 Agent 的雜湊鏈封裝)。ts 固定,便於確定性。
    public static string SignedEvent(string examId, string seatId, string agentId, string machineId,
        SignalType type, Dictionary<string, object?> payload, int risk, long seq,
        string hashPrev = "GENESIS", string? evidenceImageId = null, byte[]? psk = null)
        => SignedEventTs(examId, seatId, agentId, machineId, type, payload, risk, seq, 1750000000.123, hashPrev, evidenceImageId, psk);

    /// 同上,但 ts 由呼叫方指定(如心跳需落在線上視窗內)。
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
