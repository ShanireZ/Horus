using Horus.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Horus.Server.Tests;

/// **身份相关表的列集合闸门。**
///
/// ★★ 为什么要有这道闸:本仓库有多处**裸 SQL**(最典型是 `Api/Endpoints.cs` 的座位热力查询),
///   它们按列名拼字符串 —— **删了一列照样编译通过**,要到运行时才 `no column named …`。
///   2026-08-07 收窄身份字段时就是这么栽的:服务端 `dotnet build` 全绿,
///   靠几条不相干的集成测试(座位热力、健康告警)变红才发现。那属于**碰巧被兜住**,
///   而不是有东西在守着。
///
/// 这道闸把「这两张表有哪些列」变成一条**显式断言**:任何增删列都会在这里先红一次,
/// 逼改动的人回头看一眼那几处裸 SQL —— 判据落在**表的形状**本身,不是某条查询的镜像。
///
/// ★ 加列时**照实更新期望值**即可;红了不是要绕过它,是要顺手检查:
///   `Api/Endpoints.cs` 的 seat 查询、`Identity/SessionStore.cs`、`Identity/AdminSessionStore.cs`、
///   以及 `BetapassRevokeEndpoint` 的台账写入,这四处都在按列名拼 SQL。
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
        // ★ 身份只剩 sub / name / username(贝塔通 P81)。
        //   `username` 是**座位标识**的来源(ExamDispatch.SeatFrom),不是显示字段 —— 别当画像删掉。
        Assert.Equal(
            ["agent_id", "exam_id", "expires_at", "issued_at", "k_sess", "machine_id",
             "name", "seat_id", "session_id", "sub", "username"],
            Columns(db, "oidc_sessions"));
    }

    [Fact]
    public void 管理会话表的列集合()
    {
        using var app = new TestApp();
        var db = app.Services.GetRequiredService<Db>();
        // ★ **不得再出现任何角色列**:看板准入由贝塔通的 `horus-admin` 平台开关回答(P83),
        //   本地不自己判。加回 user_type / role 之类会让判据变成两个来源。
        Assert.Equal(
            ["expires_at", "issued_at", "name", "session_id", "sub"],
            Columns(db, "admin_sessions"));
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
