using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·RBAC:监考员看板**管理会话**(cpplearn dashboard OIDC 登录后派发·取代静态 adminToken)。
/// 与采集会话 <see cref="HorusSession"/> 相互独立:此会话不绑 exam/seat/agent、无 k_sess,只承载"某长老已认证进管理端"。
/// 仅 user_type='elder' 才建会话(弟子在 /cb 被拒,不入表)。gate 校验此表(未过期)→ 放行 /api/*。见 docs/m4-identity-oidc.md §10.3。
public sealed record AdminSession(
    string SessionId, string Sub, string UserType,
    string Username, string Nickname, string DaoName, string Avatar, string Realm, int RealmLevel, int CombatPower,
    double IssuedAt, double ExpiresAt)
{
    public bool IsElder => string.Equals(UserType, "elder", StringComparison.Ordinal);
}

public sealed class AdminSessionStore(Db db)
{
    /// 建管理会话并落库(仅应对已确认 elder 的 claims 调用)。
    public AdminSession Create(OidcClaims claims, double now, int sessionMinutes)
    {
        string sessionId = "asess_" + Guid.NewGuid().ToString("N");
        double expiresAt = now + sessionMinutes * 60.0;
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO admin_sessions
                    (session_id,sub,user_type,username,nickname,dao_name,avatar,realm,realm_level,combat_power,issued_at,expires_at)
                  VALUES (@sid,@sub,@ut,@u,@n,@d,@av,@r,@rl,@cp,@iss,@exp)",
                ("@sid", sessionId), ("@sub", claims.Sub), ("@ut", claims.UserType),
                ("@u", claims.Username), ("@n", claims.Nickname), ("@d", claims.DaoName),
                ("@av", claims.Avatar), ("@r", claims.Realm), ("@rl", claims.RealmLevel), ("@cp", claims.CombatPower),
                ("@iss", now), ("@exp", expiresAt));
            c.ExecuteNonQuery();
        });
        return new AdminSession(sessionId, claims.Sub, claims.UserType,
            claims.Username, claims.Nickname, claims.DaoName, claims.Avatar, claims.Realm, claims.RealmLevel, claims.CombatPower,
            now, expiresAt);
    }

    /// 按 sessionId 取管理会话;不存在或**已过期**返回 null(gate 据此拒)。
    public AdminSession? Get(string sessionId, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return db.Read<AdminSession?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"SELECT sub,user_type,username,nickname,dao_name,avatar,realm,realm_level,combat_power,issued_at,expires_at
                  FROM admin_sessions WHERE session_id=@sid", ("@sid", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return null;
            double expiresAt = r.GetDouble(10);
            if (now > expiresAt) return null;   // 过期
            return new AdminSession(
                sessionId, r.GetString(0), r.GetString(1),
                Nz(r, 2), Nz(r, 3), Nz(r, 4), Nz(r, 5), Nz(r, 6),
                r.IsDBNull(7) ? 0 : r.GetInt32(7), r.IsDBNull(8) ? 0 : r.GetInt32(8),
                r.GetDouble(9), expiresAt);
        });
    }

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
