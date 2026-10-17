using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text.Json;
using Horus.Contracts;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// M3 雜湊鏈完整性複驗:①ingest 時復算 hashSelf 拒收「hashSelf 不承諾其 payload」的影格;
/// ②/integrity 離線稽核發現寫入資料庫後篡改(hash 不符)與刪除/重排(鏈斷)。
public class IntegrityTests
{
    private const string Exam = "E1", Seat = "A07", Agent = "ag-A07", Machine = "PC-A07";

    private static async Task CreateExamAsync(HttpClient http)
    {
        HttpResponseMessage r = await http.PostAsJsonAsync("/api/exams", new
        {
            examId = Exam, name = "完整性测试",
            seats = new[] { new { seatId = Seat, agentId = Agent, machineId = Machine, displayName = "学员", studentId = "s01" } },
        });
        r.EnsureSuccessStatusCode();
    }

    /// 建構一條已封鏈事件,回傳(信封 JSON, 本條 hashSelf) —— 便於把 hashSelf 串給下一條作 hashPrev。
    private static (string json, string hashSelf) BuildEvent(
        SignalType type, Dictionary<string, object?> payload, int risk, long seq, string hashPrev,
        string exam = Exam, string seat = Seat, string agent = Agent, string machine = Machine)
    {
        var core = new AgentEvent
        {
            ExamId = exam, SeatId = seat, AgentId = agent, MachineId = machine,
            Ts = 1750000000.0 + seq, Type = type, Payload = payload, Risk = risk, Seq = seq,
        };
        string hashSelf = EventCanonical.HashSelf(hashPrev, core, seq);
        string sig = EventCanonical.Sig(TestApp.Psk, hashSelf, seq);
        AgentEvent stamped = core with { HashPrev = hashPrev, HashSelf = hashSelf };
        return (Envelope.Serialize(stamped, sig), hashSelf);
    }

    /// 傳送一條 window_focus,回讀 ack。回傳本條 hashSelf。
    private static async Task<string> SendChainedAsync(WebSocket ws, long seq, string hashPrev)
    {
        (string json, string hashSelf) = BuildEvent(
            SignalType.WindowFocus, new() { ["title"] = "题目" + seq, ["process"] = "acrobat" }, 0, seq, hashPrev);
        await Ws.SendAsync(ws, json);
        JsonElement ack = await Ws.ReceiveAsync(ws);
        Assert.Equal("ack", ack.GetProperty("type").GetString());   // 合法鏈每條都 ack(不誤傷)
        return hashSelf;
    }

    [Fact]
    public async Task 完整链_离线审计全通过()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        string h1 = await SendChainedAsync(ws, 1, "GENESIS");
        string h2 = await SendChainedAsync(ws, 2, h1);
        await SendChainedAsync(ws, 3, h2);

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.True(rep.GetProperty("ok").GetBoolean());
        Assert.Equal(3, rep.GetProperty("totalEvents").GetInt32());
        Assert.Equal(3, rep.GetProperty("totalHashOk").GetInt32());
        Assert.Equal(3, rep.GetProperty("totalChainOk").GetInt32());
        JsonElement agents = rep.GetProperty("agents");
        Assert.Equal(1, agents.GetArrayLength());
        Assert.True(agents[0].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ingest时_hashSelf不承诺payload_拒收bad_hash()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);

        // 為 payload=P(訪問 AI 站)正確封鏈,得合法 hashSelf/sig;然後把信封裡的 url 篡改為判題站(P'),
        // hashSelf/sig 保持不變 —— sig 仍驗得過(只綁 hashSelf),但 hashSelf 不再承諾 P' → bad_hash。
        (string json, _) = BuildEvent(SignalType.BrowserUrl,
            new() { ["process"] = "chrome", ["url"] = "https://chat.openai.com/", ["whitelisted"] = false }, 80, 1, "GENESIS");
        string tampered = json.Replace("chat.openai.com", "judge.exam.cn");
        Assert.NotEqual(json, tampered);   // 確保確實改動了

        await Ws.SendAsync(ws, tampered);
        JsonElement err = await Ws.ReceiveAsync(ws);
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Equal("bad_hash", err.GetProperty("code").GetString());

        // 未寫入資料庫
        JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events?seatId={Seat}");
        Assert.Equal(0, events.GetArrayLength());
    }

    [Fact]
    public async Task 落库后篡改payload_审计报hash不符()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");

        // 直接改庫(模擬 DB 層篡改 / 儲存損壞):payload 被改,但 hash_self 未隨之更新 → 錨點不再自洽
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "UPDATE events SET payload=@p WHERE exam_id=@e AND seq=1", ("@p", "{\"title\":\"改过了\"}"), ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.False(rep.GetProperty("ok").GetBoolean());
        JsonElement agent = rep.GetProperty("agents")[0];
        JsonElement mism = agent.GetProperty("hashMismatches");
        Assert.Equal(1, mism.GetArrayLength());
        Assert.Equal(1, mism[0].GetProperty("seq").GetInt64());
        // 鏈連續本身沒斷(只改了內容,沒刪沒插)→ continuityBreaks 為空
        Assert.Equal(0, agent.GetProperty("continuityBreaks").GetArrayLength());
    }

    [Fact]
    public async Task 迁移前旧事件缺machineId_标不可验_不误报篡改()
    {
        // 閉合稽核 High:M3 前寫入資料庫的舊事件 machine_id=NULL,canonicalCore 含 machineId 無從復算 →
        // 必須歸為 unverifiable,**不能**誤報"hash 不符(疑篡改)"汙染取證結論。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");

        // 模擬遷移前舊行:machine_id 置 NULL(內容/hash 未動)
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("UPDATE events SET machine_id=NULL WHERE exam_id=@e AND seq=1", ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.True(rep.GetProperty("ok").GetBoolean());                       // 無篡改證據
        Assert.Equal(1, rep.GetProperty("totalUnverifiable").GetInt32());       // 但誠實標註 1 條不可驗
        Assert.Equal(0, rep.GetProperty("totalHashOk").GetInt32());
        JsonElement agent = rep.GetProperty("agents")[0];
        Assert.Equal(1, agent.GetProperty("unverifiable").GetInt32());
        Assert.Equal(0, agent.GetProperty("hashMismatches").GetArrayLength());  // 關鍵:不誤報篡改
    }

    [Fact]
    public async Task 已归档考试_integrity返回applicable_false_不伪装全绿()
    {
        // 閉合稽核 Med:歸檔後 live 事件清空,端點不應回傳空的 ok:true(誤導"已核驗乾淨")。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using (WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent))
        {
            await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
            await Ws.ReceiveAsync(ws);
            await SendChainedAsync(ws, 1, "GENESIS");
        }
        (await http.PostAsync($"/api/exams/{Exam}/end", null)).EnsureSuccessStatusCode();

        var archive = app.Services.GetRequiredService<Horus.Server.Jobs.ArchiveService>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 31 * 86400.0;
        Assert.Equal(1, archive.RunOnce(now).Archived);

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.Equal("archived", rep.GetProperty("status").GetString());
        Assert.False(rep.GetProperty("applicable").GetBoolean());
        Assert.False(rep.TryGetProperty("ok", out _));   // 不再有會被誤讀為"全綠"的 ok 欄位

        // 不存在的考試 → 404
        HttpResponseMessage nf = await http.GetAsync("/api/exams/NOPE/integrity");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, nf.StatusCode);
    }

    [Fact]
    public async Task 删除中间事件_审计报链断()
    {
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        string h1 = await SendChainedAsync(ws, 1, "GENESIS");
        string h2 = await SendChainedAsync(ws, 2, h1);
        await SendChainedAsync(ws, 3, h2);

        // 刪除中間那條(seq=2)→ 第 3 條的 hash_prev(=h2)不再等於新前驅(第 1 條 h1)→ 鏈斷
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("DELETE FROM events WHERE exam_id=@e AND seq=2", ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.False(rep.GetProperty("ok").GetBoolean());
        JsonElement agent = rep.GetProperty("agents")[0];
        // 每條自身 hash 仍自洽(沒改內容)→ hashMismatches 空;但鏈斷在第 3 條
        Assert.Equal(0, agent.GetProperty("hashMismatches").GetArrayLength());
        JsonElement breaks = agent.GetProperty("continuityBreaks");
        Assert.Equal(1, breaks.GetArrayLength());
        Assert.Equal(3, breaks[0].GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task 归档中考试_事件被短路不落库_仍ack()
    {
        // #7:歸檔"讀快照→DELETE WHERE exam_id"之間到達的 late-ingest 會被無錨點刪。修=status='archiving' 後 ingest 短路。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        // 直接置 archiving(模擬歸檔進行中)
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("UPDATE exams SET status='archiving' WHERE exam_id=@e", ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);                                        // hello_ack
        JsonElement ended = await Ws.ReceiveAsync(ws);                    // 考試派發:非 active 考試 hello 即補發 exam_ended
        Assert.Equal("exam_ended", ended.GetProperty("type").GetString());
        await SendChainedAsync(ws, 1, "GENESIS");   // 內含 ack 斷言:仍 ack(讓 Agent 停發)

        JsonElement events = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/events?seatId={Seat}");
        Assert.Equal(0, events.GetArrayLength());   // 歸檔中:未寫入資料庫(不會被隨後的 DELETE 無錨點刪)
    }

    [Fact]
    public async Task Agent重启后新链段_GENESIS锚点_不误报链断()
    {
        // #1:Agent 每次處理程序啟動新建 HashChain,重新啟動後首條 hash_prev=GENESIS,但 seq 續增。
        // 稽核應把它認作**重新啟動錨點**(合法段起點),而非刪除/插入的 continuityBreak。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        string h1 = await SendChainedAsync(ws, 1, "GENESIS");
        await SendChainedAsync(ws, 2, h1);
        await SendChainedAsync(ws, 3, "GENESIS");   // 模擬重新啟動:seq 續到 3,新鏈段從 GENESIS 起

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.True(rep.GetProperty("ok").GetBoolean());                       // 重新啟動不是篡改
        Assert.Equal(1, rep.GetProperty("totalRestartBoundaries").GetInt32());
        JsonElement agent = rep.GetProperty("agents")[0];
        Assert.Equal(0, agent.GetProperty("continuityBreaks").GetArrayLength());   // 關鍵:不誤報鏈斷
        Assert.Equal(1, agent.GetProperty("restartBoundaries").GetInt32());
    }

    [Fact]
    public async Task 落库后改payload并重算hashSelf但无PSK重签_审计凭sig识破()
    {
        // #1 連帶:hash_self 是無金鑰 SHA256,非 PSK 篡改者改 payload 後可重算使①自洽;
        // 唯 sig(HMAC-PSK)能識破。驗證稽核補的 sig 檢核能抓到"hashSelf 自洽但 sig 不符"。
        using var app = new TestApp();
        HttpClient http = app.CreateClient();
        await CreateExamAsync(http);

        using WebSocket ws = await app.ConnectEventsAsync(Exam, Seat, Agent);
        await Ws.SendAsync(ws, "{\"v\":1,\"type\":\"hello\"}");
        await Ws.ReceiveAsync(ws);
        await SendChainedAsync(ws, 1, "GENESIS");

        // 攻擊者(無 PSK):改 payload 為 P',重算 hash_self'(無金鑰 SHA256)使錨點自洽;sig 仍綁舊 hash_self。
        var core2 = new AgentEvent
        {
            ExamId = Exam, SeatId = Seat, AgentId = Agent, MachineId = Machine,
            Ts = 1750000000.0 + 1, Type = SignalType.WindowFocus,
            Payload = new() { ["title"] = "forged" }, Risk = 0, Seq = 1,
        };
        string hs2 = EventCanonical.HashSelf("GENESIS", core2, 1);
        Db db = app.Services.GetRequiredService<Db>();
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "UPDATE events SET payload=@p, hash_self=@h WHERE exam_id=@e AND seq=1",
                ("@p", "{\"title\":\"forged\"}"), ("@h", hs2), ("@e", Exam));
            Assert.Equal(1, c.ExecuteNonQuery());
        });

        JsonElement rep = await http.GetFromJsonAsync<JsonElement>($"/api/exams/{Exam}/integrity");
        Assert.False(rep.GetProperty("ok").GetBoolean());
        JsonElement agent = rep.GetProperty("agents")[0];
        JsonElement mism = agent.GetProperty("hashMismatches");
        Assert.Equal(1, mism.GetArrayLength());                            // hashSelf 自洽,但 sig 檢核失敗
        Assert.Contains("sig", mism[0].GetProperty("detail").GetString());
        Assert.Equal(0, agent.GetProperty("continuityBreaks").GetArrayLength());
    }
}
