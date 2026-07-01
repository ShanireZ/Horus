using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Honus.Server.Data;

/// 从内嵌资源读取权威 DDL(schema.sql)并应用。
/// M1 剥离 sqlite-vec 的 vec0 虚表(需 vec0 扩展,属 M3),其余表照建。
public static class Schema
{
    public static string LoadDdl()
    {
        Assembly asm = typeof(Schema).Assembly;
        string res = asm.GetManifestResourceNames().First(n => n.EndsWith("schema.sql", StringComparison.OrdinalIgnoreCase));
        using Stream s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static void Apply(SqliteConnection conn)
    {
        var kept = SplitStatements(LoadDdl())
            .Where(st => !st.Contains("USING vec0", StringComparison.OrdinalIgnoreCase))  // M1 跳过 CLIP 向量虚表
            .ToList();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = string.Join(";\n", kept) + ";";
        cmd.ExecuteNonQuery();
    }

    /// 按 ';' 切分语句。该 DDL 内无包含 ';' 的字符串字面量,故简单切分即可;
    /// 每块保留内部换行与 '--' 行注释,SQLite 可直接执行。
    private static IEnumerable<string> SplitStatements(string sql)
        => sql.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0);
}
