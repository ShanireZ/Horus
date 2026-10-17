using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// **身分相關表的列集合閘門。**
///
/// ★★ 為什麼要有這道閘:本儲存庫有多處**裸 SQL**(最典型是 `Api/Endpoints.cs` 的座位熱力查詢),
///   它們按列名拼字串 —— **刪了一列照樣編譯通過**,要到執行環境才 `no column named …`。
///   2026-08-07 收窄身分欄位時就是這麼栽的:伺服器端 `dotnet build` 全綠,
///   靠幾條不相干的集成測試(座位熱力、健康告警)變紅才發現。那屬於**碰巧被兜住**,
///   而不是有東西在守著。
///
/// 這道閘把「這兩張表有哪些列」變成一條**顯式斷言**:任何增刪列都會在這裡先紅一次,
/// 逼改動的人回頭看一眼那幾處裸 SQL —— 判據落在**表的形狀**本身,不是某條查詢的鏡像。
///
/// ★ 加列時**照實更新期望值**即可;紅了不是要繞過它,是要順手檢查:
///   `Api/Endpoints.cs` 的 seat 查詢、`Identity/SessionStore.cs`、`Identity/AdminSessionStore.cs`、
///   以及 `BetapassRevokeEndpoint` 的臺賬寫入,這四處都在按列名拼 SQL。
public class SchemaDriftTests
{
    private static string[] Columns(Db db, string table)
        => db.Read(conn =>
        {
            var names = new List<string>();
            using SqliteCommand c = conn.Cmd($"SELECT name FROM pragma_table_info('{table}') ORDER BY name");
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names.ToArray();
        });

    [Fact]
    public void 采集会话表的列集合()
    {
        using var app = new TestApp();
        var db = app.Services.GetRequiredService<Db>();
        // ★ 身分只剩 sub / name / username(貝塔通 P81)。
        //   `username` 是**座位標識**的來源(ExamDispatch.SeatFrom),不是顯示欄位 —— 別當畫像刪掉。
        // ★ 三道門(P88–P92):`expires_at` 是 absolute,另兩道靠 last_heartbeat_at / last_seen_at。
        //   ★★ 少任何一列都不會編譯回報錯誤,只會讓那道門**靜默失效** —— 正是這道閘要擋的形狀。
        Assert.Equal(
            ["agent_id", "exam_id", "expires_at", "issued_at", "k_sess", "last_heartbeat_at",
             "last_seen_at", "machine_id", "name", "seat_id", "session_id", "sub", "username"],
            Columns(db, "oidc_sessions"));
    }

    [Fact]
    public void 管理会话表的列集合()
    {
        using var app = new TestApp();
        var db = app.Services.GetRequiredService<Db>();
        // ★ **不得再出現任何角色列**:看板存取資格由貝塔通的 `horus-admin` 平台開關回答(P83),
        //   本機不自己判。加回 user_type / role 之類會讓判據變成兩個來源。
        Assert.Equal(
            ["expires_at", "issued_at", "last_heartbeat_at", "last_seen_at", "name", "session_id", "sub"],
            Columns(db, "admin_sessions"));
    }

    [Fact]
    public void 撤权原因留痕表只有原因_没有任何凭据()
    {
        using var app = new TestApp();
        var db = app.Services.GetRequiredService<Db>();
        // ★★ 這條斷言的是**這張表的性質**:它只存一句給使用者看的原因。
        //   一旦有人往裡加 sub / k_sess / 任何身分或金鑰欄位,它就從「留痕」變成了
        //   「一份已撤銷工作階段的副本」—— 而那正是當初選「刪行 + 側表」而不是軟刪除要避開的東西。
        Assert.Equal(
            ["reason", "revoked_at", "session_id"],
            Columns(db, "revoked_session_notices"));
    }

    [Fact]
    public void 撤权幂等台账的列集合()
    {
        using var app = new TestApp();
        var db = app.Services.GetRequiredService<Db>();
        Assert.Equal(
            ["client_id", "jti", "reason", "received_at", "revoked_count", "sub"],
            Columns(db, "betapass_revocations"));
    }
}
