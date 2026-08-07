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

public sealed class AdminSessionStore(Db db)
{
    /// 建管理会话并落库。
    public AdminSession Create(OidcClaims claims, double now, int sessionMinutes)
    {
        string sessionId = "asess_" + Guid.NewGuid().ToString("N");
        double expiresAt = now + sessionMinutes * 60.0;
        db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO admin_sessions (session_id,sub,name,issued_at,expires_at)
                  VALUES (@sid,@sub,@n,@iss,@exp)",
                ("@sid", sessionId), ("@sub", claims.Sub), ("@n", claims.Name),
                ("@iss", now), ("@exp", expiresAt));
            c.ExecuteNonQuery();
        });
        return new AdminSession(sessionId, claims.Sub, claims.Name, now, expiresAt);
    }

    /// 按 sessionId 取管理会话;不存在或**已过期**返回 null(gate 据此拒)。
    public AdminSession? Get(string sessionId, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return db.Read<AdminSession?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"SELECT sub,name,issued_at,expires_at FROM admin_sessions WHERE session_id=@sid",
                ("@sid", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return null;
            double expiresAt = r.GetDouble(3);
            if (now > expiresAt) return null;   // 过期
            return new AdminSession(sessionId, r.GetString(0), Nz(r, 1), r.GetDouble(2), expiresAt);
        });
    }

    /// 按 `sub` 吊销该人的全部管理会话(贝塔通撤权通知·rp-contract)。返回吊销条数。
    public int RevokeBySub(string sub)
        => db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("DELETE FROM admin_sessions WHERE sub=@s", ("@s", sub));
            return c.ExecuteNonQuery();
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
