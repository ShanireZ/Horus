using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·S2:OIDC 采集会话(取代共享 PSK)。经 /oidc/exchange 建立,绑定 cpplearn 身份到 (exam,seat,agent),
/// 携派生密钥 k_sess 供采集签名。DB 持久化(服务器重启不丢·考试中途不强制重登)。见 docs/m4-identity-oidc.md §5 S2/S3。
public sealed record HorusSession(
    string SessionId, string ExamId, string SeatId, string AgentId, string? MachineId,
    string Sub, string Username, string Nickname, string DaoName, string Avatar, string Realm, int RealmLevel, int CombatPower,
    byte[] KSess, double IssuedAt, double ExpiresAt)
{
    /// 事件/图片/击键体自报的 (exam,seat,agent) 是否与本会话绑定值一致 —— 不一致即跨身份栽赃,拒收(闭合 A1)。
    public bool IdentityMatches(string examId, string seatId, string agentId)
        => ExamId == examId && SeatId == seatId && AgentId == agentId;
}

public sealed class SessionStore(Db db)
{
    /// 建会话并落库。k_sess 为 ECDH 派生的 32 字节密钥。
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
                    (session_id,exam_id,seat_id,agent_id,machine_id,sub,username,nickname,dao_name,avatar,realm,realm_level,combat_power,k_sess,issued_at,expires_at)
                  VALUES (@sid,@e,@s,@a,@m,@sub,@u,@n,@d,@av,@r,@rl,@cp,@k,@iss,@exp)",
                ("@sid", sessionId), ("@e", examId), ("@s", seatId), ("@a", agentId), ("@m", machineId),
                ("@sub", claims.Sub), ("@u", claims.Username), ("@n", claims.Nickname), ("@d", claims.DaoName),
                ("@av", claims.Avatar), ("@r", claims.Realm), ("@rl", claims.RealmLevel), ("@cp", claims.CombatPower),
                ("@k", Convert.ToBase64String(kSess)), ("@iss", now), ("@exp", expiresAt));
            c.ExecuteNonQuery();
        });
        return new HorusSession(sessionId, examId, seatId, agentId, machineId,
            claims.Sub, claims.Username, claims.Nickname, claims.DaoName, claims.Avatar, claims.Realm, claims.RealmLevel, claims.CombatPower,
            kSess, now, expiresAt);
    }

    /// 按 sessionId 取会话;不存在或**已过期**返回 null(过期即拒,Agent 须重登)。
    public HorusSession? Get(string sessionId, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return db.Read<HorusSession?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"SELECT exam_id,seat_id,agent_id,machine_id,sub,username,nickname,dao_name,avatar,realm,realm_level,combat_power,k_sess,issued_at,expires_at
                  FROM oidc_sessions WHERE session_id=@sid", ("@sid", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return null;
            double expiresAt = r.GetDouble(14);
            if (now > expiresAt) return null;   // 过期
            return new HorusSession(
                sessionId, r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), Nz(r, 5), Nz(r, 6), Nz(r, 7), Nz(r, 8), Nz(r, 9),
                r.IsDBNull(10) ? 0 : r.GetInt32(10), r.IsDBNull(11) ? 0 : r.GetInt32(11),
                Convert.FromBase64String(r.GetString(12)), r.GetDouble(13), expiresAt);
        });
    }

    private static string Nz(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
}
