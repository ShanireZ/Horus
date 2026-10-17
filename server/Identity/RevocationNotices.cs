using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// 「你的工作階段為什麼沒了」—— 撤權留痕與它對應的那句人話。
///
/// ★★ **為什麼需要它**:撤權的處置是**刪行**,於是原因隨之消失,被踢的人只看得到
///   一句籠統的「登入已失效」。而契約(BetaPass `docs/rp-contract.md`)明寫四種 reason
///   「區分只服務提示語」—— 提示語正是這裡。
///
/// ★★ **它在「平台權限被關掉」這一種上尤其要緊**:取消 SSO(貝塔通 P84)之後,
///   無權限的人**每點一次登入都要完整輸一遍密碼**才被拒(其 P98,owner 明確不做緩解)。
///   不告訴他真實原因,他會一遍遍白輸 —— 這就是為什麼這條不是「體驗優化」。
///
/// ★ 表裡**只有一句原因,沒有任何憑證** —— 所以它不可能讓任何工作階段復活。
///   這正是選「刪行 + 側表留痕」而不是「軟刪除加個 revoked 列」的理由:
///   軟刪除要求每一條查詢都記得加過濾,漏一條就是把一條已撤銷的工作階段當活的用。
public static class RevocationNotices
{
    /// 留痕保留多久。★ 只是給下一次請求看的一句話,過了這個視窗那個人早就重登過了。
    private const double RetentionSeconds = 7 * 24 * 3600;

    /// 本機口徑的 reason(貝塔通那四種之外)。
    public const string ExamLogout = "exam_logout";

    /// 記下這些工作階段是因為什麼被撤的。`sessionIds` 為空則 no-op。
    public static void Record(SqliteConnection conn, IEnumerable<string> sessionIds, string reason, double now)
    {
        foreach (string sid in sessionIds)
        {
            using SqliteCommand c = conn.Cmd(
                @"INSERT INTO revoked_session_notices (session_id,reason,revoked_at) VALUES (@s,@r,@t)
                  ON CONFLICT(session_id) DO UPDATE SET reason=@r, revoked_at=@t",
                ("@s", sid), ("@r", reason), ("@t", now));
            c.ExecuteNonQuery();
        }
        using SqliteCommand prune = conn.Cmd(
            "DELETE FROM revoked_session_notices WHERE revoked_at < @cut", ("@cut", now - RetentionSeconds));
        prune.ExecuteNonQuery();
    }

    /// 查這條工作階段是因為什麼被撤的;沒有留痕(自然過期 / 從來不存在)回傳 null。
    public static string? Lookup(Db db, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        return db.Read<string?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "SELECT reason FROM revoked_session_notices WHERE session_id=@s", ("@s", sessionId));
            return c.ExecuteScalar() as string;
        });
    }

    /// reason → 給使用者的一句話。按各站口徑改寫自 rp-contract 的那張表。
    ///
    /// ★★ **認不出的新值也要有話說**:貝塔通那邊加一種 reason 時本端一行程式碼都不用改
    ///   (處置本就與 reason 無關),但**提示語不能因此變成空白或「未知錯誤」**。
    ///   備援那句必須自己站得住。
    public static string Message(string? reason) => reason switch
    {
        "platform_access_revoked" => "你的「贝塔天目·监考台」访问权限已关闭，请联系管理员。再次登录仍会被拒。",
        "password_changed" => "你的贝塔通密码已变更，请重新登录。",
        "mfa_factor_changed" => "你的贝塔通二次验证设置已变更，请重新登录。",
        "user_logout" => "你已在贝塔通选择「退出所有站点」。",
        ExamLogout => "监考员已执行全场登出，请重新登录。",
        _ => "你的登录已被身份中心撤销，请重新登录。",
    };
}
