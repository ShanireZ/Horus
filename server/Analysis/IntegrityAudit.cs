using Horus.Contracts;
using Horus.Server.Data;
using Microsoft.Data.Sqlite;

namespace Horus.Server.Analysis;

/// 哈希链完整性审计(M3·取证强化)。对某场考试的 live 事件做**离线复验**,回答三个独立问题:
///
///   ① **锚点自洽(hashOk)**:每条事件的 `hash_self` 是否确实 = SHA256(`hash_prev` + "\n" + canonicalCore(落库字段))?
///      —— 从**落库的 payload/字段**逐字节复算(见 EventCanonical.CoreRaw)。不符 = 落库后 payload/字段被改动。
///      注:hash_self 是**无密钥 SHA256**,非 PSK 方改 payload 后可**重算**使其自洽 → 单靠 hashOk 挡不住老练篡改。
///   ② **签名自洽(sigOk)**:`sig` 是否 = HMAC-SHA256(PSK, `hash_self` + "\n" + `seq`)?**唯有持 PSK 者能产生合法 sig**,
///      故这是真正能识破「非 PSK 方改 payload+重算 hashSelf」的锚点。配了 PSK 才验;hashOk 通过但 sigOk 失败 = 疑非 PSK 篡改。
///   ③ **链连续(chainOk)**:按 (agent_id, seq) 升序,每条 `hash_prev` 是否 = 前一条 `hash_self`?
///      `hash_prev="GENESIS"` = **合法段起点**(该 agent 首条,或 **Agent 重启后新起链段**)—— **不算断裂**
///      (Agent 每次进程启动新建 HashChain,重启边界事件 hash_prev=GENESIS 属正常;伪造的 GENESIS 边界会被 ② 的 sig 校验识破)。
///      非 GENESIS 的 hash_prev 与前驱 hash_self 不符 = 删除/插入/重排。
///
/// **诚实边界(architecture §10.1)**:持 PSK 的学员机可构造**自洽的**伪造链(①②③ 全过),故本审计**不**能证明"内容为真",
/// 只能证明"落库后未被无 PSK 方改动 / 未被静默删增"。内容真伪靠截图 / 视觉 / 人工裁决。
/// **迁移前旧数据**:events.machine_id 是 M3 才加的列(旧行 NULL),canonicalCore 含 machineId → 旧事件无从复算 hashSelf,
/// 归入 `unverifiable` 单列(既不算 hashOk 也不当篡改),**绝不对合法历史数据误报"篡改"**。
public static class IntegrityAudit
{
    public sealed record Issue(long Id, long Seq, string Detail);

    public sealed record AgentChain(
        string AgentId, string SeatId, int Total, int HashOk, int ChainOk, int Unverifiable, int RestartBoundaries,
        IReadOnlyList<Issue> HashMismatches, IReadOnlyList<Issue> ContinuityBreaks)
    {
        // Ok = 未发现篡改证据。Unverifiable(迁移前缺 machine_id)与 RestartBoundaries(重启锚点)不算失败。
        public bool Ok => HashMismatches.Count == 0 && ContinuityBreaks.Count == 0;
    }

    public sealed record Report(
        string ExamId, int TotalEvents, int TotalHashOk, int TotalChainOk, int TotalUnverifiable, int TotalRestartBoundaries,
        bool SigVerified, IReadOnlyList<AgentChain> Agents)
    {
        public bool Ok => Agents.All(a => a.Ok);
        // 诚实标注(第三轮 D6):psk=null(联调/未配 PSK)时**从未验签**,ok:true 只代表"锚点自洽 + 链连续",
        // **不代表**未被持 PSK 者篡改。sigVerified=false 时消费方(看板/运维)须知这是"未验签的绿"。
        public string? Note => SigVerified ? null : "未验签(psk 未配置):ok 仅表锚点自洽+链连续,不含 sig 校验;生产须配 PSK";
    }

    private sealed record Row(
        long Id, string SeatId, string AgentId, string? MachineId, long Seq, double Ts,
        string Type, string Payload, int Risk, string? EvidenceImageId, string? HashPrev, string? HashSelf, string? Sig);

    /// 在**只读连接**上执行(调用方走 db.Read)。按 agent 分组、seq 升序复验。
    /// psk 非 null 时附加 sig 校验(生产必配);null 时仅验 hashSelf + 链连续(联调无 PSK)。
    public static Report Run(SqliteConnection conn, string examId, byte[]? psk = null)
    {
        // 一次性拉取该考试全部事件,按 (agent_id, seq) 升序 —— 复现 Agent 封链的先后。
        var byAgent = new Dictionary<string, List<Row>>();
        var seatOf = new Dictionary<string, string>();
        using (SqliteCommand c = conn.Cmd(
            @"SELECT id, seat_id, agent_id, machine_id, seq, ts, type, payload, risk, evidence_image_id, hash_prev, hash_self, sig
              FROM events WHERE exam_id=@e ORDER BY agent_id, seq", ("@e", examId)))
        using (SqliteDataReader r = c.ExecuteReader())
        {
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
                if (!byAgent.TryGetValue(row.AgentId, out List<Row>? list)) byAgent[row.AgentId] = list = new();
                list.Add(row);
                seatOf[row.AgentId] = row.SeatId;   // 取该 agent 见到的 seat(通常固定)
            }
        }

        var agents = new List<AgentChain>();
        int totalEvents = 0, totalHashOk = 0, totalChainOk = 0, totalUnverifiable = 0, totalRestart = 0;

        foreach ((string agentId, List<Row> rows) in byAgent)
        {
            var hashMiss = new List<Issue>();
            var chainMiss = new List<Issue>();
            int unverifiable = 0, hashOkCount = 0, restart = 0;
            string? prevSelf = null;   // null → 尚无前驱(首条应链到 GENESIS)

            foreach (Row e in rows)
            {
                // ①+②锚点自洽:从落库字段(含 machine_id)复算 hashSelf,并(配了 PSK 时)复验 sig。
                //    迁移前旧事件缺 machine_id → 归 unverifiable,既不算 hashOk 也不当篡改。
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

                // ③链连续:hash_prev="GENESIS"=合法段起点(首条或重启锚点),不算断裂;否则应 = 前一条 hash_self。
                string gotPrev = e.HashPrev ?? "";
                if (gotPrev == "GENESIS")
                {
                    if (prevSelf is not null) restart++;   // 非首条却链到 GENESIS = Agent 重启后新起链段
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
            // chainOk 口径 = 连续性未断的行数。**与 hashOk 口径不同**:hashOk 只数"锚点+签名都验过"的行(不含 unverifiable);
            // 而连续性检查对**每一行**(含重启锚点、含迁移前缺 machineId 的行)都做了 hash_prev↔前驱 比对,故它们计入 chainOk 是正确的
            // (它们的链确实没断)。看板若要"纯净度",另有 totalUnverifiable / totalRestartBoundaries 正交呈现。
            totalChainOk += rows.Count - chainMiss.Count;
            agents.Add(new AgentChain(
                agentId, seatOf[agentId], rows.Count, hashOkCount, rows.Count - chainMiss.Count, unverifiable, restart,
                hashMiss, chainMiss));
        }

        return new Report(examId, totalEvents, totalHashOk, totalChainOk, totalUnverifiable, totalRestart, psk is not null, agents);
    }

    private static string Short(string h) => h.Length <= 12 ? h : h[..12] + "…";
}
