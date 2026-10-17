using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·RBAC:監考員看板**管理工作階段**(wentian dashboard OIDC 登入後派發·取代靜態 adminToken)。
/// 與採集工作階段 <see cref="HorusSession"/> 相互獨立:此工作階段不綁 exam/seat/agent、無 k_sess,只承載"某長老已認證進管理端"。
/// ★ 建工作階段的前提是**身分中心已在授權階段放行**(平台 `horus-admin`,貝塔通 P83) —— 換得到 code 就是監考員。
/// gate 檢核此表(未過期)→ 放行 /api/*。見 docs/m4-identity-oidc.md §10.3。
/// ★ 此前還存 user_type 與六項富畫像(wentian `horus_profile`),已隨貝塔通 P81 移除。
public sealed record AdminSession(
    string SessionId, string Sub, string Name, double IssuedAt, double ExpiresAt);

public sealed class AdminSessionStore(Db db, ServerConfig cfg)
{
    /// 建管理工作階段並寫入資料庫。兩個心跳時間戳以 `now` 起步(理由同採集工作階段)。
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

    /// 看板心跳:續「心跳」那道門;★ **只有 `active: true` 才額外續 idle**。
    /// 節流與「按欄位分開」的落法與採集面完全一致,見 <see cref="SessionStore.Heartbeat"/>。
    ///
    /// ★★ **這是 `last_seen_at` 在管理面的唯一寫入口**。看板是持續自動輪詢的,
    ///   任何業務請求若也續 idle,那道門就永遠不會到點 —— 監考機上開著看板等於永不登出。
    ///   admin gate(<c>Program.cs</c>)因此**唯讀不寫**。
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

    /// 按 sessionId 取管理工作階段;不存在或**三道門任一到點**回傳 null(gate 據此拒)。
    public AdminSession? Get(string sessionId, double now) => GetWithGate(sessionId, now).Session;

    /// 同 <see cref="Get"/>,另外告訴呼叫方是哪一道門攔下的。
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

    /// 按 `sub` 吊銷該人的全部管理工作階段(貝塔通撤權通知·rp-contract)。回傳吊銷條數。
    /// ★ `reason` 只用來**留一句給使用者的話**(見 <see cref="RevocationNotices"/>),
    ///   **絕不參與「要不要清」的判斷** —— 四種 reason 處置完全相同,認不出的新值也照清。
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

    /// 登出:刪工作階段(冪等)。
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
