using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// 「你的会话为什么没了」—— 撤权留痕与它对应的那句人话。
///
/// ★★ **为什么需要它**:撤权的处置是**删行**,于是原因随之消失,被踢的人只看得到
///   一句笼统的「登录已失效」。而契约(BetaPass `docs/rp-contract.md`)明写四种 reason
///   「区分只服务提示语」—— 提示语正是这里。
///
/// ★★ **它在「平台权限被关掉」这一种上尤其要紧**:取消 SSO(贝塔通 P84)之后,
///   无权限的人**每点一次登录都要完整输一遍密码**才被拒(其 P98,owner 明确不做缓解)。
///   不告诉他真实原因,他会一遍遍白输 —— 这就是为什么这条不是「体验优化」。
///
/// ★ 表里**只有一句原因,没有任何凭据** —— 所以它不可能让任何会话复活。
///   这正是选「删行 + 侧表留痕」而不是「软删除加个 revoked 列」的理由:
///   软删除要求每一条查询都记得加过滤,漏一条就是把一条已撤销的会话当活的用。
public static class RevocationNotices
{
    /// 留痕保留多久。★ 只是给下一次请求看的一句话,过了这个窗口那个人早就重登过了。
    private const double RetentionSeconds = 7 * 24 * 3600;

    /// 本地口径的 reason(贝塔通那四种之外)。
    public const string ExamLogout = "exam_logout";

    /// 记下这些会话是因为什么被撤的。`sessionIds` 为空则 no-op。
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

    /// 查这条会话是因为什么被撤的;没有留痕(自然过期 / 从来不存在)返回 null。
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

    /// reason → 给用户的一句话。按各站口径改写自 rp-contract 的那张表。
    ///
    /// ★★ **认不出的新值也要有话说**:贝塔通那边加一种 reason 时本端一行代码都不用改
    ///   (处置本就与 reason 无关),但**提示语不能因此变成空白或「未知错误」**。
    ///   兜底那句必须自己站得住。
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
