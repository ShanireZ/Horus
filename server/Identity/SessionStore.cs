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
                    (session_id,exam_id,seat_id,agent_id,machine_id,sub,name,username,k_sess,issued_at,expires_at)
                  VALUES (@sid,@e,@s,@a,@m,@sub,@n,@u,@k,@iss,@exp)",
                ("@sid", sessionId), ("@e", examId), ("@s", seatId), ("@a", agentId), ("@m", machineId),
                ("@sub", claims.Sub), ("@n", claims.Name), ("@u", claims.Username),
                ("@k", Convert.ToBase64String(kSess)), ("@iss", now), ("@exp", expiresAt));
            c.ExecuteNonQuery();
        });
        return new HorusSession(sessionId, examId, seatId, agentId, machineId,
            claims.Sub, claims.Name, claims.Username, kSess, now, expiresAt);
    }

    /// 按考试吊销全部采集会话(监考员远程登出全场)。吊销后 Get 查无此会话 → 重连/上报一律 401,Agent 须重登。
    /// 返回吊销条数。
    public int RevokeByExam(string examId)
        => db.Write(conn =>
        {
            using SqliteCommand c = conn.Cmd("DELETE FROM oidc_sessions WHERE exam_id=@e", ("@e", examId));
            return c.ExecuteNonQuery();
        });

    /// 按 sessionId 取会话;不存在或**已过期**返回 null(过期即拒,Agent 须重登)。
    public HorusSession? Get(string sessionId, double now)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return db.Read<HorusSession?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                @"SELECT exam_id,seat_id,agent_id,machine_id,sub,name,username,k_sess,issued_at,expires_at
                  FROM oidc_sessions WHERE session_id=@sid", ("@sid", sessionId));
            using SqliteDataReader r = c.ExecuteReader();
            if (!r.Read()) return null;
            double expiresAt = r.GetDouble(9);
            if (now > expiresAt) return null;   // 过期
            return new HorusSession(
                sessionId, r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), Nz(r, 5), Nz(r, 6),
                Convert.FromBase64String(r.GetString(7)), r.GetDouble(8), expiresAt);
        });
    }

    private static string Nz(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
}
