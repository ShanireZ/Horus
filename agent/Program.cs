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
using Horus.Contracts;   // AgentEvent / SignalType / Envelope(线协议共享)

namespace Horus.Agent;

/// 采集端入口。默认 MTA(利于 UIAutomation 客户端);剪贴板监听自带 STA 线程。
/// 考试派发(owner 决策 2026-07-03):OIDC 模式 = **常态待命循环** —— 配置里没有 examId/seatId;
/// 轮询服务器「有无活跃考试」→ 有则弹浏览器 OIDC 登录(examId 服务端派发·seatId 由身份派生)→ 采集;
/// 收到 exam_ended / session_revoked(或会话探针发现失效)→ 停采、弃会话,回待命等下一场。
/// psk(legacy)模式保持单场语义:examId/seatId 仍来自配置,运行至 Ctrl+C。
internal static class Program
{
    private static int Main(string[] args)
    {
        string selfExe = Environment.ProcessPath ?? "";

        // 质量#6:兜底未 observed 的 fire-and-forget 任务异常(事件回调 async void 的异常栈不直观)。
        // 至少落到 stderr 便于诊断,并标记已处理避免进程崩溃(异常仍不阻断采集主循环)。
        TaskScheduler.UnobservedTaskException += (_, ea) =>
        {
            Console.Error.WriteLine($"[horus-agent] 未observed 任务异常: {ea.Exception?.GetType().Name}: {ea.Exception?.Message}");
            ea.SetObserved();
        };

        // ---- M5 保活模式分发(采集模式 = 无这些首参)----
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
            // ---- psk(legacy)模式:examId/seatId 来自配置,单场运行至 Ctrl+C ----
            if (cfg.Psk is null) { Console.Error.WriteLine("[horus-agent] psk 模式需在配置提供 psk"); return 1; }
            if (cfg.ExamId.Length == 0 || cfg.SeatId.Length == 0)
            { Console.Error.WriteLine("[horus-agent] psk 模式需在配置提供 examId/seatId(oidc 模式二者由服务器派发)"); return 1; }
            RunCollectionSession(cfg, cfgPath, selfExe, new IngestCredential(cfg.Psk, null), probeHttp: null, cts.Token);
            return 0;
        }

        // ---- OIDC 模式:常态待命循环 ----
        Console.WriteLine("[horus-agent] OIDC 模式:待命中(不采集),等待服务器开考…");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (!cts.IsCancellationRequested)
        {
            if (!WaitForActiveExam(cfg, http, cts.Token)) break;   // 已取消

            OidcSession session;
            try
            {
                Console.WriteLine("[horus-agent] 检测到活跃考试:即将打开系统浏览器完成 wentian 授权…");
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

            cfg.ExamId = session.ExamId;   // 服务端派发的当前考试
            cfg.SeatId = session.SeatId;   // OIDC 身份派生(username)

            // ---- 开考预检:这次登录撑不撑得完一场考试(贝塔通 P91)----
            // ★ 不自动重登:刚登录完再登一次是死循环,真正的修法在服务器的 oidcSessionMinutes 上。
            //   所以这里是**开考前一声大的**,让监考员当场看见,而不是考到一半整场一起掉线。
            double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (!session.CoversExam(nowUnix))
            {
                Console.Error.WriteLine(
                    $"[horus-agent] ⚠ 开考预检不通过:本次登录只剩 {session.RemainingMinutes(nowUnix):F0} 分钟,"
                    + $"而一场考试预计 {session.ExpectedExamMinutes} 分钟。考到一半会被服务器判过期并掉线。"
                    + "请监考员把服务器的 oidcSessionMinutes 调大后重启服务器,再让学员重新登录。");
            }

            // ★ 「当前登录:XXX」—— 机房电脑轮流坐人,让下一个人立刻看见这不是自己的号(贝塔通 R9)。
            Console.WriteLine($"[horus-agent] OIDC 登录成功 exam={cfg.ExamId} seat={cfg.SeatId},开始采集。");
            RunCollectionSession(cfg, cfgPath, selfExe, new IngestCredential(session.KSess, session.SessionId), http, cts.Token);

            if (cts.IsCancellationRequested) break;
            Console.WriteLine("[horus-agent] 本场结束,回待命(等待下一场考试)…");
        }
        return 0;
    }

    /// 待命轮询:每 5s 查一次 /oidc/active-exam,直到出现活跃考试(true)或取消(false)。不采集、不抓屏、不连 WS。
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

    /// 跑一场采集会话:装配信号源/上行/哈希链,阻塞至「考试结束 / 被远程登出 / 探针发现会话失效 / 全局退出」。
    /// probeHttp 非空(OIDC 模式)时启用:下行帧回调 + 每 60s 会话探针兜底(覆盖离线错过推送的情形)。
    private static void RunCollectionSession(AgentConfig cfg, string cfgPath, string selfExe,
        IngestCredential cred, HttpClient? probeHttp, CancellationToken outerCt)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        CancellationToken ct = sessionCts.Token;

        var buffer = new LocalBuffer(Path.Combine(AppContext.BaseDirectory, "buffer"));
        // OIDC 换场/重登:旧会话 K_sess 已死,残留缓冲永不可验签(bad_sig)→ 清掉,防每次重连重放-被拒。
        // seq 高水位保留(seq.state),且 hello_ack 会按服务器 MAX(seq) 对齐,不会撞旧 seq。
        if (cred.SessionId is not null) buffer.PurgeSession();

        var uplink = new UplinkClient(cfg, buffer, cred);
        var chain = new HashChain(cred.Key);
        var sealLock = new object();
        var live = new LiveConfig(cfg);   // 可热更新配置(白名单/阈值/截图参数),各源每轮读取

        // 上传委托:WebP → imageId(自带 seq)
        async Task<string?> Upload(byte[] webp, string trigger, ulong phash)
            => await uplink.UploadImageAsync(webp, trigger, phash, uplink.NextSeq(), ct);

        var capturer = new ScreenshotCapturer(live, Upload);

        // 监考员 capture_now → 立即抓一张;config_update → 热更新 LiveConfig(下一轮采集即生效)
        uplink.OnCaptureNow = reason => _ = capturer.CaptureAsync(reason, dedupAgainstLast: false);
        uplink.OnConfigUpdate = json =>
        {
            live.Apply(json);
            Console.WriteLine("[horus-agent] 已应用 config_update 热更新");
        };
        // A3 防御性自检:capture_now handler 必须已注入,否则看板「点名抓图」会静默失效(服务端 pushed:true 但 Agent 不抓图)。
        if (uplink.OnCaptureNow is null)
            Console.Error.WriteLine("[horus-agent] 警告: capture_now 处理器未注入 → 看板点名抓图将静默失效");
        // 考试结束(在线推送 / 重连 hello 补发):留 5s 排空缓冲续传窗口,再停采回待命。
        uplink.OnExamEnded = () =>
        {
            Console.WriteLine("[horus-agent] 考试已结束:5s 内排空缓冲后停止采集。");
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(5_000, ct); } catch (OperationCanceledException) { /* 已在停 */ }
                sessionCts.Cancel();
            });
        };
        // 被监考员远程登出:会话已吊销、连接随后被强断,续传无望 → 立即停。
        uplink.OnSessionRevoked = () =>
        {
            Console.WriteLine("[horus-agent] 已被监考员远程登出:停止采集,回待命。");
            sessionCts.Cancel();
        };

        // 事件管线:RawSignal →(必要时抓图)→ 盖章(ts/seq/hash)→ 发送
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
                lock (sealLock)   // 串行化:保证哈希链顺序与 seq 一致
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

        // 装配信号源
        var sources = new List<ISignalSource>
        {
            new ForegroundWindowSource(),
            new BrowserUrlSource(live),
            new ProcessWatcher(live),
            new ClipboardWatcher(live),
            new UsbWatcher(),
        };
        foreach (ISignalSource s in sources) s.Signal += Handle;

        // 连接管理(握手/hello/续传/断线重连)在后台常驻,直到会话取消
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

        // ---- M5 采集端硬化:健康信号自检上报(纯检测:让规避暴露,不阻断)----
        // ① 遮蔽检测:每帧 luma 统计 → 分类 → 进入遮蔽态时上报一次(退出后可再报)。
        ObscureReason lastObscure = ObscureReason.None;
        capturer.OnStats = stats =>
        {
            ObscureReason r = ScreenQuality.Classify(stats);
            if (r == ObscureReason.None) { lastObscure = ObscureReason.None; return; }
            if (r == lastObscure) return;   // 同一遮蔽态不重复刷
            lastObscure = r;
            Handle(new RawSignal(SignalType.ScreenshotObscured, new()
            {
                ["reason"] = ScreenQuality.ReasonLabel(r),
                ["variance"] = stats.LumaVariance, ["width"] = stats.Width, ["height"] = stats.Height,
            }, Risk: RiskScores.ScreenObscured));
        };
        // ② 异常重启取证:启动读旧标记,若上次未正常退出 → 上报 watchdog_restart。
        string markerPath = Path.Combine(AppContext.BaseDirectory, "horus-agent.marker");
        if (RestartClassifier.IsUnexpectedRestart(RestartMarker.ReadThenMarkRunning(markerPath)))
            Handle(new RawSignal(SignalType.WatchdogRestart, new() { ["reason"] = "unexpected_prev_exit" }, Risk: RiskScores.WatchdogRestart));
        // ③ 能力降级:非管理员 / 信号源启动失败 → 上报 capability_degraded。
        if (!WinPrivilege.IsAdministrator())
            Handle(new RawSignal(SignalType.CapabilityDegraded,
                new() { ["capability"] = "admin", ["status"] = "not_elevated", ["detail"] = "采集需管理员权限跑 ETW/UIA/WMI" }, Risk: RiskScores.CapabilityDegraded));
        foreach (string name in failedSources)
            Handle(new RawSignal(SignalType.CapabilityDegraded,
                new() { ["capability"] = name, ["status"] = "start_failed" }, Risk: RiskScores.CapabilityDegraded));

        _ = Task.Run(() => BaselineLoop(live, capturer, ct));
        _ = Task.Run(() => HeartbeatLoop(Handle, ct));
        _ = Task.Run(() => HealthLoop(Handle, ct));   // ④ 挂起检测
        // ⑤ 层2 互拉:若由看门狗拉起(HORUS_WATCHDOG_PID),守护看门狗——它被杀则重拉一个 adopt 本进程的新看门狗。
        // 键 = agentId_machineId(与考试无关:考试派发后配置里已无 exam/seat,且看门狗单例本就该按机器算)。
        if (int.TryParse(Environment.GetEnvironmentVariable("HORUS_WATCHDOG_PID"), out int wdPid) && wdPid > 0)
            _ = Task.Run(() => Watchdog.GuardWatchdogAsync(wdPid, selfExe, new[] { cfgPath }, cfg.AgentId + "_" + cfg.MachineId, ct));
        // ⑥ 会话探针(OIDC):每 60s 校验会话仍有效 + 考试仍 active —— 兜住「离线时被远程登出/考试结束」错过推送的情形。
        if (cred.SessionId is not null && probeHttp is not null)
            _ = Task.Run(() => SessionProbeLoop(cfg, probeHttp, cred.SessionId, sessionCts));

        Console.WriteLine($"[horus-agent] 采集中 seat={cfg.SeatId} exam={cfg.ExamId}。Ctrl+C 退出。");
        ct.WaitHandle.WaitOne();

        RestartMarker.MarkClean(markerPath);   // M5:正常退出写 clean → 下次启动不误判异常重启
        foreach (ISignalSource s in sources) { try { s.Stop(); s.Dispose(); } catch { /* ignore */ } }
        try { uplinkTask.Wait(TimeSpan.FromSeconds(3)); } catch { /* 等连接循环退出再释放,避免 dispose 竞态 */ }
        uplink.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// 会话探针:GET /oidc/session(带 X-Horus-Session)。valid=false → 已被登出/过期,立即停;
    /// examStatus 非 active → 考试已结束(错过推送),留 5s 排空后停。网络错误忽略(断网由缓冲续传兜)。
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
                if (!resp.IsSuccessStatusCode) continue;   // 服务器异常 ≠ 会话失效
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
            catch { /* 网络抖动忽略,下轮再探 */ }
        }
    }

    /// 看门狗单例键 / adopt 用:agentId_machineId(每机稳定,与考试无关;配置里已无 exam/seat)。加载失败回退 default。
    private static string InstanceKey(string[] args)
    {
        try { AgentConfig c = AgentConfig.Load(args.Length > 0 ? args[0] : "agent.config.json"); return c.AgentId + "_" + c.MachineId; }
        catch { return "default"; }
    }

    /// 随机基线抓图(30–90s,不去重——每张都可能是抓 IDE 插件的孤证)。区间可热更新。
    private static async Task BaselineLoop(LiveConfig live, ScreenshotCapturer cap, CancellationToken ct)
    {
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            // min/max 是两个独立 volatile,热更新非原子写有瞬时倒挂窗口(读到新 min 配旧 max)。
            // 本地读入并钳正 max≥min,杜绝 rng.Next(min>maxExclusive) 抛异常逃出循环、永久停掉基线抓图。
            int mn = live.BaselineMinSeconds, mx = live.BaselineMaxSeconds;
            if (mx < mn) mx = mn;
            int wait = rng.Next(mn, mx + 1);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (TaskCanceledException) { break; }
            try { await cap.CaptureAsync("baseline_random", dedupAgainstLast: false); }
            catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 基线抓图异常: {ex.Message}"); }
        }
    }

    /// 心跳:既是看板的「座位在线」指示,**也是三道门里心跳与 idle 两道的续期**(贝塔通 P88–P92)。
    ///
    /// ★ **复用既有上行通道,不另开心跳端点** —— Agent 本来就在持续上传事件流,
    ///   再加一条 HTTP 心跳只是把同一件事做两遍(其 rp-contract 对采集端明写可以复用)。
    /// ★★ `active` **由 Agent 自己定义**(机器上有无用户活动),不是浏览器那套
    ///   `visibilityState` + `mousemove` —— 见 <see cref="Horus.Agent.Signals.UserActivity"/>。
    /// ★ 30 秒一发远密于契约给网页的 5 分钟,而服务端有 150 秒节流,所以既不会压垮写入,
    ///   也让「心跳 15 分钟」那道门有 30 次容错而不是 3 次。
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

    /// M5 防挂起:每 30s 观测 wall-clock;相邻跳变超阈值(期望 30s × 3)→ 上报 suspected_suspend
    /// (进程被 suspend / 系统睡眠 / 锁屏时 Task.Delay 与进程一同挂起,恢复后 gap = 间隔 + 挂起时长)。
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
