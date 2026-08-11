namespace Horus.Server.Identity;

/// 最近一次**探活实际发生了什么**(不是「配置成什么」)。
///
/// ★★★ **为什么需要它**:预检原本只报「本机认哪些 `aud`」—— 那是**配置读数**,
///   它永远显示正常,哪怕对侧一次都没探到过、或者每一发都被拒。
///   本仓自己反复写过「配置化最容易做成装饰品,**断言必须落在行为上**」,
///   而那一项恰恰就是装饰品。这个类把它换成跑起来的事实。
///
/// ★ 它能直接答出的两件事,都是别处答不出的:
///   ① **探活从来没来过** —— 多半是贝塔通后台**没登记撤权回调**(探活地址按它同源推导,
///      所以那一条登记同时决定探活与撤权两件事);
///   ② **来了但被拒** —— 口径对不上(最常见:线上贝塔通还是 P100 之前的版本,还在发裸 `client_id`)。
///   ★★ 这两件事在别处**都没有症状**:贝塔通把 401 也算在线,唯一迹象在**它的**后台,
///   而那要有人去看才算数。
///
/// ★ 纯内存、进程重启即清 —— 它不是审计台账,只是给预检一个当下的事实。
///   丢了最多是「重启后显示成从未探过」,而探活每 5 分钟就来一次,一轮就补上。
public sealed class BetapassProbeState
{
    private readonly object _gate = new();
    private double _lastOkAt;
    private double _lastRejectedAt;
    private double _lastWarnAt;
    private string? _lastRejectReason;

    /// 验签通过。
    public void RecordOk(double now)
    {
        lock (_gate) { _lastOkAt = now; }
    }

    /// 验签被拒。@returns 这一发**该不该记 Warning**。
    ///
    /// ★ 按分钟节流:口径对不上时探活会**每 5 分钟失败一次、永远失败**,
    ///   照记就是一条永不停止的告警流;但完全不记(此前记的是 Debug)又等于没有任何主动信号。
    ///   ★★ 节流的另一半用途:这个端点是**公开可达**的,不节流的话谁都能拿垃圾令牌灌满日志。
    public bool RecordRejected(double now, string reason)
    {
        lock (_gate)
        {
            _lastRejectedAt = now;
            _lastRejectReason = reason;
            if (now - _lastWarnAt < 60) return false;
            _lastWarnAt = now;
            return true;
        }
    }

    /// 供预检读。三种状态:从未探过 / 最近一次被拒 / 最近一次通过。
    public (double? OkAt, double? RejectedAt, string? Reason) Snapshot()
    {
        lock (_gate)
        {
            return (_lastOkAt > 0 ? _lastOkAt : null,
                    _lastRejectedAt > 0 ? _lastRejectedAt : null,
                    _lastRejectReason);
        }
    }
}
