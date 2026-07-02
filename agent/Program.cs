using Horus.Agent.Buffer;
using Horus.Agent.Capture;
using Horus.Agent.Config;
using Horus.Agent.Identity;
using Horus.Agent.Integrity;
using Horus.Agent.Model;
using Horus.Agent.Signals;
using Horus.Agent.Transport;
using Horus.Contracts;   // AgentEvent / SignalType / Envelope(线协议共享)

namespace Horus.Agent;

/// 采集端入口。默认 MTA(利于 UIAutomation 客户端);剪贴板监听自带 STA 线程。
internal static class Program
{
    private static int Main(string[] args)
    {
        string cfgPath = args.Length > 0 ? args[0] : "agent.config.json";
        AgentConfig cfg;
        try { cfg = AgentConfig.Load(cfgPath); }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 配置加载失败: {ex.Message}"); return 1; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var buffer = new LocalBuffer(Path.Combine(AppContext.BaseDirectory, "buffer"));

        // M4·A1/A2:采集凭证 —— OIDC 模式先经 cpplearn 登录换会话(浏览器·near-无感),得 K_sess + sessionId;
        // PSK 模式沿用共享 PSK。登录前不连服务器、不采集(A3:未认证不上报)。
        IngestCredential cred;
        if (cfg.OidcMode)
        {
            try
            {
                Console.WriteLine("[horus-agent] OIDC 登录:即将打开系统浏览器完成 cpplearn 授权…");
                using var loginHttp = new HttpClient();
                OidcSession session = OidcLoginFlow.LoginAsync(cfg, loginHttp, ct: cts.Token).GetAwaiter().GetResult();
                cred = new IngestCredential(session.KSess, session.SessionId);
                Console.WriteLine("[horus-agent] OIDC 登录成功,会话已建立。");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] OIDC 登录失败: {ex.Message}"); return 1; }
        }
        else
        {
            if (cfg.Psk is null) { Console.Error.WriteLine("[horus-agent] psk 模式需在配置提供 psk"); return 1; }
            cred = new IngestCredential(cfg.Psk, null);
        }

        var uplink = new UplinkClient(cfg, buffer, cred);
        var chain = new HashChain(cred.Key);
        var sealLock = new object();
        var live = new LiveConfig(cfg);   // 可热更新配置(白名单/阈值/截图参数),各源每轮读取

        // 上传委托:WebP → imageId(自带 seq)
        async Task<string?> Upload(byte[] webp, string trigger, ulong phash)
            => await uplink.UploadImageAsync(webp, trigger, phash, uplink.NextSeq(), cts.Token);

        var capturer = new ScreenshotCapturer(live, Upload);

        // 监考员 capture_now → 立即抓一张;config_update → 热更新 LiveConfig(下一轮采集即生效)
        uplink.OnCaptureNow = reason => _ = capturer.CaptureAsync(reason, dedupAgainstLast: false);
        uplink.OnConfigUpdate = json =>
        {
            live.Apply(json);
            Console.WriteLine("[horus-agent] 已应用 config_update 热更新");
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
                await uplink.SendEventAsync(json, seq, cts.Token);
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

        // 连接管理(握手/hello/续传/断线重连)在后台常驻,直到 cts 取消
        Task uplinkTask = Task.Run(() => uplink.RunAsync(cts.Token));
        foreach (ISignalSource s in sources)
        {
            try { s.Start(); }
            catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 启动 {s.Name} 失败: {ex.Message}"); }
        }

        _ = Task.Run(() => BaselineLoop(live, capturer, cts.Token));
        _ = Task.Run(() => HeartbeatLoop(Handle, cts.Token));

        Console.WriteLine($"[horus-agent] 运行中 seat={cfg.SeatId} exam={cfg.ExamId}。Ctrl+C 退出。");
        cts.Token.WaitHandle.WaitOne();

        foreach (ISignalSource s in sources) { try { s.Stop(); s.Dispose(); } catch { /* ignore */ } }
        try { uplinkTask.Wait(TimeSpan.FromSeconds(3)); } catch { /* 等连接循环退出再释放,避免 dispose 竞态 */ }
        uplink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
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

    private static async Task HeartbeatLoop(Action<RawSignal> emit, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            emit(new RawSignal(SignalType.Heartbeat, new() { ["status"] = "alive" }));
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (TaskCanceledException) { break; }
        }
    }
}
