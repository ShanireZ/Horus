using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Horus.Server.Analysis;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Ingest;

/// 線上 Agent 連接註冊表 + 每考試最新設定快取。
/// 用於向連接中的 Agent **推送 config_update**(熱更新白名單/閾值/截圖參數)等下行影格。
/// **設定持久化**:下發即落 exam_config 表,建構時回填記憶體 —— 伺服器重新啟動後白名單不丟(server_risk 復判不退化、
/// Agent 重連 hello 仍能補推),閉合"config 記憶體快取重新啟動丟失致白名單退化"(architecture §10.2)。
public sealed class AgentHub
{
    private readonly ConcurrentDictionary<string, Conn> _conns = new();      // agentId → 當前連接
    private readonly ConcurrentDictionary<string, string> _config = new();   // examId → 最新設定 JSON
    private readonly ConcurrentDictionary<string, RiskModel.Policy> _policy = new();   // examId → **已解析**策略(熱路徑免每事件重解析)
    private readonly Db _db;

    public AgentHub(Db db)
    {
        _db = db;
        // 啟動回填:把已持久化的每考試設定載入記憶體快取(含解析後的策略)。
        _db.Read(conn =>
        {
            using SqliteCommand c = conn.Cmd("SELECT exam_id, config FROM exam_config");
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                string examId = r.GetString(0), json = r.GetString(1);
                _config[examId] = json;
                _policy[examId] = RiskModel.PolicyFrom(json);
            }
            return 0;
        });
    }

    /// 單個 Agent 連接。傳送經信號量串行化(避免 ack 與 config 推送並行寫同一 WS)。
    public sealed class Conn
    {
        public required string ExamId { get; init; }
        public required WebSocket Ws { get; init; }
        private readonly SemaphoreSlim _send = new(1, 1);

        public async Task SendAsync(string json, CancellationToken ct)
        {
            if (Ws.State != WebSocketState.Open) return;
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try { await Ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _send.Release(); }
        }
    }

    public Conn Register(string agentId, string examId, WebSocket ws)
    {
        var c = new Conn { ExamId = examId, Ws = ws };
        if (_conns.TryGetValue(agentId, out Conn? old))
            try { old.Ws.Abort(); } catch { /* 關舊連接:保證每 agent 至多一條活動連接 */ }
        _conns[agentId] = c;   // 重連覆蓋舊連接
        return c;
    }

    /// 用序列化器建構 config_update 影格(不用字串拼接,避免影格結構被破壞)。
    public static string BuildConfigFrame(string configJson)
        => JsonSerializer.Serialize(new { v = 1, type = "config_update", config = JsonNode.Parse(configJson) });

    /// 向指定線上 Agent 推送 capture_now(監考員手動點名抓圖)。回傳是否成功送達(agent 不線上 / 傳送失敗 → false)。
    /// 閉合第三輪 D2:api-contract 曾承諾 capture_now 影格但伺服器從無傳送方,Agent 側 handler 是死程式碼;此端點使其成真。
    public async Task<bool> PushCaptureNowAsync(string agentId, string reason, CancellationToken ct)
    {
        if (!_conns.TryGetValue(agentId, out Conn? c)) return false;
        string frame = JsonSerializer.Serialize(new { v = 1, type = "capture_now", reason });
        try { await c.SendAsync(frame, ct); return true; }
        catch { return false; }
    }

    /// 向該考試所有線上 Agent 推送一影格下行訊息。回傳送達連接數(掉線忽略)。
    public async Task<int> PushToExamAsync(string examId, string frame, CancellationToken ct)
    {
        int n = 0;
        foreach (KeyValuePair<string, Conn> kv in _conns)
        {
            if (kv.Value.ExamId != examId) continue;
            try { await kv.Value.SendAsync(frame, ct); n++; } catch { /* 掉線忽略 */ }
        }
        return n;
    }

    /// 考試結束廣播:通知線上 Agent 停止採集、排空緩衝後自行登出回待命(連接由 Agent 側優雅收尾,不強斷 —— 留續傳視窗)。
    public Task<int> PushExamEndedAsync(string examId, CancellationToken ct)
        => PushToExamAsync(examId, JsonSerializer.Serialize(new { v = 1, type = "exam_ended", examId }), ct);

    /// 全場遠程登出廣播(工作階段已在 DB 吊銷):先通知,再**強斷**該考試全部線上連接 ——
    /// 已吊銷工作階段的舊 WS 不能續用(握手時解析的 K_sess 仍在連接上下文裡,不斷開則事件仍會被收下)。
    /// 通知與強斷之間留極短視窗讓影格送達;重連會因 invalid_session 被 401。
    public async Task<int> PushSessionRevokedAsync(string examId, CancellationToken ct)
    {
        int n = await PushToExamAsync(examId, JsonSerializer.Serialize(new { v = 1, type = "session_revoked", examId }), ct);
        try { await Task.Delay(200, ct); } catch (OperationCanceledException) { /* 關停中仍要強斷 */ }
        foreach (KeyValuePair<string, Conn> kv in _conns)
        {
            if (kv.Value.ExamId != examId) continue;
            try { kv.Value.Ws.Abort(); } catch { /* 已斷,忽略 */ }
        }
        return n;
    }

    public void Unregister(string agentId, Conn c)
    {
        // 僅當仍是同一連接才移除,避免誤刪更晚的重連
        if (_conns.TryGetValue(agentId, out Conn? cur) && ReferenceEquals(cur, c))
            _conns.TryRemove(agentId, out _);
    }

    public string? GetConfig(string examId) => _config.TryGetValue(examId, out string? v) ? v : null;

    /// 該考試**已解析**的風險策略(白名單/閾值)。熱路徑(每事件)取此快取,免重複 JsonDocument.Parse + 建 HashSet。
    /// 未下發過 → EmptyPolicy(不加碼)。
    public RiskModel.Policy GetPolicy(string examId) => _policy.TryGetValue(examId, out RiskModel.Policy? p) ? p : RiskModel.EmptyPolicy;

    /// 存最新設定並推送給該考試所有線上 Agent。回傳成功推送到的連接數。
    public async Task<int> PushConfigAsync(string examId, string configJson, CancellationToken ct)
    {
        _config[examId] = configJson;
        _policy[examId] = RiskModel.PolicyFrom(configJson);   // 下發即重解析快取,熱路徑直接取
        // 持久化:重新啟動後回填,白名單不丟。
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO exam_config (exam_id,config,updated_at) VALUES (@e,@c,@t)
                  ON CONFLICT(exam_id) DO UPDATE SET config=@c, updated_at=@t",
                ("@e", examId), ("@c", configJson), ("@t", now));
            c.ExecuteNonQuery();
        });
        string frame = BuildConfigFrame(configJson);
        int n = 0;
        foreach (KeyValuePair<string, Conn> kv in _conns)
        {
            if (kv.Value.ExamId != examId) continue;
            try { await kv.Value.SendAsync(frame, ct); n++; } catch { /* 掉線忽略 */ }
        }
        return n;
    }
}
