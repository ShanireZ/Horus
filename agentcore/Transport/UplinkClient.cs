using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Horus.Agent.Buffer;
using Horus.Agent.Config;
using Horus.Agent.Identity;
using Horus.Contracts;

namespace Horus.Agent.Transport;

/// 事件走 WebSocket(即時),圖片走 HTTP。**含握手鑑權、hello/ack、斷線重連(指數退避)、續傳**。
/// 可靠性:每條事件先落緩衝再盡力傳送;伺服器 ack(upto) 後壓實;重連後按 seq 續傳(伺服器冪等去重)。
/// WS 連接與 HttpClient 均可注入(預設真實實作;測試注入 TestServer 用戶端,實作記憶體端到端驗證)。
public sealed class UplinkClient : IAsyncDisposable
{
    private readonly AgentConfig _cfg;
    private readonly LocalBuffer _buffer;
    private readonly IngestCredential _cred;   // M4:簽名金鑰(PSK 或 OIDC K_sess)+ 可選 sessionId
    private readonly HttpClient _http;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _wsConnect;
    private readonly Uri _wsUri;
    private readonly Uri _httpUri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private volatile WebSocket? _ws;
    private long _seq;
    private long _seqCeiling;                          // 已持久化的序號高水位
    private readonly object _seqLock = new();
    private const long SeqBlock = 256;                 // 每預留 256 個序號才落一次盤

    /// 監考員 capture_now → 觸發一次抓圖(Program 注入)。
    /// 未注入 → 看板「點名抓圖」回傳 pushed:true 但 Agent 不抓圖(靜默失效),故 Program 注入後須保證非空(見 agent/Program.cs 啟動斷言)。
    public Action<string>? OnCaptureNow;
    /// 伺服器下發新設定(熱更新)。M1 僅回呼,由 Program 決定如何應用。
    public Action<JsonElement>? OnConfigUpdate;
    /// 考試結束廣播(end 時線上推送 / 離線重連 hello 補發)。Program 應排空緩衝後停採、登出回待命。
    public Action? OnExamEnded;
    /// 全場遠程登出廣播(工作階段已在伺服器吊銷,連接隨後被強斷)。Program 應立即棄工作階段回待命(重連必 401)。
    public Action? OnSessionRevoked;

    public UplinkClient(AgentConfig cfg, LocalBuffer buffer,
        IngestCredential? cred = null,
        HttpClient? http = null,
        Func<Uri, CancellationToken, Task<WebSocket>>? wsConnect = null)
    {
        _cfg = cfg;
        _buffer = buffer;
        // 預設 PSK 模式(既有呼叫點無感);OIDC 模式由 Program 傳入 {K_sess, sessionId}。
        _cred = cred ?? new IngestCredential(cfg.Psk ?? Array.Empty<byte>(), null);
        _http = http ?? new HttpClient();
        _wsConnect = wsConnect ?? DefaultConnectAsync;
        _wsUri = new Uri($"{cfg.ServerWsBase}/ingest/events?examId={cfg.ExamId}&seatId={cfg.SeatId}&agentId={cfg.AgentId}");
        _httpUri = new Uri($"{cfg.ServerHttpBase}/ingest/images");

        // 序號從持久化高水位與緩衝最大 seq 之上繼續,杜絕重新啟動重複使用(否則新事件撞舊 seq 被伺服器冪等吞掉)。
        long start = Math.Max(buffer.LoadSeqCeiling(), buffer.MaxBufferedSeq());
        _seq = start;
        _seqCeiling = start;
        if (start > 0) buffer.SaveSeqCeiling(start);
    }

    /// 單調遞增序號。跨事件與圖片共用;每超過高水位就預留一個 block 並寫入磁碟,重新啟動後從高水位之上繼續。
    public long NextSeq()
    {
        long s = Interlocked.Increment(ref _seq);
        if (s > Volatile.Read(ref _seqCeiling))
            lock (_seqLock)
                if (s > _seqCeiling) { _seqCeiling = s + SeqBlock; _buffer.SaveSeqCeiling(_seqCeiling); }
        return s;
    }

    /// 預設 WS 連接:帶 X-Horus-Auth 握手頭的 ClientWebSocket。
    private async Task<WebSocket> DefaultConnectAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Horus-Auth", Auth.Handshake(_cred.Key, _cfg.ExamId, _cfg.SeatId, _cfg.AgentId));
        if (_cred.SessionId is not null) ws.Options.SetRequestHeader("X-Horus-Session", _cred.SessionId);   // M4:OIDC 工作階段
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }

    /// 連接管理循環:連接 → hello → 續傳 → 收影格,斷線後指數退避重連。直到 ct 取消。
    public async Task RunAsync(CancellationToken ct)
    {
        int backoffMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            WebSocket? ws = null;
            try
            {
                ws = await _wsConnect(_wsUri, ct).ConfigureAwait(false);
                _ws = ws;
                backoffMs = 1000;                                  // 連接成功,退避歸零

                await SendHelloAsync(ws, ct).ConfigureAwait(false);
                await ReplayAsync(ws, ct).ConfigureAwait(false);   // 續傳緩衝中的事件/圖片
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false); // 阻塞至斷線
            }
            catch (OperationCanceledException) { break; }
            catch { /* 連接/收發失敗 → 退避重連 */ }
            finally
            {
                _ws = null;
                if (ws is not null) { try { ws.Dispose(); } catch { /* ignore */ } }
            }

            if (ct.IsCancellationRequested) break;
            int delay = backoffMs / 2 + Random.Shared.Next(backoffMs);   // ±50% 抖動:打散多 Agent 同步重連風暴
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoffMs = Math.Min(backoffMs * 2, 30_000);           // 上限 30s
        }
    }

    private async Task SendHelloAsync(WebSocket ws, CancellationToken ct)
    {
        string hello = JsonSerializer.Serialize(new
        {
            v = 1, type = "hello",
            agentId = _cfg.AgentId, examId = _cfg.ExamId, seatId = _cfg.SeatId, machineId = _cfg.MachineId,
            agentVersion = "0.1.0", ts = Now(),
        });
        await SendRawAsync(ws, hello, ct).ConfigureAwait(false);
    }

    /// 重連後續傳:**先補傳圖片,再重發事件**(與線上時"先傳圖後發事件"一致,
    /// 使引用該圖的事件在伺服器端能命中並標記 is_evidence)。伺服器按 (agent,seq,type)/image_id 冪等去重。
    private async Task ReplayAsync(WebSocket ws, CancellationToken ct)
    {
        foreach ((long seq, string? imageId, string trigger, ulong phash, byte[] webp) in _buffer.SnapshotPendingImages())
        {
            if (ct.IsCancellationRequested) return;
            (string? id, bool permanent) = await PostImageAsync(webp, trigger, phash, seq, imageId, ct).ConfigureAwait(false);
            // 成功 → 刪;**永久失敗(4xx:簽名錯/過大/考試已封存)也刪**,否則該壞圖每次重連都重傳、永久滯留佔盤。
            if (id is not null) _buffer.RemoveImage(seq);
            else if (permanent) { _buffer.RemoveImage(seq); Console.Error.WriteLine($"[horus-agent] 图片补传被永久拒收(seq={seq}),丢弃"); }
        }
        foreach ((long _, string json) in _buffer.SnapshotPendingEvents())
        {
            if (ct.IsCancellationRequested) return;
            await SendRawAsync(ws, json, ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string? msg = await ReceiveTextAsync(ws, ct).ConfigureAwait(false);
            if (msg is null) break;                                // 斷線/關閉
            try { await HandleFrameAsync(ws, msg, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* 單影格異常不影響連接 */ }
        }
    }

    private async Task HandleFrameAsync(WebSocket ws, string msg, CancellationToken ct)
    {
        using JsonDocument doc = JsonDocument.Parse(msg);
        JsonElement root = doc.RootElement;
        string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "hello_ack":
                if (root.TryGetProperty("maxSeq", out JsonElement ms) && ms.TryGetInt64(out long maxSeq))
                    AlignSeq(maxSeq);                              // 跨重新啟動對齊序號,避免重複使用
                break;
            case "ack":
                if (root.TryGetProperty("seq", out JsonElement up) && up.TryGetInt64(out long ackedSeq))
                    _buffer.RemoveEvent(ackedSeq);                 // 逐條精確刪除已持久化的 seq
                break;
            case "config_update":
                if (root.TryGetProperty("config", out JsonElement cfgEl))
                    OnConfigUpdate?.Invoke(cfgEl.Clone());
                break;
            case "capture_now":
                OnCaptureNow?.Invoke(root.TryGetProperty("reason", out JsonElement rs) ? rs.GetString() ?? "capture_now" : "capture_now");
                break;
            case "exam_ended":
                OnExamEnded?.Invoke();
                break;
            case "session_revoked":
                OnSessionRevoked?.Invoke();
                break;
            case "ping":
                await SendRawAsync(ws, "{\"v\":1,\"type\":\"pong\",\"ts\":" + Now().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", ct).ConfigureAwait(false);
                break;
            default: break;                                        // error / pong / 未知 → 忽略
        }
    }

    /// 傳送一條已封裝(含 hash/sig)的事件:**先落緩衝(持久),再盡力傳送**。ack 到達後壓實。
    public async Task SendEventAsync(string json, long seq, CancellationToken ct)
    {
        await _buffer.EnqueueEventAsync(seq, json).ConfigureAwait(false);
        WebSocket? ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try { await SendRawAsync(ws, json, ct).ConfigureAwait(false); }
            catch { /* 失敗留待重連續傳 */ }
        }
    }

    /// 上傳一張 WebP,回傳 imageId;失敗落緩衝。
    /// 觸發型抓圖**預生成用戶端 imageId**(事件據此關聯,離線也有效,重連補傳用同一 id);
    /// baseline 抓圖不預生成,由伺服器分配 + pHash 去重。
    public async Task<string?> UploadImageAsync(byte[] webp, string trigger, ulong phash, long seq, CancellationToken ct)
    {
        trigger = TriggerMap.ToContract(trigger);         // CaptureReason → 契約 trigger(否則寫入資料庫髒值)
        string? clientId = trigger == "baseline_random" ? null : NewImageId();
        (string? confirmed, bool permanent) = await PostImageAsync(webp, trigger, phash, seq, clientId, ct).ConfigureAwait(false);
        if (confirmed is null)
        {
            // 永久失敗(4xx)不緩衝(否則每次重連重傳永遠失敗);僅**臨時失敗**(網路/5xx)落緩衝待續傳。
            if (!permanent)
                await _buffer.EnqueueImageAsync(seq, clientId, trigger, phash, webp).ConfigureAwait(false);
            return clientId;   // 觸發型回傳預生成 id(事件關聯);baseline 為 null
        }
        return confirmed;
    }

    private static string NewImageId() => "img_" + Guid.NewGuid().ToString("N");

    /// 實際 HTTP 上傳(含 X-Horus-Sig 簽名;clientId 非空則帶 X-Horus-Image-Id 由伺服器沿用)。
    /// 回傳 (imageId, permanent):成功 → (id, false);**永久失敗(4xx,除 408/429)→ (null, true) 應丟棄**;
    /// 臨時失敗(5xx/408/429/網路/異常)→ (null, false) 應緩衝重試。
    private async Task<(string? id, bool permanent)> PostImageAsync(byte[] webp, string trigger, ulong phash, long seq, string? clientId, CancellationToken ct)
    {
        try
        {
            string phashHex = phash.ToString("x16");
            string ts = Now().ToString(System.Globalization.CultureInfo.InvariantCulture);
            string canon = Auth.ImageCanonicalHeaders(_cfg.ExamId, _cfg.SeatId, _cfg.AgentId, seq, trigger, phashHex, ts, clientId ?? "");
            string sig = Auth.ImageSig(_cred.Key, canon, webp);

            using var content = new ByteArrayContent(webp);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");

            using var req = new HttpRequestMessage(HttpMethod.Post, _httpUri) { Content = content };
            req.Headers.Add("X-Horus-Exam", _cfg.ExamId);
            req.Headers.Add("X-Horus-Seat", _cfg.SeatId);
            req.Headers.Add("X-Horus-Agent", _cfg.AgentId);
            req.Headers.Add("X-Horus-Seq", seq.ToString());
            req.Headers.Add("X-Horus-Trigger", trigger);
            req.Headers.Add("X-Horus-Phash", phashHex);
            req.Headers.Add("X-Horus-Ts", ts);
            req.Headers.Add("X-Horus-Sig", sig);
            if (_cred.SessionId is not null) req.Headers.Add("X-Horus-Session", _cred.SessionId);   // M4:OIDC 工作階段
            if (clientId is not null) req.Headers.Add("X-Horus-Image-Id", clientId);   // 用戶端預生成 id,伺服器沿用

            using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                int code = (int)resp.StatusCode;
                bool permanent = code is >= 400 and < 500 and not 408 and not 429;   // 4xx(除超時/限流)= 永久拒收
                return (null, permanent);
            }

            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            return (doc.RootElement.TryGetProperty("imageId", out JsonElement idEl) ? idEl.GetString() : null, false);
        }
        catch { return (null, false); }   // 網路/取消/解析異常 = 臨時,緩衝重試
    }

    // ---- 底層收發 ----
    private async Task SendRawAsync(WebSocket ws, string json, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private const int MaxFrameBytes = 1 * 1024 * 1024;   // 單條下行訊息上限 1MB
    // 讀超時:檢測 TCP 半開(拔網線/AP 掉線/NAT 過期,ReceiveAsync 永掛不回傳)。心跳每 30s → 伺服器 ack 每 30s
    // 餵給 receive,故健康連接下每次讀遠快於此;90s 無任何影格 = 連接已死,主動回傳 null 觸發重連。
    private const int ReceiveTimeoutSeconds = 90;

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ReceiveTimeoutSeconds));
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, timeoutCts.Token).ConfigureAwait(false); }
            catch { return null; }   // 斷線 / 半開讀超時 / 停機取消 → 視為斷開(RunAsync 據 ct 決定重連或退出)
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > MaxFrameBytes) return null;   // 超限 → 斷開
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void AlignSeq(long maxSeq)
    {
        long cur;
        do { cur = Interlocked.Read(ref _seq); if (maxSeq <= cur) return; }
        while (Interlocked.CompareExchange(ref _seq, maxSeq, cur) != cur);
        lock (_seqLock)
            if (maxSeq > _seqCeiling) { _seqCeiling = maxSeq + SeqBlock; _buffer.SaveSeqCeiling(_seqCeiling); }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public async ValueTask DisposeAsync()
    {
        try { _buffer.Compact(); } catch { /* 關停時把已確認的壓實掉 */ }
        WebSocket? ws = _ws;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { /* ignore */ }
            ws.Dispose();
        }
        _sendLock.Dispose();
        _http.Dispose();
    }
}
