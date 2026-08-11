using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·S2:OIDC 采集会话(取代共享 PSK)。经 /oidc/exchange 建立,绑定 wentian 身份到 (exam,seat,agent),
/// 携派生密钥 k_sess 供采集签名。DB 持久化(服务器重启不丢·考试中途不强制重登)。见 docs/m4-identity-oidc.md §5 S2/S3。
public sealed record HorusSession(
    string SessionId, string ExamId, string SeatId, string AgentId, string? MachineId,
    // ★ 身份只剩三项(贝塔通 P81):sub / 真实姓名 / 用户名。`Username` 是**座位标识**的依据,不是显示字段。
    string Sub, string Name, string Username,
    byte[] KSess, double IssuedAt, double ExpiresAt)
{
    /// 事件/图片/击键体自报的 (exam,seat,agent) 是否与本会话绑定值一致 —— 不一致即跨身份栽赃,拒收(闭合 A1)。
    public bool IdentityMatches(string examId, string seatId, string agentId)
        => ExamId == examId && SeatId == seatId && AgentId == agentId;

}

public sealed class SessionStore(Db db, ServerConfig cfg)
{
    /// 建会话并落库。k_sess 为 ECDH 派生的 32 字节密钥。
    /// ★ 两个新时间戳都以 `now` 起步 —— 建会话那一刻我们确实见过这个人,
    ///   否则第一次心跳到达之前(最长 5 分钟)会话就被自己的门判掉了。
    public HorusSession Create(
        string examId, string seatId, string agentId, string? machineId, OidcClaims claims,
        byte[] kSess, double now, int sessionMinutes)
    {
        string sessionId = "sess_" + Guid.NewGuid().ToString("N");
        double expiresAt = now + sessionMinutes * 60.0;
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO oidc_sessions
                    (session_id,exam_id,seat_id,agent_id,machine_id,sub,name,username,k_sess,
                     issued_at,expires_at,last_heartbeat_at,last_seen_at)
                  VALUES (@sid,@e,@s,@a,@m,@sub,@n,@u,@k,@iss,@exp,@iss,@iss)",
                ("@sid", sessionId), ("@e", examId), ("@s", seatId), ("@a", agentId), ("@m", machineId),
                ("@sub", claims.Sub), ("@n", claims.Name), ("@u", claims.Username),
                ("@k", Convert.ToBase64String(kSess)), ("@iss", now), ("@exp", expiresAt));
            c.ExecuteNonQuery();
        });
        return new HorusSession(sessionId, examId, seatId, agentId, machineId,
            claims.Sub, claims.Name, claims.Username, kSess, now, expiresAt);
    }

    /// 采集端心跳:续「心跳」那道门;★ **只有 `active: true` 才额外续 idle**。
    ///
    /// ★★ **`horus-client` 自己定义 `active`**(贝塔通 rp-contract 专门为桌面客户端写了这一条):
    ///   它没有 `BroadcastChannel`、没有 `visibilityState`,而**它本来就在采集机器上有无用户活动** ——
    ///   那个信号比浏览器的更准。照抄网页那套的结果是一个恒为 `false` 的 `active`,
    ///   表现是**每个学生考到 30 分钟就被 idle 踢掉**。
    ///
    /// ★★ 节流与「按字段分开」都落在 SQL 上,见 <see cref="SessionGates.ShouldWrite"/> 的说明:
    ///   WHERE 是「心跳够旧 **或**(本次 active **且** idle 那个时间戳也够旧)」,
    ///   两个 CASE 各管各的列 —— 于是「标签 A 的 active:false 吞掉标签 B 的 active:true」
    ///   在结构上就发生不了,而不是靠调用方记得。
    ///
    /// @returns 是否真的写了(节流窗口内返回 false;会话不存在也返回 false)。
    public bool Heartbeat(string sessionId, bool active, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        return db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"UPDATE oidc_sessions SET
                    last_heartbeat_at = CASE WHEN @now - last_heartbeat_at >= @thr THEN @now ELSE last_heartbeat_at END,
                    last_seen_at      = CASE WHEN @act = 1 AND @now - last_seen_at >= @thr THEN @now ELSE last_seen_at END
                  WHERE session_id = @sid
                    AND (@now - last_heartbeat_at >= @thr OR (@act = 1 AND @now - last_seen_at >= @thr))",
                ("@sid", sessionId), ("@now", now), ("@act", active ? 1 : 0),
                ("@thr", SessionGates.ThrottleSeconds));
            return c.ExecuteNonQuery() > 0;
        });
    }

    /// 按考试吊销全部采集会话(监考员远程登出全场)。吊销后 Get 查无此会话 → 重连/上报一律 401,Agent 须重登。
    /// 返回吊销条数。
    public int RevokeByExam(string examId, double now)
        => Revoke(conn => conn.Cmd("SELECT session_id FROM oidc_sessions WHERE exam_id=@e", ("@e", examId)),
                  conn => conn.Cmd("DELETE FROM oidc_sessions WHERE exam_id=@e", ("@e", examId)),
                  RevocationNotices.ExamLogout, now);

    /// 按 `sub` 吊销该人的全部采集会话(贝塔通撤权通知·rp-contract)。返回吊销条数。
    /// ★ 与 <see cref="RevokeByExam"/> 的区别:那条是监考员主动清全场,这条是身份中心告知
    ///   「这个人的凭据失效了」—— 跨考试、只针对一个人。
    /// ★ `reason` 只用来**留一句给用户的话**,**绝不参与「要不要清」的判断**。
    public int RevokeBySub(string sub, string reason, double now)
        => Revoke(conn => conn.Cmd("SELECT session_id FROM oidc_sessions WHERE sub=@s", ("@s", sub)),
                  conn => conn.Cmd("DELETE FROM oidc_sessions WHERE sub=@s", ("@s", sub)),
                  reason, now);

    /// 先取出要删的 session_id、再删、再留痕 —— 顺序不能倒(删完就查不到 id 了)。
    private int Revoke(Func<SqliteConnection, SqliteCommand> select, Func<SqliteConnection, SqliteCommand> delete,
        string reason, double now)
        => db.Write(conn =>
        {
            var ids = new List<string>();
            using (SqliteCommand q = select(conn))
            using (SqliteDataReader r = q.ExecuteReader())
                while (r.Read()) ids.Add(r.GetString(0));

            using SqliteCommand c = delete(conn);
            int n = c.ExecuteNonQuery();
            RevocationNotices.Record(conn, ids, reason, now);
            return n;
        });

    /// 按 sessionId 取会话;不存在或**三道门任一到点**返回 null(即拒,Agent 须重登)。
    /// ★ 被动判定:过期就发生在这一次查询里,**没有定时任务、没有轮询**。
    public HorusSession? Get(string sessionId, double now) => GetWithGate(sessionId, now).Session;

    /// 同 <see cref="Get"/>,另外告诉调用方**是哪一道门**拦下的(用于给用户一句人话、以及预检)。
    /// <see cref="SessionGate.Ok"/> 且 Session 为 null = 查无此会话(已被撤权删掉,或从来就不存在)。
    public (HorusSession? Session, SessionGate Gate) GetWithGate(string sessionId, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return (null, SessionGate.Ok);
        return db.Read<(HorusSession?, SessionGate)>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"SELECT exam_id,seat_id,agent_id,machine_id,sub,name,username,k_sess,issued_at,expires_at,
                         COALESCE(last_heartbeat_at,issued_at), COALESCE(last_seen_at,issued_at)
                  FROM oidc_sessions WHERE session_id=@sid", ("@sid", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return (null, SessionGate.Ok);   // revoked / 不存在 —— 判定顺序里最前的一道
            double expiresAt = r.GetDouble(9);
            SessionGate gate = SessionGates.Evaluate(
                now, expiresAt, lastSeenAt: r.GetDouble(11), lastHeartbeatAt: r.GetDouble(10),
                cfg.SessionIdleMinutes, cfg.SessionHeartbeatMinutes);
            if (gate != SessionGate.Ok) return (null, gate);
            return (new HorusSession(
                sessionId, r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), Nz(r, 5), Nz(r, 6),
                Convert.FromBase64String(r.GetString(7)), r.GetDouble(8), expiresAt), SessionGate.Ok);
        });
    }

    private static string Nz(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
}
