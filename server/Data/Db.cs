using Microsoft.Data.Sqlite;

namespace Horus.Server.Data;

/// SQLite 访问层:**单写连接(写锁串行) + 独立只读连接(WAL 并发读)**。
///
/// 写:所有落库经 <see cref="Write"/> 在写锁内串行(单写者,杜绝 "database is locked" 写-写冲突)。
/// 读:看板等纯只读查询经 <see cref="Read"/> 走**独立只读连接**——WAL 下读不阻塞写、写不阻塞读,
///     监考端每 5s 轮询不再与采集写路径抢同一把锁(闭合 architecture §10.2「看板只读查询走独立只读连接」)。
///
/// **:memory: 例外**:内存库是每连接独立的(且不支持 WAL),无法开第二条连接看到同一份数据,
/// 故内存模式下 `_read` 为 null,Read 回退到写连接 + 写锁(语义与旧版单连接完全一致,测试不受影响)。
/// 图片原图存文件系统,不入 DB(见 Storage)。
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _write;
    private readonly SqliteConnection? _read;    // 独立只读连接;:memory: 下为 null → 回退写连接
    private readonly object _writeGate = new();
    private readonly object _readGate = new();   // 单只读连接自身非线程安全:并发读之间仍串行,但**与写不互斥**

    public Db(string dataSource)
    {
        bool isMemory = dataSource == ":memory:";

        // Pooling=false:两条连接都是随 Db 生命周期常驻的单例,连接池对单例无益;
        // 且池化会在 Dispose 后仍扣着文件句柄(-wal/-shm 无法释放),妨碍归档 VACUUM / 清理。
        _write = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Pooling = false,
        }.ToString());
        _write.Open();
        Exec(_write, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Schema.Apply(_write);

        if (!isMemory)
        {
            // 只读连接:Mode=ReadOnly 从物理上杜绝经此连接误写;WAL 让它读已提交快照而不阻塞写者。
            _read = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dataSource,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            _read.Open();
            Exec(_read, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        }
    }

    /// 写(或读改写混合的原子序列):单写连接 + 写锁,单写者串行。
    public T Write<T>(Func<SqliteConnection, T> body)
    {
        lock (_writeGate) return body(_write);
    }

    public void Write(Action<SqliteConnection> body)
    {
        lock (_writeGate) body(_write);
    }

    /// 纯只读查询:独立只读连接(WAL 并发读,不占写锁)。:memory: 回退写连接 + 写锁。
    /// 注意:仅用于**无副作用**的 SELECT;任何写/读改写序列必须走 <see cref="Write"/> 以保原子性。
    public T Read<T>(Func<SqliteConnection, T> body)
    {
        if (_read is null) { lock (_writeGate) return body(_write); }   // :memory: 回退
        lock (_readGate) return body(_read);
    }

    /// 兼容旧调用点:Locked == Write(既有语义 = 写锁串行)。新代码只读请显式用 Read。
    public T Locked<T>(Func<SqliteConnection, T> body) => Write(body);
    public void Locked(Action<SqliteConnection> body) => Write(body);

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _read?.Dispose();
        _write.Dispose();
    }
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
