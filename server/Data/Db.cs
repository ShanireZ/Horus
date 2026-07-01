using Microsoft.Data.Sqlite;

namespace Honus.Server.Data;

/// 单连接 + 全局串行化的 SQLite 访问层。
/// M1 规模(局域网内数十 Agent、看板低频轮询)吞吐极小,一把写锁足够且避免 "database is locked"。
/// 所有 DB 操作经 Locked(...) 在锁内串行执行。图片原图存文件系统,不入 DB(见 Storage)。
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Db(string dataSource)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dataSource }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Schema.Apply(_conn);
    }

    /// 在写锁内执行一段 DB 操作(读或写),返回结果。
    public T Locked<T>(Func<SqliteConnection, T> body)
    {
        lock (_gate) return body(_conn);
    }

    public void Locked(Action<SqliteConnection> body)
    {
        lock (_gate) body(_conn);
    }

    private void Exec(string sql)
    {
        using SqliteCommand c = _conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

/// SqliteCommand 参数绑定小工具(null → DBNull)。
public static class DbExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection conn, string sql, params (string name, object? val)[] ps)
    {
        SqliteCommand c = conn.CreateCommand();
        c.CommandText = sql;
        foreach ((string name, object? val) in ps)
            c.Parameters.AddWithValue(name, val ?? DBNull.Value);
        return c;
    }
}
