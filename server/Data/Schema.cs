using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Data;

/// 從內嵌資源讀取權威 DDL(schema.sql)並應用（sqlite-vec 虛表已從 DDL 移除，統一走普通 image_embeddings 表 + C# 暴力餘弦）。
public static class Schema
{
    /// 按資源名後綴讀取內嵌 DDL(如 "schema.sql" / "schema-archive.sql")。
    public static string LoadNamed(string endsWith)
    {
        Assembly asm = typeof(Schema).Assembly;
        string res = asm.GetManifestResourceNames().First(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
        using Stream s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string LoadDdl() => LoadNamed("schema.sql");

    public static void Apply(SqliteConnection conn)
    {
        var kept = SplitStatements(LoadDdl()).ToList();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = string.Join(";\n", kept) + ";";
        cmd.ExecuteNonQuery();

        Migrate(conn);
    }

    /// 應用歸檔庫 DDL(M3 歸檔作業首次執行環境對 archive 檔案呼叫,idempotent)。
    public static void ApplyArchive(SqliteConnection conn)
    {
        var stmts = SplitStatements(LoadNamed("schema-archive.sql")).ToList();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = string.Join(";\n", stmts) + ";";
            cmd.ExecuteNonQuery();
        }
        // 既有 archive 庫(CREATE IF NOT EXISTS 不補列)補列:
        //   machine_id —— 歸檔事件錨點日後獨立復算 hash_self 需 machineId;
        //   server_risk —— 留存"為何被歸檔為關鍵"的取證依據(旁註,不入 canonical)。
        AddColumnIfMissing(conn, "archive_events", "machine_id", "TEXT");
        AddColumnIfMissing(conn, "archive_events", "server_risk", "INTEGER");
    }

    /// 冪等列遷移:CREATE TABLE IF NOT EXISTS 不會給**已存在**的表補列,故對既有 dev DB 顯式 ADD COLUMN。
    /// 新建 DB 已含新列(DDL 裡有),此處對其為 no-op。
    private static void Migrate(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "events", "server_risk", "INTEGER");   // M2:伺服器側風險復判
        AddColumnIfMissing(conn, "events", "machine_id", "TEXT");       // M3:落 machineId 以支援雜湊鏈離線複驗
        // 貝塔通 P81/P83:身分只剩 sub + 真實姓名。既有 dev 庫裡那八列(user_type/username/nickname/
        // dao_name/avatar/realm/realm_level/combat_power)留著不刪 —— SQLite 的 DROP COLUMN 有約束,
        // 而它們已無人讀,留著無害;新建庫按 DDL 就沒有它們。這裡只補新增的 name 列。
        AddColumnIfMissing(conn, "oidc_sessions", "name", "TEXT");
        AddColumnIfMissing(conn, "admin_sessions", "name", "TEXT");
        // 三道門(貝塔通 P88–P92):兩張工作階段表各補心跳與 idle 兩列。
        // ★ SQLite 的 ADD COLUMN 加不了「NOT NULL 且無預設值」的列,所以這裡聲明成可空、
        //   隨後 BackfillSessionGateColumns 回填 —— 新建庫按 DDL 就是 NOT NULL。
        foreach (string t in new[] { "oidc_sessions", "admin_sessions" })
        {
            AddColumnIfMissing(conn, t, "last_heartbeat_at", "REAL");
            AddColumnIfMissing(conn, t, "last_seen_at", "REAL");
        }
        BackfillSessionGateColumns(conn);
        // 第三輪 F1/F2:把"視覺分析閂鎖"從 uploaded_to_ocr(現僅表"真出網")拆到獨立列。
        // 舊庫 uploaded_to_ocr=1 的圖視為已終結(analysis_state 預設 0 會被重掃,故遷移時把舊 =1 回填為已終結)。
        AddColumnIfMissing(conn, "images", "analysis_state", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "images", "analysis_attempts", "INTEGER NOT NULL DEFAULT 0");
        BackfillAnalysisState(conn);
        AddColumnIfMissing(conn, "suspicious_queue", "source", "TEXT NOT NULL DEFAULT 'suspicion'"); // M5:區分可裁決作弊線索(預設)與唯讀健康告警
        MigrateHeartbeatPk(conn);                                       // 心跳 PK 補 exam/seat 維(舊庫重建·心跳短暫可棄)

        // 效能#4:為看板/檢索高頻查詢補索引(冪等 · 既有庫也補)。僅新增讀取路徑,不改變寫入語義。
        //   ix_images_exam_id             → /search-image 的 images JOIN(exam_id) 過濾 + 歸檔清理
        //   ix_suspicious_queue_exam_*    → /suspicious、/health 按 (exam, source, status) 過濾
        //   ix_events_exam_type / _ts     → 座位熱力(M5 健康計數)與 /events 時間線按 exam 過濾
        AddIndexIfMissing(conn, "ix_images_exam_id", "images", "(exam_id)");
        AddIndexIfMissing(conn, "ix_suspicious_queue_exam_src_status", "suspicious_queue", "(exam_id, source, status)");
        AddIndexIfMissing(conn, "ix_events_exam_type", "events", "(exam_id, type)");
        AddIndexIfMissing(conn, "ix_events_exam_ts", "events", "(exam_id, ts)");
    }

    /// 舊 PK=(agent_id, ts) → 新 PK=(exam_id, seat_id, agent_id, ts)。心跳是 ~90s 視窗的線上指示、且歸檔時清理,
    /// 丟棄舊行至多損失幾分鐘"誰線上"(每 30s 自愈),故遷移直接 DROP+重建(CREATE IF NOT EXISTS 只對新庫建了新 PK)。
    private static void MigrateHeartbeatPk(SqliteConnection conn)
    {
        // 檢測:舊錶裡 exam_id 不在 PK(pk=0)→ 需遷移;新表 exam_id 在 PK(pk>0)→ 跳過。
        bool needMigrate;
        using (SqliteCommand q = conn.CreateCommand())
        {
            q.CommandText = "SELECT pk FROM pragma_table_info('agent_heartbeats') WHERE name='exam_id'";
            object? pk = q.ExecuteScalar();
            needMigrate = pk is not null && Convert.ToInt64(pk) == 0;
        }
        if (!needMigrate) return;

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            @"DROP TABLE agent_heartbeats;
              CREATE TABLE agent_heartbeats (
                agent_id TEXT NOT NULL, exam_id TEXT NOT NULL, seat_id TEXT NOT NULL,
                ts REAL NOT NULL, status TEXT NOT NULL,
                PRIMARY KEY (exam_id, seat_id, agent_id, ts));
              CREATE INDEX IF NOT EXISTS ix_hb_exam_ts ON agent_heartbeats(exam_id, ts);";
        cmd.ExecuteNonQuery();
    }

    /// 三道門遷移的回填:既有工作階段行的兩個新列為 NULL,回落到 `issued_at`。
    ///
    /// ★★ **不能留 NULL,也不能填 0**。兩者的表現都是「升級那一刻全體線上工作階段立刻失效」:
    ///   NULL 參與比較恆為假 → 心跳那道門的寫入判據永遠不成立 → 心跳永遠續不上;
    ///   填 0(紀元)→ 直接就是過期。用 `issued_at` 才是如實的:建工作階段那一刻我們確實見過這個人。
    /// ★ 冪等:只碰 IS NULL 的行,重跑 no-op。
    private static void BackfillSessionGateColumns(SqliteConnection conn)
    {
        foreach (string t in new[] { "oidc_sessions", "admin_sessions" })
        {
            using SqliteCommand c = conn.CreateCommand();
            c.CommandText =
                $@"UPDATE {t} SET last_heartbeat_at = COALESCE(last_heartbeat_at, issued_at),
                                  last_seen_at      = COALESCE(last_seen_at,      issued_at)
                   WHERE last_heartbeat_at IS NULL OR last_seen_at IS NULL";
            c.ExecuteNonQuery();
        }
    }

    /// 遷移舊庫:舊語義下 uploaded_to_ocr=1 表示"已認領/已分析",新語義把閂鎖移到 analysis_state。
    /// 故把舊的 uploaded_to_ocr=1 且 analysis_state=0 的行標為已終結,避免升級後補償重掃把全部歷史圖重新送分析。冪等(重跑 no-op)。
    private static void BackfillAnalysisState(SqliteConnection conn)
    {
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = "UPDATE images SET analysis_state=1 WHERE analysis_state=0 AND uploaded_to_ocr=1";
        c.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string decl)
    {
        bool exists;
        using (SqliteCommand q = conn.CreateCommand())
        {
            q.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$c";
            q.Parameters.AddWithValue("$c", column);
            exists = q.ExecuteScalar() is not null;
        }
        if (exists) return;
        using SqliteCommand alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl}";
        alter.ExecuteNonQuery();
    }

    /// 冪等補索引(效能#4):僅當 sqlite_master 中尚無同名索引才建,避免重複 CREATE 回報錯誤。
    private static void AddIndexIfMissing(SqliteConnection conn, string name, string table, string cols)
    {
        bool exists;
        using (SqliteCommand q = conn.CreateCommand())
        {
            q.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$n";
            q.Parameters.AddWithValue("$n", name);
            exists = q.ExecuteScalar() is not null;
        }
        if (exists) return;
        using SqliteCommand c = conn.CreateCommand();
        c.CommandText = $"CREATE INDEX IF NOT EXISTS {name} ON {table}{cols}";
        c.ExecuteNonQuery();
    }

    /// 先剝離 '--' 行註解,再按 ';' 切分語句。
    /// **必須先去註解**:否則註解裡的分號(如 "與契約 §1.4 一致;seq")會劈裂 DDL 語句。
    /// 該 DDL 無包含 '--' 或 ';' 的字串字面值,故按行去註解 + 簡單切分是安全的。
    private static IEnumerable<string> SplitStatements(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (string line in sql.Split('\n'))
        {
            int c = line.IndexOf("--", StringComparison.Ordinal);
            sb.Append(c >= 0 ? line[..c] : line).Append('\n');
        }
        return sb.ToString().Split(';').Select(s => s.Trim()).Where(s => s.Length > 0);
    }
}
