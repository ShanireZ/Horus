using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Identity;

/// 考試派發(owner 決策 2026-07-03):examId 不再由 Agent 設定攜帶,而是**伺服器端指派** ——
/// 「當前活躍考試」= exams 表 status='active' 中最近建立的一場(正常運營同一時刻只應有一場 active)。
/// /oidc/exchange 建工作階段與 /oidc/active-exam 待命輪詢共用此單一實作,杜絕兩處判據漂移。
public static class ExamDispatch
{
    public sealed record ActiveExam(string ExamId, string Name);

    public static ActiveExam? ResolveActive(Db db)
        => db.Read<ActiveExam?>(conn =>
        {
            using SqliteCommand c = conn.Cmd(
                "SELECT exam_id,name FROM exams WHERE status='active' ORDER BY created_at DESC, exam_id DESC LIMIT 1");
            using SqliteDataReader r = c.ExecuteReader();
            return r.Read() ? new ActiveExam(r.GetString(0), r.GetString(1)) : null;
        });

    /// seatId 派生:= OIDC username(權威身分,看板/取證按人顯示);username 為空或含路徑危險字元時退回 sub。
    /// 「座位號」不再是學員手動設定的教室座位概念 —— 物理定位由 machineId/agentId 承擔。
    public static string SeatFrom(OidcClaims claims)
        => !string.IsNullOrEmpty(claims.Username) && Api.Endpoints.IsSafeId(claims.Username)
            ? claims.Username
            : claims.Sub;
}
