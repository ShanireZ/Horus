using Honus.Agent.Buffer;
using Honus.Agent.Capture;
using Honus.Agent.Config;
using Honus.Agent.Integrity;
using Honus.Agent.Model;
using Honus.Agent.Signals;
using Honus.Agent.Transport;
using Honus.Contracts;   // AgentEvent / SignalType / Envelope(线协议共享)

namespace Honus.Agent;

/// 采集端入口。默认 MTA(利于 UIAutomation 客户端);剪贴板监听自带 STA 线程。
internal static class Program
{
    private static int Main(string[] args)
    {
        string cfgPath = args.Length > 0 ? args[0] : "agent.config.json";
        AgentConfig cfg;
        try { cfg = AgentConfig.Load(cfgPath); }
        catch (Exception ex) { Console.Error.WriteLine($"[honus-agent] 配置加载失败: {ex.Message}"); return 1; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var buffer = new LocalBuffer(Path.Combine(AppContext.BaseDirectory, "buffer"));
        var uplink = new UplinkClient(cfg, buffer);
        var chain = new HashChain(cfg.Psk);
        var sealLock = new object();

        // 上传委托:WebP → imageId(自带 seq)
        async Task<string?> Upload(byte[] webp, string trigger, ulong phash)
            => await uplink.UploadImageAsync(webp, trigger, phash, uplink.NextSeq(), cts.Token);

        var capturer = new ScreenshotCapturer(cfg.TargetHeight, cfg.WebpQuality, Upload);

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
            catch (Exception ex) { Console.Error.WriteLine($"[honus-agent] 处理信号异常: {ex.Message}"); }
        }

        // 装配信号源
        var sources = new List<ISignalSource>
        {
            new ForegroundWindowSource(),
            new BrowserUrlSource(cfg.WhitelistHosts),
            new ProcessWatcher(cfg.WhitelistProcs),
            new ClipboardWatcher(cfg.LargePasteThreshold),
            new UsbWatcher(),
        };
        foreach (ISignalSource s in sources) s.Signal += Handle;

        uplink.ConnectAsync(cts.Token).GetAwaiter().GetResult();
        foreach (ISignalSource s in sources)
        {
            try { s.Start(); }
            catch (Exception ex) { Console.Error.WriteLine($"[honus-agent] 启动 {s.Name} 失败: {ex.Message}"); }
        }

        _ = Task.Run(() => BaselineLoop(cfg, capturer, cts.Token));
        _ = Task.Run(() => HeartbeatLoop(Handle, cts.Token));

        Console.WriteLine($"[honus-agent] 运行中 seat={cfg.SeatId} exam={cfg.ExamId}。Ctrl+C 退出。");
        cts.Token.WaitHandle.WaitOne();

        foreach (ISignalSource s in sources) { try { s.Stop(); s.Dispose(); } catch { /* ignore */ } }
        uplink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    /// 随机基线抓图(30–90s,不去重——每张都可能是抓 IDE 插件的孤证)。
    private static async Task BaselineLoop(AgentConfig cfg, ScreenshotCapturer cap, CancellationToken ct)
    {
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            int wait = rng.Next(cfg.BaselineMinSeconds, cfg.BaselineMaxSeconds + 1);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (TaskCanceledException) { break; }
            try { await cap.CaptureAsync("baseline_random", dedupAgainstLast: false); }
            catch (Exception ex) { Console.Error.WriteLine($"[honus-agent] 基线抓图异常: {ex.Message}"); }
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
