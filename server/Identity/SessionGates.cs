namespace Horus.Server.Identity;

/// 本地会话失效的原因。<see cref="Ok"/> 之外的每一项都对应「三道门」里的一道。
public enum SessionGate
{
    /// 三道门都没到点,会话有效。
    Ok,
    /// **absolute**:建会话时写死的到期时刻。★ 任何活动都推不动它。
    Absolute,
    /// **idle**:最后一次「人还在」距今太久 —— 挡的是「页面开着但人走了」。
    Idle,
    /// **心跳**:最后一次收到心跳距今太久 —— 挡的是「关页面 / 关浏览器走人」。
    Heartbeat,
}

/// **三道门**(贝塔通 P88–P92,契约见 BetaPass `docs/rp-contract.md`「三道门与存活心跳」)。
///
/// 取代了此前的单一 `expires_at` 判定。三道门各挡各的,**不要合并成一个判据** ——
/// 合并的后果是页面开着就永不过期,idle 那道门直接失效。
///
/// | 门 | 判据字段 | 本仓默认 | 挡什么 |
/// |---|---|---|---|
/// | 心跳 | `last_heartbeat_at` | 15 分钟(5min × 3) | 关页面 / 关浏览器走人 |
/// | idle | `last_seen_at` | 30 分钟 | 页面开着但人走了 |
/// | absolute | `expires_at` | 6 小时 | 前两道被绕过时的最后一道 |
///
/// ★ **各值各平台完全自由调、不登记、贝塔通不拦**(其 P90 已正式取消原来那条
///   「RP 的 absolute 不得超过 IdP 的」)。兜底改由「永不放弃的撤权重投」承担 ——
///   时长设长的代价从「撤权可能永远漏掉」降成了「撤权可能晚一点到」。
///   ★ 但「自由调」不等于「随便填」:absolute 必须覆盖得住一整场考试(见 `ServerConfig`)。
///
/// ★★ **判定是被动的**:过期发生在「下一次请求进来时」,**不要起定时任务或轮询**。
///   本类因此只有纯函数,没有任何后台作业。
///
/// ★★ **写入口只有心跳**(其 P92 翻案 P25):`last_seen_at` **只由带 `active: true` 的心跳更新**,
///   **任何业务请求都不得续 idle**。不写死这条,监考看板那种**持续自动轮询**就会续 idle,
///   于是「看板开着 = 永不登出」,idle 那道门形同虚设 —— 这正是对侧 P25 被翻案的直接原因。
public static class SessionGates
{
    /// 服务端心跳节流窗口(秒)。
    ///
    /// ★ 取 150 秒而不是更接近心跳间隔(5 分钟):定时器有抖动(浏览器节流、系统负载、
    ///   leader 刚切换时的补发),窗口越接近间隔越容易吞掉一次心跳,而**吞一次就用掉
    ///   15 分钟容忍窗口的 60%**。150 秒留了 2× 余量。
    public const double ThrottleSeconds = 150;

    /// 判定顺序:**revoked → absolute → idle → heartbeat**(更强的终止理由排前面)。
    ///
    /// ★ `revoked` 那道门不在这里 —— 撤权(<c>RevokeBySub</c> / <c>RevokeByExam</c>)是**删行**,
    ///   所以「查无此会话」本身就是它,顺序上天然排在最前。
    public static SessionGate Evaluate(
        double now, double expiresAt, double lastSeenAt, double lastHeartbeatAt,
        double idleMinutes, double heartbeatMinutes)
    {
        if (now > expiresAt) return SessionGate.Absolute;
        if (now > lastSeenAt + idleMinutes * 60.0) return SessionGate.Idle;
        if (now > lastHeartbeatAt + heartbeatMinutes * 60.0) return SessionGate.Heartbeat;
        return SessionGate.Ok;
    }

    // ★ **这里故意没有「节流判据」的 C# 版本。**
    //   节流与「按字段分开」整个落在 SQL 上(`SessionStore.Heartbeat` / `AdminSessionStore.Heartbeat`
    //   的 WHERE + 两个独立 CASE),让「分开」成为结构上唯一写得出的形状。
    //   ★★ 另写一份可单测的 C# 副本看着更好测,但那就是**同一个判据两个来源** ——
    //   它会漂,而漂的表现是「测试全绿、线上把 active:true 吞掉了」。
    //   回归改为直接打 `Heartbeat()` 的返回值与两个时间戳列(见 SessionGateTests)。
}
