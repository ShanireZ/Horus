using System.Text.Json;
using Horus.Agent.Buffer;
using Horus.Agent.Capture;
using Horus.Agent.Config;
using Horus.Agent.Hardening;
using Horus.Agent.Identity;
using Horus.Agent.Integrity;
using Horus.Agent.Model;
using Horus.Agent.Signals;
using Horus.Agent.Transport;
using Horus.Contracts;   // AgentEvent / SignalType / Envelope(線協定共享)

namespace Horus.Agent;

/// 採集端入口。預設 MTA(利於 UIAutomation 用戶端);剪貼板監聽自帶 STA 執行緒。
/// 考試派發(owner 決策 2026-07-03):OIDC 模式 = **常態待命循環** —— 設定裡沒有 examId/seatId;
/// 輪詢伺服器「有無活躍考試」→ 有則彈瀏覽器 OIDC 登入(examId 伺服器端派發·seatId 由身分派生)→ 採集;
/// 收到 exam_ended / session_revoked(或工作階段探針發現失效)→ 停採、棄工作階段,回待命等下一場。
/// psk(legacy)模式保持單場語義:examId/seatId 仍來自設定,執行至 Ctrl+C。
internal static class Program
{
    private static int Main(string[] args)
    {
        string selfExe = Environment.ProcessPath ?? "";

        // 品質#6:備援未 observed 的 fire-and-forget 任務異常(事件回呼 async void 的異常棧不直觀)。
        // 至少落到 stderr 便於診斷,並標記已處理避免處理程序崩潰(異常仍不阻斷採集主循環)。
        TaskScheduler.UnobservedTaskException += (_, ea) =>
        {
            Console.Error.WriteLine($"[horus-agent] 未observed 任务异常: {ea.Exception?.GetType().Name}: {ea.Exception?.Message}");
            ea.SetObserved();
        };

        // ---- M5 保活模式散布(採集模式 = 無這些首參)----
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "install-service":
                    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("[horus-agent] 服务仅 Windows"); return 1; }
                    return Hardening.WindowsService.Install(selfExe, args.Skip(1).ToArray());
                case "uninstall-service":
                    if (!OperatingSystem.IsWindows()) return 1;
                    return Hardening.WindowsService.Uninstall();
                case "--service":
                    if (!OperatingSystem.IsWindows()) return 1;
                    Hardening.WindowsService.Run(selfExe, args.Skip(1).ToArray(), InstanceKey(args.Skip(1).ToArray()));
                    return 0;
                case "--watchdog":
                {
                    var rest = args.Skip(1).ToList();
                    int adopt = -1, ai = rest.IndexOf("--adopt");
                    if (ai >= 0 && ai + 1 < rest.Count && int.TryParse(rest[ai + 1], out int ap)) { adopt = ap; rest.RemoveRange(ai, 2); }
                    string[] wArgs = rest.ToArray();
                    using var wct = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; wct.Cancel(); };
                    return Hardening.Watchdog.RunSupervisor(selfExe, wArgs, adopt, InstanceKey(wArgs), serviceSession: false, wct.Token);
                }
            }
        }

        string cfgPath = args.Length > 0 ? args[0] : "agent.config.json";
        AgentConfig cfg;
        try { cfg = AgentConfig.Load(cfgPath); }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 配置加载失败: {ex.Message}"); return 1; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (!cfg.OidcMode)
        {
            // ---- psk(legacy)模式:examId/seatId 來自設定,單場執行至 Ctrl+C ----
            if (cfg.Psk is null) { Console.Error.WriteLine("[horus-agent] psk 模式需在配置提供 psk"); return 1; }
            if (cfg.ExamId.Length == 0 || cfg.SeatId.Length == 0)
            { Console.Error.WriteLine("[horus-agent] psk 模式需在配置提供 examId/seatId(oidc 模式二者由服务器派发)"); return 1; }
            RunCollectionSession(cfg, cfgPath, selfExe, new IngestCredential(cfg.Psk, null), probeHttp: null, cts.Token);
            return 0;
        }

        // ---- OIDC 模式:常態待命循環 ----
        Console.WriteLine("[horus-agent] OIDC 模式:待命中(不采集),等待服务器开考…");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (!cts.IsCancellationRequested)
        {
            if (!WaitForActiveExam(cfg, http, cts.Token)) break;   // 已取消

            OidcSession session;
            try
            {
                // ★ 如實告訴學員「要輸密碼」:取消 SSO 後(貝塔通 P84)每場考試都要完整認證一次,
                //   不再有「剛在別處登過所以秒進」。不說清楚的表現是學生盯著瀏覽器等它自己跳過去。
                Console.WriteLine("[horus-agent] 检测到活跃考试:即将打开系统浏览器,请用**贝塔通账号**登录(需输入密码,开了二次验证的还要再过一次)。");
                using var loginHttp = new HttpClient();
                session = OidcLoginFlow.LoginAsync(cfg, loginHttp, ct: cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[horus-agent] OIDC 登录失败: {ex.Message};10s 后重试。");
                if (cts.Token.WaitHandle.WaitOne(10_000)) break;
                continue;
            }

            cfg.ExamId = session.ExamId;   // 伺服器端派發的當前考試
            cfg.SeatId = session.SeatId;   // OIDC 身分派生(username)

            // ---- 開考預檢:這次登入撐不撐得完一場考試(貝塔通 P91)----
            // ★ 不自動重登:剛登入完再登一次是死循環,真正的修法在伺服器的 oidcSessionMinutes 上。
            //   所以這裡是**開考前一聲大的**,讓監考員當場看見,而不是考到一半整場一起掉線。
            double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (!session.CoversExam(nowUnix))
            {
                Console.Error.WriteLine(
                    $"[horus-agent] ⚠ 开考预检不通过:本次登录只剩 {session.RemainingMinutes(nowUnix):F0} 分钟,"
                    + $"而一场考试预计 {session.ExpectedExamMinutes} 分钟。考到一半会被服务器判过期并掉线。"
                    + "请监考员把服务器的 oidcSessionMinutes 调大后重启服务器,再让学员重新登录。");
            }

            // ---- 「當前登入:XXX + 一鍵退出」(貝塔通 R9)----
            //
            // ★★ 機房電腦**無還原卡**、瀏覽器混裝國產核心因而組策略不現實、共享 PC 模式也不現實。
            //   上一個人不點退出就走時,下一個人可以直接用他的工作階段 —— **身分層關不掉這個視窗**,
            //   唯一能真正生效的緩解就是讓下一個人**立刻看見這不是自己的號**。
            //   ★ 對考場尤其直接:學生輪流坐同一臺機器正是常態。
            // ★ 採集端是**控制檯程式**,沒有可以放一個按鈕的介面 —— 所以這裡是一整塊醒目的
            //   橫幅而不是一行紀錄,並明寫退出方式(Ctrl+C)。這是本形態下能做到的等價物,
            //   如實記下來,免得下一個人以為「一鍵退出按鈕」漏做了。
            string who = DisplayName(session.ProfileJson, cfg.SeatId);
            Console.WriteLine();
            Console.WriteLine("  ┌────────────────────────────────────────────────┐");
            Console.WriteLine($"  │ 当前登录：{who}");
            Console.WriteLine($"  │ 座位 {cfg.SeatId} · 考试 {cfg.ExamId}");
            Console.WriteLine("  │ 不是你本人？按 Ctrl+C 退出，再用自己的账号登录。");
            Console.WriteLine("  └────────────────────────────────────────────────┘");
            Console.WriteLine();
            Console.WriteLine($"[horus-agent] OIDC 登录成功 exam={cfg.ExamId} seat={cfg.SeatId},开始采集。");
            RunCollectionSession(cfg, cfgPath, selfExe, new IngestCredential(session.KSess, session.SessionId), http, cts.Token);

            if (cts.IsCancellationRequested) break;
            Console.WriteLine("[horus-agent] 本场结束,回待命(等待下一场考试)…");
        }
        return 0;
    }

    /// 從 `/oidc/exchange` 回的 profile 裡取一個給人看的名字。
    /// ★ 身分出口只有 `sub` / `name` / `username` 三項(貝塔通 P81)。
    ///   `name`(真實姓名)最適合給「這是不是我」這個判斷;取不到就退回使用者名,
    ///   再取不到才用座位號 —— **不能退成空白**,那就等於這塊橫幅沒寫。
    private static string DisplayName(string profileJson, string fallback)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(profileJson);
            JsonElement p = d.RootElement;
            string? name = p.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            string? user = p.TryGetProperty("username", out JsonElement u) ? u.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name))
                return string.IsNullOrWhiteSpace(user) ? name! : $"{name}（{user}）";
            if (!string.IsNullOrWhiteSpace(user)) return user!;
        }
        catch (JsonException) { /* profile 不是合法 JSON:退回下面的備援 */ }
        return fallback;
    }

    /// 待命輪詢:每 5s 查一次 /oidc/active-exam,直到出現活躍考試(true)或取消(false)。不採集、不抓屏、不連 WS。
    private static bool WaitForActiveExam(AgentConfig cfg, HttpClient http, CancellationToken ct)
    {
        bool noticePrinted = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string body = http.GetStringAsync($"{cfg.ServerHttpBase}/oidc/active-exam", ct).GetAwaiter().GetResult();
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("active", out JsonElement a) && a.GetBoolean()) return true;
                if (!noticePrinted) { Console.WriteLine("[horus-agent] 暂无活跃考试,待命轮询中…"); noticePrinted = true; }
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                if (!noticePrinted) { Console.Error.WriteLine($"[horus-agent] 服务器暂不可达({ex.Message}),待命轮询中…"); noticePrinted = true; }
            }
            if (ct.WaitHandle.WaitOne(5_000)) return false;
        }
        return false;
    }

    /// 跑一場採集工作階段:裝配信號源/上行/雜湊鏈,阻塞至「考試結束 / 被遠程登出 / 探針發現工作階段失效 / 全域退出」。
    /// probeHttp 非空(OIDC 模式)時啟用:下行影格回呼 + 每 60s 工作階段探針備援(覆蓋離線錯過推送的情形)。
    private static void RunCollectionSession(AgentConfig cfg, string cfgPath, string selfExe,
        IngestCredential cred, HttpClient? probeHttp, CancellationToken outerCt)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        CancellationToken ct = sessionCts.Token;

        var buffer = new LocalBuffer(Path.Combine(AppContext.BaseDirectory, "buffer"));
        // OIDC 換場/重登:舊工作階段 K_sess 已死,殘留緩衝永不可驗籤(bad_sig)→ 清掉,防每次重連重放-被拒。
        // seq 高水位保留(seq.state),且 hello_ack 會按伺服器 MAX(seq) 對齊,不會撞舊 seq。
        if (cred.SessionId is not null) buffer.PurgeSession();

        var uplink = new UplinkClient(cfg, buffer, cred);
        var chain = new HashChain(cred.Key);
        var sealLock = new object();
        var live = new LiveConfig(cfg);   // 可熱更新設定(白名單/閾值/截圖參數),各源每輪讀取

        // 上傳委託:WebP → imageId(自帶 seq)
        async Task<string?> Upload(byte[] webp, string trigger, ulong phash)
            => await uplink.UploadImageAsync(webp, trigger, phash, uplink.NextSeq(), ct);

        var capturer = new ScreenshotCapturer(live, Upload);

        // 監考員 capture_now → 立即抓一張;config_update → 熱更新 LiveConfig(下一輪採集即生效)
        uplink.OnCaptureNow = reason => _ = capturer.CaptureAsync(reason, dedupAgainstLast: false);
        uplink.OnConfigUpdate = json =>
        {
            live.Apply(json);
            Console.WriteLine("[horus-agent] 已应用 config_update 热更新");
        };
        // A3 防禦性自檢:capture_now handler 必須已注入,否則看板「點名抓圖」會靜默失效(伺服器端 pushed:true 但 Agent 不抓圖)。
        if (uplink.OnCaptureNow is null)
            Console.Error.WriteLine("[horus-agent] 警告: capture_now 处理器未注入 → 看板点名抓图将静默失效");
        // 考試結束(線上推送 / 重連 hello 補發):留 5s 排空緩衝續傳視窗,再停採回待命。
        uplink.OnExamEnded = () =>
        {
            Console.WriteLine("[horus-agent] 考试已结束:5s 内排空缓冲后停止采集。");
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { /* 已在停 */ }
                sessionCts.Cancel();
            });
        };
        // 被監考員遠程登出:工作階段已吊銷、連接隨後被強斷,續傳無望 → 立即停。
        uplink.OnSessionRevoked = () =>
        {
            Console.WriteLine("[horus-agent] 已被监考员远程登出:停止采集,回待命。");
            sessionCts.Cancel();
        };

        // 事件管線:RawSignal →(必要時抓圖)→ 蓋章(ts/seq/hash)→ 傳送
        async void Handle(RawSignal raw)
        {
            try
            {
                string? imageId = raw.TriggerCapture
                    ? await capturer.CaptureAsync(raw.CaptureReason ?? "event", dedupAgainstLast: true)
                    : null;

                var core = new AgentEvent
                {
                    ExamId = cfg.ExamId, SeatId = cfg.SeatId, AgentId = cfg.AgentId, MachineId = cfg.MachineId,
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    Type = raw.Type, Payload = raw.Payload, Risk = raw.Risk,
                    EvidenceImageId = imageId,
                };

                string json;
                long seq;
                lock (sealLock)   // 串行化:保證雜湊鏈順序與 seq 一致
                {
                    seq = uplink.NextSeq();
                    AgentEvent stamped = core with { Seq = seq };
                    var (hp, hs, sig) = chain.Seal(stamped, seq);
                    json = Envelope.Serialize(stamped with { HashPrev = hp, HashSelf = hs }, sig);
                }
                await uplink.SendEventAsync(json, seq, ct);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 处理信号异常: {ex.Message}"); }
        }

        // 裝配信號源
        var sources = new List<ISignalSource>
        {
            new ForegroundWindowSource(),
            new BrowserUrlSource(live),
            new ProcessWatcher(live),
            new ClipboardWatcher(live),
            new UsbWatcher(),
        };
        foreach (ISignalSource s in sources) s.Signal += Handle;

        // 連接管理(握手/hello/續傳/斷線重連)在後台常駐,直到工作階段取消
        Task uplinkTask = Task.Run(() => uplink.RunAsync(ct));
        var failedSources = new List<string>();
        foreach (ISignalSource s in sources)
        {
            try { s.Start(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[horus-agent] 启动 {s.Name} 失败: {ex.Message}");
                failedSources.Add(s.Name);
            }
        }

        // ---- M5 採集端硬化:健康信號自檢上報(純檢測:讓規避暴露,不阻斷)----
        // ① 遮蔽檢測:每影格 luma 統計 → 分類 → 進入遮蔽態時上報一次(退出後可再報)。
        ObscureReason lastObscure = ObscureReason.None;
        capturer.OnStats = stats =>
        {
            ObscureReason r = ScreenQuality.Classify(stats);
            if (r == ObscureReason.None) { lastObscure = ObscureReason.None; return; }
            if (r == lastObscure) return;   // 同一遮蔽態不重複刷
            lastObscure = r;
            Handle(new RawSignal(SignalType.ScreenshotObscured, new()
            {
                ["reason"] = ScreenQuality.ReasonLabel(r),
                ["variance"] = stats.LumaVariance, ["width"] = stats.Width, ["height"] = stats.Height,
            }, Risk: RiskScores.ScreenObscured));
        };
        // ② 異常重新啟動取證:啟動讀舊標記,若上次未正常退出 → 上報 watchdog_restart。
        string markerPath = Path.Combine(AppContext.BaseDirectory, "horus-agent.marker");
        if (RestartClassifier.IsUnexpectedRestart(RestartMarker.ReadThenMarkRunning(markerPath)))
            Handle(new RawSignal(SignalType.WatchdogRestart, new() { ["reason"] = "unexpected_prev_exit" }, Risk: RiskScores.WatchdogRestart));
        // ③ 能力降級:非管理員 / 信號源啟動失敗 → 上報 capability_degraded。
        if (!WinPrivilege.IsAdministrator())
            Handle(new RawSignal(SignalType.CapabilityDegraded,
                new() { ["capability"] = "admin", ["status"] = "not_elevated", ["detail"] = "采集需管理员权限跑 ETW/UIA/WMI" }, Risk: RiskScores.CapabilityDegraded));
        foreach (string name in failedSources)
            Handle(new RawSignal(SignalType.CapabilityDegraded,
                new() { ["capability"] = name, ["status"] = "start_failed" }, Risk: RiskScores.CapabilityDegraded));

        _ = Task.Run(() => BaselineLoop(live, capturer, ct));
        _ = Task.Run(() => HeartbeatLoop(Handle, ct));
        _ = Task.Run(() => HealthLoop(Handle, ct));   // ④ 掛起檢測
        // ⑤ 層2 互拉:若由看門狗拉起(HORUS_WATCHDOG_PID),守護看門狗——它被殺則重拉一個 adopt 本處理程序的新看門狗。
        // 鍵 = agentId_machineId(與考試無關:考試派發後設定裡已無 exam/seat,且看門狗單例本就該按機器算)。
        if (int.TryParse(Environment.GetEnvironmentVariable("HORUS_WATCHDOG_PID"), out int wdPid) && wdPid > 0)
            _ = Task.Run(() => Watchdog.GuardWatchdogAsync(wdPid, selfExe, new[] { cfgPath }, cfg.AgentId + "_" + cfg.MachineId, ct));
        // ⑥ 工作階段探針(OIDC):每 60s 檢核工作階段仍有效 + 考試仍 active —— 兜住「離線時被遠程登出/考試結束」錯過推送的情形。
        if (cred.SessionId is not null && probeHttp is not null)
            _ = Task.Run(() => SessionProbeLoop(cfg, probeHttp, cred.SessionId, sessionCts));

        Console.WriteLine($"[horus-agent] 采集中 seat={cfg.SeatId} exam={cfg.ExamId}。Ctrl+C 退出。");
        ct.WaitHandle.WaitOne();

        RestartMarker.MarkClean(markerPath);   // M5:正常退出寫 clean → 下次啟動不誤判異常重新啟動
        foreach (ISignalSource s in sources) { try { s.Stop(); s.Dispose(); } catch { /* ignore */ } }
        try { uplinkTask.Wait(TimeSpan.FromSeconds(3)); } catch { /* 等連接循環退出再釋放,避免 dispose 競態 */ }
        uplink.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// 工作階段探針:GET /oidc/session(帶 X-Horus-Session)。valid=false → 已被登出/過期,立即停;
    /// examStatus 非 active → 考試已結束(錯過推送),留 5s 排空後停。網路錯誤忽略(斷網由緩衝續傳兜)。
    private static async Task SessionProbeLoop(AgentConfig cfg, HttpClient http, string sessionId, CancellationTokenSource sessionCts)
    {
        CancellationToken ct = sessionCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
            catch (TaskCanceledException) { break; }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{cfg.ServerHttpBase}/oidc/session");
                req.Headers.Add("X-Horus-Session", sessionId);
                using HttpResponseMessage resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;   // 伺服器異常 ≠ 工作階段失效
                using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                JsonElement root = doc.RootElement;
                if (!root.GetProperty("valid").GetBoolean())
                {
                    Console.WriteLine("[horus-agent] 会话探针:会话已失效(被远程登出/过期),停止采集回待命。");
                    sessionCts.Cancel();
                    break;
                }
                string status = root.TryGetProperty("examStatus", out JsonElement st) ? st.GetString() ?? "unknown" : "unknown";
                if (status is not ("active" or "unknown"))
                {
                    Console.WriteLine("[horus-agent] 会话探针:考试已结束,5s 内排空缓冲后停止采集。");
                    try { await Task.Delay(5_000, ct); } catch (TaskCanceledException) { /* 已在停 */ }
                    sessionCts.Cancel();
                    break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 網路抖動忽略,下輪再探 */ }
        }
    }

    /// 看門狗單例鍵 / adopt 用:agentId_machineId(每機穩定,與考試無關;設定裡已無 exam/seat)。載入失敗退回 default。
    private static string InstanceKey(string[] args)
    {
        try { AgentConfig c = AgentConfig.Load(args.Length > 0 ? args[0] : "agent.config.json"); return c.AgentId + "_" + c.MachineId; }
        catch { return "default"; }
    }

    /// 隨機基線抓圖(30–90s,不去重——每張都可能是抓 IDE 外掛的孤證)。區間可熱更新。
    private static async Task BaselineLoop(LiveConfig live, ScreenshotCapturer cap, CancellationToken ct)
    {
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            // min/max 是兩個獨立 volatile,熱更新非原子寫有瞬時倒掛視窗(讀到新 min 配舊 max)。
            // 本機讀入並鉗正 max≥min,杜絕 rng.Next(min>maxExclusive) 拋異常逃出循環、永久停掉基線抓圖。
            int mn = live.BaselineMinSeconds, mx = live.BaselineMaxSeconds;
            if (mx < mn) mx = mn;
            int wait = rng.Next(mn, mx + 1);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (TaskCanceledException) { break; }
            try { await cap.CaptureAsync("baseline_random", dedupAgainstLast: false); }
            catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 基线抓图异常: {ex.Message}"); }
        }
    }

    /// 心跳:既是看板的「座位線上」指示,**也是三道門裡心跳與 idle 兩道的續期**(貝塔通 P88–P92)。
    ///
    /// ★ **重複使用既有上行通道,不另開心跳端點** —— Agent 本來就在持續上傳事件流,
    ///   再加一條 HTTP 心跳只是把同一件事做兩遍(其 rp-contract 對採集端明寫可以重複使用)。
    /// ★★ `active` **由 Agent 自己定義**(機器上有無使用者活動),不是瀏覽器那套
    ///   `visibilityState` + `mousemove` —— 見 <see cref="Horus.Agent.Signals.UserActivity"/>。
    /// ★ 30 秒一發遠密於契約給網頁的 5 分鐘,而伺服器端有 150 秒節流,所以既不會壓垮寫入,
    ///   也讓「心跳 15 分鐘」那道門有 30 次容錯而不是 3 次。
    private static async Task HeartbeatLoop(Action<RawSignal> emit, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            emit(new RawSignal(SignalType.Heartbeat, new()
            {
                ["status"] = "alive",
                ["active"] = Horus.Agent.Signals.UserActivity.IsActive(),
            }));
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// M5 防掛起:每 30s 觀測 wall-clock;相鄰跳變超閾值(期望 30s × 3)→ 上報 suspected_suspend
    /// (處理程序被 suspend / 系統睡眠 / 鎖屏時 Task.Delay 與處理程序一同掛起,恢復後 gap = 間隔 + 掛起時長)。
    private static async Task HealthLoop(Action<RawSignal> emit, CancellationToken ct)
    {
        var suspend = new SuspendMonitor(expectedIntervalSec: 30, toleranceFactor: 3.0);
        while (!ct.IsCancellationRequested)
        {
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            double? gapMs = suspend.Observe(now);
            if (gapMs is not null)
                emit(new RawSignal(SignalType.SuspectedSuspend,
                    new() { ["gapMs"] = gapMs.Value, ["expectedMs"] = 30000.0 }));
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (TaskCanceledException) { break; }
        }
    }
}
