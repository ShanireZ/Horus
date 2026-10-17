using Horus.Server.Config;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// M4·S2:OIDC 採集工作階段(取代共享 PSK)。經 /oidc/exchange 建立,綁定 wentian 身分到 (exam,seat,agent),
/// 攜派生金鑰 k_sess 供採集簽名。DB 持久化(伺服器重新啟動不丟·考試中途不強制重登)。見 docs/m4-identity-oidc.md §5 S2/S3。
public sealed record HorusSession(
    string SessionId, string ExamId, string SeatId, string AgentId, string? MachineId,
    // ★ 身分只剩三項(貝塔通 P81):sub / 真實姓名 / 使用者名。`Username` 是**座位標識**的依據,不是顯示欄位。
    string Sub, string Name, string Username,
    byte[] KSess, double IssuedAt, double ExpiresAt)
{
    /// 事件/圖片/擊鍵體自報的 (exam,seat,agent) 是否與本工作階段綁定值一致 —— 不一致即跨身分栽贓,拒收(閉合 A1)。
    public bool IdentityMatches(string examId, string seatId, string agentId)
        => ExamId == examId && SeatId == seatId && AgentId == agentId;

}

public sealed class SessionStore(Db db, ServerConfig cfg)
{
    /// 建工作階段並寫入資料庫。k_sess 為 ECDH 派生的 32 位元組金鑰。
    /// ★ 兩個新時間戳都以 `now` 起步 —— 建工作階段那一刻我們確實見過這個人,
    ///   否則第一次心跳到達之前(最長 5 分鐘)工作階段就被自己的門判掉了。
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

    /// 採集端心跳:續「心跳」那道門;★ **只有 `active: true` 才額外續 idle**。
    ///
    /// ★★ **`horus-client` 自己定義 `active`**(貝塔通 rp-contract 專門為桌面用戶端寫了這一條):
    ///   它沒有 `BroadcastChannel`、沒有 `visibilityState`,而**它本來就在採集機器上有無使用者活動** ——
    ///   那個信號比瀏覽器的更準。照抄網頁那套的結果是一個恆為 `false` 的 `active`,
    ///   表現是**每個學生考到 30 分鐘就被 idle 踢掉**。
    ///
    /// ★★ 節流與「按欄位分開」都落在 SQL 上,見 <see cref="SessionGates.ShouldWrite"/> 的說明:
    ///   WHERE 是「心跳夠舊 **或**(本次 active **且** idle 那個時間戳也夠舊)」,
    ///   兩個 CASE 各管各的列 —— 於是「標籤 A 的 active:false 吞掉標籤 B 的 active:true」
    ///   在結構上就發生不了,而不是靠呼叫方記得。
    ///
    /// @returns 是否真的寫了(節流視窗內回傳 false;工作階段不存在也回傳 false)。
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

    /// 按考試吊銷全部採集工作階段(監考員遠程登出全場)。吊銷後 Get 查無此工作階段 → 重連/上報一律 401,Agent 須重登。
    /// 回傳吊銷條數。
    public int RevokeByExam(string examId, double now)
        => Revoke(conn => conn.Cmd("SELECT session_id FROM oidc_sessions WHERE exam_id=@e", ("@e", examId)),
                  conn => conn.Cmd("DELETE FROM oidc_sessions WHERE exam_id=@e", ("@e", examId)),
                  RevocationNotices.ExamLogout, now);

    /// 按 `sub` 吊銷該人的全部採集工作階段(貝塔通撤權通知·rp-contract)。回傳吊銷條數。
    /// ★ 與 <see cref="RevokeByExam"/> 的區別:那條是監考員主動清全場,這條是身分中心告知
    ///   「這個人的憑證失效了」—— 跨考試、只針對一個人。
    /// ★ `reason` 只用來**留一句給使用者的話**,**絕不參與「要不要清」的判斷**。
    public int RevokeBySub(string sub, string reason, double now)
        => Revoke(conn => conn.Cmd("SELECT session_id FROM oidc_sessions WHERE sub=@s", ("@s", sub)),
                  conn => conn.Cmd("DELETE FROM oidc_sessions WHERE sub=@s", ("@s", sub)),
                  reason, now);

    /// 先取出要刪的 session_id、再刪、再留痕 —— 順序不能倒(刪完就查不到 id 了)。
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

    /// 按 sessionId 取工作階段;不存在或**三道門任一到點**回傳 null(即拒,Agent 須重登)。
    /// ★ 被動判定:過期就發生在這一次查詢裡,**沒有定時任務、沒有輪詢**。
    public HorusSession? Get(string sessionId, double now) => GetWithGate(sessionId, now).Session;

    /// 同 <see cref="Get"/>,另外告訴呼叫方**是哪一道門**攔下的(用於給使用者一句人話、以及預檢)。
    /// <see cref="SessionGate.Ok"/> 且 Session 為 null = 查無此工作階段(已被撤權刪掉,或從來就不存在)。
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
            if (!r.Read()) return (null, SessionGate.Ok);   // revoked / 不存在 —— 判定順序裡最前的一道
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
