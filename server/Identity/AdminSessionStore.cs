using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·RBAC:监考员看板**管理会话**(wentian dashboard OIDC 登录后派发·取代静态 adminToken)。
/// 与采集会话 <see cref="HorusSession"/> 相互独立:此会话不绑 exam/seat/agent、无 k_sess,只承载"某长老已认证进管理端"。
/// ★ 建会话的前提是**身份中心已在授权阶段放行**(平台 `horus-admin`,贝塔通 P83) —— 换得到 code 就是监考员。
/// gate 校验此表(未过期)→ 放行 /api/*。见 docs/m4-identity-oidc.md §10.3。
/// ★ 此前还存 user_type 与六项富画像(wentian `horus_profile`),已随贝塔通 P81 移除。
public sealed record AdminSession(
    string SessionId, string Sub, string Name, double IssuedAt, double ExpiresAt);

public sealed class AdminSessionStore(Db db, ServerConfig cfg)
{
    /// 建管理会话并落库。两个心跳时间戳以 `now` 起步(理由同采集会话)。
    public AdminSession Create(OidcClaims claims, double now, int sessionMinutes)
    {
        string sessionId = "asess_" + Guid.NewGuid().ToString("N");
        double expiresAt = now + sessionMinutes * 60.0;
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO admin_sessions (session_id,sub,name,issued_at,expires_at,last_heartbeat_at,last_seen_at)
                  VALUES (@sid,@sub,@n,@iss,@exp,@iss,@iss)",
                ("@sid", sessionId), ("@sub", claims.Sub), ("@n", claims.Name),
                ("@iss", now), ("@exp", expiresAt));
            c.ExecuteNonQuery();
        });
        return new AdminSession(sessionId, claims.Sub, claims.Name, now, expiresAt);
    }

    /// 看板心跳:续「心跳」那道门;★ **只有 `active: true` 才额外续 idle**。
    /// 节流与「按字段分开」的落法与采集面完全一致,见 <see cref="SessionStore.Heartbeat"/>。
    ///
    /// ★★ **这是 `last_seen_at` 在管理面的唯一写入口**。看板是持续自动轮询的,
    ///   任何业务请求若也续 idle,那道门就永远不会到点 —— 监考机上开着看板等于永不登出。
    ///   admin gate(<c>Program.cs</c>)因此**只读不写**。
    public bool Heartbeat(string sessionId, bool active, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        return db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"UPDATE admin_sessions SET
                    last_heartbeat_at = CASE WHEN @now - last_heartbeat_at >= @thr THEN @now ELSE last_heartbeat_at END,
                    last_seen_at      = CASE WHEN @act = 1 AND @now - last_seen_at >= @thr THEN @now ELSE last_seen_at END
                  WHERE session_id = @sid
                    AND (@now - last_heartbeat_at >= @thr OR (@act = 1 AND @now - last_seen_at >= @thr))",
                ("@sid", sessionId), ("@now", now), ("@act", active ? 1 : 0),
                ("@thr", SessionGates.ThrottleSeconds));
            return c.ExecuteNonQuery() > 0;
        });
    }

    /// 按 sessionId 取管理会话;不存在或**三道门任一到点**返回 null(gate 据此拒)。
    public AdminSession? Get(string sessionId, double now) => GetWithGate(sessionId, now).Session;

    /// 同 <see cref="Get"/>,另外告诉调用方是哪一道门拦下的。
    public (AdminSession? Session, SessionGate Gate) GetWithGate(string sessionId, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return (null, SessionGate.Ok);
        return db.Read<(AdminSession?, SessionGate)>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"SELECT sub,name,issued_at,expires_at,
                         COALESCE(last_heartbeat_at,issued_at), COALESCE(last_seen_at,issued_at)
                  FROM admin_sessions WHERE session_id=@sid",
                ("@sid", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return (null, SessionGate.Ok);   // revoked / 不存在
            double expiresAt = r.GetDouble(3);
            SessionGate gate = SessionGates.Evaluate(
                now, expiresAt, lastSeenAt: r.GetDouble(5), lastHeartbeatAt: r.GetDouble(4),
                cfg.SessionIdleMinutes, cfg.SessionHeartbeatMinutes);
            if (gate != SessionGate.Ok) return (null, gate);
            return (new AdminSession(sessionId, r.GetString(0), Nz(r, 1), r.GetDouble(2), expiresAt), SessionGate.Ok);
        });
    }

    /// 按 `sub` 吊销该人的全部管理会话(贝塔通撤权通知·rp-contract)。返回吊销条数。
    /// ★ `reason` 只用来**留一句给用户的话**(见 <see cref="RevocationNotices"/>),
    ///   **绝不参与「要不要清」的判断** —— 四种 reason 处置完全相同,认不出的新值也照清。
    public int RevokeBySub(string sub, string reason, double now)
        => db.Write(conn =>
        {
            var ids = new List<string>();
            using (SqliteCommand q = conn.Cmd("SELECT session_id FROM admin_sessions WHERE sub=@s", ("@s", sub)))
            using (SqliteDataReader r = q.ExecuteReader())
                while (r.Read()) ids.Add(r.GetString(0));

            using SqliteCommand c = conn.Cmd("DELETE FROM admin_sessions WHERE sub=@s", ("@s", sub));
            int n = c.ExecuteNonQuery();
            RevocationNotices.Record(conn, ids, reason, now);
            return n;
        });

    /// 登出:删会话(幂等)。
    public void Delete(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("DELETE FROM admin_sessions WHERE session_id=@sid", ("@sid", sessionId));
            c.ExecuteNonQuery();
        });
    }

    private static string Nz(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
}
