using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Data;

/// SQLite 訪問層:**單寫連接(寫鎖串行) + 唯讀連接池(WAL 並行讀)**。
///
/// 寫:所有寫入資料庫經 <see cref="Write"/> 在寫鎖內串行(單寫者,杜絕 "database is locked" 寫-寫衝突)。
/// 讀:純唯讀查詢經 <see cref="Read"/> 從**唯讀連接池**租借一條連接——WAL 下讀不阻塞寫、寫不阻塞讀,且
///     **讀與讀之間也並行**(池大小內):看板 6 個 GET + 完整性稽核(全考試事件 SHA256)+ 歸檔 copy 讀**互不串行**,
///     避免一個慢唯讀操作阻塞所有交互看板輪詢(閉合 architecture §10.2「看板走獨立唯讀連接」的讀-讀串行殘留)。
///
/// **:memory: 例外**:記憶體庫每連接獨立(且不支援 WAL),無法開第二條連接看到同一份資料,故記憶體模式下
///     無唯讀池,Read 退回寫連接 + 寫鎖(語義與舊版單連接一致,測試不受影響)。圖片原圖存檔案系統,不入 DB(見 Storage)。
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _write;
    private readonly object _writeGate = new();
    private readonly SemaphoreSlim? _readSlots;               // 唯讀並行閘(= 池大小);:memory: 下 null
    private readonly ConcurrentBag<SqliteConnection>? _readPool;
    private readonly List<SqliteConnection> _allReads = new();   // 供 Dispose 釋放

    public Db(string dataSource, int readPoolSize = 4)
    {
        bool isMemory = dataSource == ":memory:";

        // Pooling=false:連接隨 Db 生命週期常駐的單例/池,連接池對其無益;
        // 且池化會在 Dispose 後仍扣著檔案句柄(-wal/-shm 無法釋放),妨礙歸檔 VACUUM / 清理。
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
            // 唯讀連接池:Mode=ReadOnly 從物理上杜絕經此誤寫;WAL 讓每條讀已提交快照而不阻塞寫者。
            int n = Math.Clamp(readPoolSize, 1, 16);
            _readSlots = new SemaphoreSlim(n, n);
            _readPool = new ConcurrentBag<SqliteConnection>();
            for (int i = 0; i < n; i++)
            {
                var rc = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dataSource,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
                rc.Open();
                Exec(rc, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
                _readPool.Add(rc);
                _allReads.Add(rc);
            }
        }
    }

    /// 寫(或讀改寫混合的原子序列):單寫連接 + 寫鎖,單寫者串行。
    public T Write<T>(Func<SqliteConnection, T> body)
    {
        lock (_writeGate) return body(_write);
    }

    public void Write(Action<SqliteConnection> body)
    {
        lock (_writeGate) body(_write);
    }

    /// 純唯讀查詢:從唯讀連接池租借一條(WAL 並行讀,不佔寫鎖,讀-讀並行)。:memory: 退回寫連接 + 寫鎖。
    /// 注意:僅用於**無副作用**的 SELECT;任何寫/讀改寫序列必須走 <see cref="Write"/> 以保原子性。
    public T Read<T>(Func<SqliteConnection, T> body)
    {
        if (_readPool is null) { lock (_writeGate) return body(_write); }   // :memory: 退回
        _readSlots!.Wait();
        SqliteConnection? c = null;
        try
        {
            _readPool.TryTake(out c);   // 閘已保證有空閒連接可取
            return body(c!);
        }
        finally
        {
            if (c is not null) _readPool.Add(c);
            _readSlots.Release();
        }
    }

    /// 相容舊呼叫點:Locked == Write(既有語義 = 寫鎖串行)。新程式碼唯讀請顯式用 Read。
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
        foreach (SqliteConnection rc in _allReads) rc.Dispose();
        _readSlots?.Dispose();
        _write.Dispose();
    }
}

/// SqliteCommand 參數綁定小工具(null → DBNull)。
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

    /// 該考試是否正在歸檔 / 已歸檔(`archiving`/`archived`)——歸檔視窗內 ingest 應短路,
    /// 否則"讀快照→DELETE WHERE exam_id"之間到達的新資料會被無錨點刪掉(閉合 architecture §13.2 late-ingest 競態)。
    /// 未建考試(status=null)/active/ended 均放行(不改既有 ingest 行為)。
    public static bool IsExamSealed(this SqliteConnection conn, string examId)
    {
        using SqliteCommand c = conn.Cmd("SELECT status FROM exams WHERE exam_id=@e", ("@e", examId));
        return c.ExecuteScalar() as string is "archiving" or "archived";
    }
}
