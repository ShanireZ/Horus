using Horus.Contracts;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Analysis;

/// 雜湊鏈完整性稽核(M3·取證強化)。對某場考試的 live 事件做**離線複驗**,回答三個獨立問題:
///
///   ① **錨點自洽(hashOk)**:每條事件的 `hash_self` 是否確實 = SHA256(`hash_prev` + "\n" + canonicalCore(寫入資料庫欄位))?
///      —— 從**寫入資料庫的 payload/欄位**逐位元組復算(見 EventCanonical.CoreRaw)。不符 = 寫入資料庫後 payload/欄位被改動。
///      注:hash_self 是**無金鑰 SHA256**,非 PSK 方改 payload 後可**重算**使其自洽 → 單靠 hashOk 擋不住老練篡改。
///   ② **簽名自洽(sigOk)**:`sig` 是否 = HMAC-SHA256(PSK, `hash_self` + "\n" + `seq`)?**唯有持 PSK 者能產生合法 sig**,
///      故這是真正能識破「非 PSK 方改 payload+重算 hashSelf」的錨點。配了 PSK 才驗;hashOk 通過但 sigOk 失敗 = 疑非 PSK 篡改。
///   ③ **鏈連續(chainOk)**:按 (agent_id, seq) 升序,每條 `hash_prev` 是否 = 前一條 `hash_self`?
///      `hash_prev="GENESIS"` = **合法段起點**(該 agent 首條,或 **Agent 重新啟動後新起鏈段**)—— **不算斷裂**
///      (Agent 每次處理程序啟動新建 HashChain,重新啟動邊界事件 hash_prev=GENESIS 屬正常;偽造的 GENESIS 邊界會被 ② 的 sig 檢核識破)。
///      非 GENESIS 的 hash_prev 與前驅 hash_self 不符 = 刪除/插入/重排。
///
/// **誠實邊界(architecture §10.1)**:持 PSK 的學員機可建構**自洽的**偽造鏈(①②③ 全過),故本稽核**不**能證明"內容為真",
/// 只能證明"寫入資料庫後未被無 PSK 方改動 / 未被靜默刪增"。內容真偽靠截圖 / 視覺 / 人工裁決。
/// **遷移前舊資料**:events.machine_id 是 M3 才加的列(舊行 NULL),canonicalCore 含 machineId → 舊事件無從復算 hashSelf,
/// 歸入 `unverifiable` 單列(既不算 hashOk 也不當篡改),**絕不對合法歷史資料誤報"篡改"**。
public static class IntegrityAudit
{
    public sealed record Issue(long Id, long Seq, string Detail);

    public sealed record AgentChain(
        string AgentId, string SeatId, int Total, int HashOk, int ChainOk, int Unverifiable, int RestartBoundaries,
        IReadOnlyList<Issue> HashMismatches, IReadOnlyList<Issue> ContinuityBreaks)
    {
        // Ok = 未發現篡改證據。Unverifiable(遷移前缺 machine_id)與 RestartBoundaries(重新啟動錨點)不算失敗。
        public bool Ok => HashMismatches.Count == 0 && ContinuityBreaks.Count == 0;
    }

    public sealed record Report(
        string ExamId, int TotalEvents, int TotalHashOk, int TotalChainOk, int TotalUnverifiable, int TotalRestartBoundaries,
        bool SigVerified, IReadOnlyList<AgentChain> Agents)
    {
        public bool Ok => Agents.All(a => a.Ok);
        // 誠實標註(第三輪 D6):psk=null(整合測試/未配 PSK)時**從未驗籤**,ok:true 只代表"錨點自洽 + 鏈連續",
        // **不代表**未被持 PSK 者篡改。sigVerified=false 時消費方(看板/維運)須知這是"未驗籤的綠"。
        public string? Note => SigVerified ? null : "未验签(psk 未配置):ok 仅表锚点自洽+链连续,不含 sig 校验;生产须配 PSK";
    }

    private sealed record Row(
        long Id, string SeatId, string AgentId, string? MachineId, long Seq, double Ts,
        string Type, string Payload, int Risk, string? EvidenceImageId, string? HashPrev, string? HashSelf, string? Sig);

    /// 在**唯讀連接**上執行(呼叫方走 db.Read)。按 agent 分組、seq 升序複驗。
    /// psk 非 null 時附加 sig 檢核(生產必配);null 時僅驗 hashSelf + 鏈連續(整合測試無 PSK)。
    public static Report Run(SqliteConnection conn, string examId, byte[]? psk = null)
    {
        // 效能#3:流式按 agent 分組處理。結果按 (agent_id, seq) 升序,讀到 agent 切換或 EOF 才處理並清空其緩衝,
        // 峰值記憶體由"全考試事件"降為"單 agent 事件"(極大考試不再 O(N) 撐爆記憶體),稽核語義不變。
        var seatOf = new Dictionary<string, string>();
        var agents = new List<AgentChain>();
        int totalEvents = 0, totalHashOk = 0, totalChainOk = 0, totalUnverifiable = 0, totalRestart = 0;

        // 單 agent 連續事件的稽核(與流式讀取解耦,緩衝邊界記憶體受控)。閉包捕獲 examId/psk 與累計量。
        void ProcessAgent(List<Row> rows, string agentId)
        {
            var hashMiss = new List<Issue>();
            var chainMiss = new List<Issue>();
            int unverifiable = 0, hashOkCount = 0, restart = 0;
            string? prevSelf = null;   // null → 尚無前驅(首條應鏈到 GENESIS)

            foreach (Row e in rows)
            {
                // ①+②錨點自洽:從寫入資料庫欄位(含 machine_id)復算 hashSelf,並(配了 PSK 時)複驗 sig。
                //    遷移前舊事件缺 machine_id → 歸 unverifiable,既不算 hashOk 也不當篡改。
                if (e.MachineId is null)
                {
                    unverifiable++;
                }
                else
                {
                    bool hashOk = EventCanonical.VerifyHashSelf(
                        e.HashPrev ?? "GENESIS", examId, e.SeatId, agentId, e.MachineId, e.Ts,
                        e.Type, e.Payload, e.Risk, e.EvidenceImageId, e.Seq, e.HashSelf);
                    if (!hashOk)
                        hashMiss.Add(new Issue(e.Id, e.Seq, "hash_self 与落库 payload/字段不符(疑落库后改动)"));
                    else if (psk is not null && !EventCanonical.VerifySig(psk, e.HashSelf, e.Seq, e.Sig))
                        hashMiss.Add(new Issue(e.Id, e.Seq, "sig 与 PSK 不符(hash_self 自洽但疑被非 PSK 方重算/伪造)"));
                    else
                        hashOkCount++;
                }

                // ③鏈連續:hash_prev="GENESIS"=合法段起點(首條或重新啟動錨點),不算斷裂;否則應 = 前一條 hash_self。
                string gotPrev = e.HashPrev ?? "";
                if (gotPrev == "GENESIS")
                {
                    if (prevSelf is not null) restart++;   // 非首條卻鏈到 GENESIS = Agent 重新啟動後新起鏈段
                }
                else if (!Crypto.FixedTimeEquals(gotPrev, prevSelf ?? "GENESIS"))
                {
                    chainMiss.Add(new Issue(e.Id, e.Seq,
                        $"hash_prev 与前驱 hash_self 不符(疑删除/插入/重排):期望={Short(prevSelf ?? "GENESIS")} 实得={Short(gotPrev)}"));
                }

                prevSelf = e.HashSelf;
            }

            totalEvents += rows.Count;
            totalHashOk += hashOkCount;
            totalUnverifiable += unverifiable;
            totalRestart += restart;
            // chainOk 口徑 = 連續性未斷的行數。**與 hashOk 口徑不同**:hashOk 只數"錨點+簽名都驗過"的行(不含 unverifiable);
            // 而連續性檢查對**每一行**(含重新啟動錨點、含遷移前缺 machineId 的行)都做了 hash_prev↔前驅 比對,故它們計入 chainOk 是正確的
            // (它們的鏈確實沒斷)。看板若要"純淨度",另有 totalUnverifiable / totalRestartBoundaries 正交呈現。
            totalChainOk += rows.Count - chainMiss.Count;
            agents.Add(new AgentChain(
                agentId, seatOf.TryGetValue(agentId, out string? s) ? s : "", rows.Count, hashOkCount, rows.Count - chainMiss.Count, unverifiable, restart,
                hashMiss, chainMiss));
        }

        using (SqliteCommand c = conn.Cmd(
            @"SELECT id, seat_id, agent_id, machine_id, seq, ts, type, payload, risk, evidence_image_id, hash_prev, hash_self, sig
              FROM events WHERE exam_id=@e ORDER BY agent_id, seq", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            List<Row>? buf = null;
            string? curAgent = null;
            while (r.Read())
            {
                var row = new Row(
                    r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetDouble(5),
                    r.GetString(6), r.IsDBNull(7) ? "{}" : r.GetString(7), r.GetInt32(8),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? null : r.GetString(12));
                if (curAgent is null) curAgent = row.AgentId;
                if (row.AgentId != curAgent)   // agent 切換:處理上一緩衝,起新緩衝
                {
                    ProcessAgent(buf!, curAgent!);
                    buf = null;
                    curAgent = row.AgentId;
                }
                (buf ??= new List<Row>()).Add(row);
                seatOf[row.AgentId] = row.SeatId;   // 取該 agent 見到的 seat(通常固定)
            }
            if (buf is not null) ProcessAgent(buf, curAgent!);
        }

        return new Report(examId, totalEvents, totalHashOk, totalChainOk, totalUnverifiable, totalRestart, psk is not null, agents);
    }

    private static string Short(string h) => h.Length <= 12 ? h : h[..12] + "…";
}
