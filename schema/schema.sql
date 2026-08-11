-- ============================================================
-- Horus 监考系统 · SQLite **live** DB DDL (M1)
-- 热库:当前 + 近 30 天。30 天后关键数据转 archive(见 schema-archive.sql),其余清理。
-- 字段命名与 api-contract-m1.md / Horus.Agent 对齐(库内用 snake_case)。
-- ============================================================
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- 考试 --------------------------------------------------------
CREATE TABLE IF NOT EXISTS exams (
  exam_id      TEXT PRIMARY KEY,
  name         TEXT NOT NULL,
  started_at   REAL,                                  -- Unix 秒
  ended_at     REAL,
  status       TEXT NOT NULL DEFAULT 'active',        -- active|ended|archiving|archived（archiving=归档进行中,ingest 短路）
  created_at   REAL NOT NULL
);

-- 座位 / 学员 / 机器 / Agent 绑定 ------------------------------
CREATE TABLE IF NOT EXISTS seats (
  exam_id      TEXT NOT NULL REFERENCES exams(exam_id),
  seat_id      TEXT NOT NULL,
  student_id   TEXT,
  machine_id   TEXT,
  agent_id     TEXT,
  display_name TEXT,
  PRIMARY KEY (exam_id, seat_id)
);
CREATE INDEX IF NOT EXISTS ix_seats_agent ON seats(agent_id);

-- 事件流(元数据信号) -----------------------------------------
CREATE TABLE IF NOT EXISTS events (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id           TEXT NOT NULL,
  seat_id           TEXT NOT NULL,
  agent_id          TEXT NOT NULL,
  machine_id        TEXT,                             -- 机器标识;canonicalCore 含 machineId,须落库以支持 M3 链复验
  seq               INTEGER NOT NULL,                 -- agent 单调序号
  ts                REAL NOT NULL,                    -- agent 本机时钟(Unix 秒)
  recv_ts           REAL NOT NULL,                    -- 服务器接收时钟
  type              TEXT NOT NULL,                    -- window_focus|browser_url|process_start|...
  payload           TEXT NOT NULL,                    -- JSON
  risk              INTEGER NOT NULL DEFAULT 0,        -- **Agent 自报**初判(原样留证,不改)
  server_risk       INTEGER,                          -- **服务器独立复判**(不信任 Agent risk);入队/看板用 max(risk,server_risk)
  evidence_image_id TEXT,                             -- → images.image_id
  hash_prev         TEXT,
  hash_self         TEXT,
  sig               TEXT,
  UNIQUE (agent_id, seq)                              -- 幂等去重 / 断网续传(与契约 §1.4 一致，seq 每事件唯一)
);
CREATE INDEX IF NOT EXISTS ix_events_seat_ts ON events(exam_id, seat_id, ts, risk);  -- 含 risk:看板 MAX(risk) 免回表
CREATE INDEX IF NOT EXISTS ix_events_risk    ON events(exam_id, risk);
CREATE INDEX IF NOT EXISTS ix_events_type    ON events(exam_id, type);
-- 图片入库反向补标 is_evidence 时按 evidence_image_id 查(部分索引,仅索引有引用的行)
CREATE INDEX IF NOT EXISTS ix_events_evidence ON events(evidence_image_id) WHERE evidence_image_id IS NOT NULL;

-- 截图元数据(原图存文件系统,这里只存指针) --------------------
CREATE TABLE IF NOT EXISTS images (
  image_id        TEXT PRIMARY KEY,                   -- 服务器分配(uuid)
  exam_id         TEXT NOT NULL,
  seat_id         TEXT NOT NULL,
  agent_id        TEXT NOT NULL,
  ts              REAL NOT NULL,
  recv_ts         REAL NOT NULL,
  trigger         TEXT NOT NULL,                      -- event:browser|event:paste|baseline_random|...
  phash           TEXT NOT NULL,                      -- 16 hex (dHash 64bit)
  file_path       TEXT NOT NULL,                      -- 局域网内相对路径 images/<exam>/<seat>/<id>.webp
  width           INTEGER,
  height          INTEGER,
  format          TEXT NOT NULL DEFAULT 'webp',
  bytes           INTEGER,
  -- 隐私审计:图**字节是否真出局域网**送云视觉。仅当 SendsOffNetwork 分析器**成功送出**才置 1;
  -- 本地/mock(不出网)或从未成功送出的图恒 0。**不再兼作处理认领闩锁**(闩锁改用 analysis_state,闭合第三轮 F2 语义冲突)。
  uploaded_to_ocr INTEGER NOT NULL DEFAULT 0,
  -- 视觉分析状态(处理闩锁):0=待分析 1=已终结(成功落库 / 派生失败 / 文件缺失等确定态,不再重扫)。
  -- 与 uploaded_to_ocr 解耦:临时云失败**不置 1** → 保持 0 由补偿重扫拾回(闭合第三轮 F1 临时失败永久漏析)。
  analysis_state  INTEGER NOT NULL DEFAULT 0,
  -- 已认领分析的次数(含失败):补偿重扫按 attempts < 上限 重试临时失败,超限则放弃防死循环。
  analysis_attempts INTEGER NOT NULL DEFAULT 0,
  is_evidence     INTEGER NOT NULL DEFAULT 0          -- 是否被某可疑项引用(归档保留判据)
);
CREATE INDEX IF NOT EXISTS ix_images_analysis ON images(exam_id, analysis_state) WHERE analysis_state=0;
CREATE INDEX IF NOT EXISTS ix_images_seat_ts ON images(exam_id, seat_id, ts);
CREATE INDEX IF NOT EXISTS ix_images_phash   ON images(exam_id, seat_id, phash);

-- 云 OCR 结果 (L2) -------------------------------------------
CREATE TABLE IF NOT EXISTS ocr_results (
  image_id    TEXT PRIMARY KEY REFERENCES images(image_id),
  engine      TEXT NOT NULL,                          -- 供应商标识
  text        TEXT,                                   -- 识别全文
  hits        TEXT,                                   -- JSON: 命中关键词列表
  confidence  REAL,
  created_at  REAL NOT NULL
);

-- Logo / 模板匹配 (L3) ---------------------------------------
CREATE TABLE IF NOT EXISTS logo_hits (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  image_id    TEXT NOT NULL REFERENCES images(image_id),
  label       TEXT NOT NULL,                          -- chatgpt|google|deepseek|...
  score       REAL,
  bbox        TEXT,                                   -- JSON [x,y,w,h]
  created_at  REAL NOT NULL
);

-- 图像向量普通表(M3 实做·按图搜图)——规模小(单场几千图),C# 暴力余弦 KNN,不依赖 sqlite-vec 原生扩展。
-- embedding = float32[dim] 小端字节。仅嵌证据/可疑图(省算力)。
CREATE TABLE IF NOT EXISTS image_embeddings (
  image_id    TEXT PRIMARY KEY,
  dim         INTEGER NOT NULL,
  embedding   BLOB NOT NULL,
  embedded_at REAL NOT NULL
);

-- 击键节奏(外部判题后端经 KSK 旁路 POST /ingest/keystroke 写入；前端埋点已撤) -----------------------------
CREATE TABLE IF NOT EXISTS keystroke_samples (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id       TEXT NOT NULL,
  seat_id       TEXT NOT NULL,
  submission_id TEXT,
  ts            REAL NOT NULL,
  timeline      TEXT,                                 -- JSON: keydown 时间戳序列(可降采样)
  features      TEXT,                                 -- JSON: pasteCount/maxBurstCharsPerSec/idleThenBlock 等
  risk          INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_keystroke_seat ON keystroke_samples(exam_id, seat_id, ts);

-- 可疑事件队列(系统初筛产出 → 人工裁决) ----------------------
CREATE TABLE IF NOT EXISTS suspicious_queue (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id     TEXT NOT NULL,
  seat_id     TEXT NOT NULL,
  ts          REAL NOT NULL,
  kind        TEXT NOT NULL,                          -- web_ai|search|non_whitelist_proc|large_paste|usb|ide_plugin_suspect|...
  score       INTEGER NOT NULL,
  status      TEXT NOT NULL DEFAULT 'pending',        -- pending|reviewing|confirmed|dismissed
  refs        TEXT,                                   -- JSON: 关联 events.id / images.image_id
  reviewer    TEXT,
  decided_at  REAL,
  note        TEXT,
  source      TEXT NOT NULL DEFAULT 'suspicion'  -- suspicion(作弊线索·可裁决) | health(采集端健康告警·仅提示·不可裁决)
);
CREATE INDEX IF NOT EXISTS ix_susp_status ON suspicious_queue(exam_id, status, score);

-- 每考试已下发配置(白名单/阈值/截图参数)持久化 -------------
-- 服务器重启后回填内存缓存,使 server_risk 白名单复判不退化、Agent 重连 hello 时能补推(见 architecture §10.2)。
CREATE TABLE IF NOT EXISTS exam_config (
  exam_id    TEXT PRIMARY KEY,
  config     TEXT NOT NULL,                          -- 下发的 camelCase 配置 JSON 原文
  updated_at REAL NOT NULL
);

-- M4 身份层:OIDC 采集会话(取代共享 PSK) --------------------
-- Agent 经 wentian OIDC 登录后,服务器派发一条会话:绑定 wentian 身份(sub + 富画像)到 (exam,seat,agent),
-- 派生的 k_sess(ECDH·32B base64)作采集签名密钥。事件体身份须 == 本会话绑定值,闭合跨身份栽赃/seq 抢占。
-- 持久化:服务器重启后会话不丢(考试中途不必强制学员重登)。k_sess 存于可信服务器 DB(同 PSK 的信任面)。
CREATE TABLE IF NOT EXISTS oidc_sessions (
  session_id   TEXT PRIMARY KEY,
  exam_id      TEXT NOT NULL,
  seat_id      TEXT NOT NULL,
  agent_id     TEXT NOT NULL,
  machine_id   TEXT,
  sub          TEXT NOT NULL,                          -- 贝塔通稳定身份(UUID)·永不变更
  name         TEXT,                                    -- 真实姓名(标准 profile scope 的 name)
  username     TEXT,                                    -- 用户名(标准 profile 的 preferred_username)·**座位标识的来源**
  -- ★ 此前还有 user_type/nickname/dao_name/avatar/realm/realm_level/combat_power 七列,
  --   全部来自 wentian 自定义 scope `horus_profile`。贝塔通 P81 停发,已移除。
  --   既有 dev 库里那几列会留着(SQLite 不走 DROP COLUMN),无人读、无害。
  k_sess       TEXT NOT NULL,                          -- base64(32B) ECDH 派生会话密钥(HMAC 签名密钥)
  issued_at    REAL NOT NULL,
  -- ★ 三道门(贝塔通 P88–P92):expires_at = absolute(任何活动都推不动它),
  --   另两道靠下面两列。判定顺序 revoked → absolute → idle → heartbeat,见 SessionGates。
  expires_at   REAL NOT NULL,
  last_heartbeat_at REAL NOT NULL,                     -- 心跳门:最后一次收到心跳的时刻(任何心跳都续)
  last_seen_at      REAL NOT NULL                      -- idle 门:最后一次「人还在」·★ 只由 active:true 的心跳续
);
CREATE INDEX IF NOT EXISTS ix_oidc_sessions_agent ON oidc_sessions(exam_id, agent_id);

-- 监考员看板管理会话(贝塔通 dashboard OIDC 登录·取代静态 adminToken) ----
-- ★★ 准入判据在**身份中心**:看板客户端归属平台 `horus-admin`,没有该平台权限的人
--    在贝塔通的授权阶段就被拒、换不到 code(贝塔通 P83)。所以能走到建会话这一步的**就是监考员**,
--    本表不再存任何角色字段。admin gate(AdminAuthMode=oidc)只校验「此会话在且未过期」。
CREATE TABLE IF NOT EXISTS admin_sessions (
  session_id   TEXT PRIMARY KEY,
  sub          TEXT NOT NULL,                          -- 贝塔通稳定身份(UUID)·永不变更
  name         TEXT,                                   -- 真实姓名(标准 profile scope 的 name)
  issued_at    REAL NOT NULL,
  -- 三道门同 oidc_sessions。★ 看板是**持续自动轮询**的,所以「任何业务请求都不得续 idle」
  --   这条对它尤其致命 —— 轮询算作活动的话,监考机上开着看板就等于永不登出。
  expires_at   REAL NOT NULL,
  last_heartbeat_at REAL NOT NULL,
  last_seen_at      REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_admin_sessions_sub ON admin_sessions(sub);

-- 会话「为什么没了」的留痕(供 401 时给用户一句真话) ------------------
-- ★★ 撤权的处置是**删行** —— 删掉才不会被任何一条忘了加过滤条件的裸 SQL 意外复活
--    (本仓反复吃过这个亏,见 SchemaDriftTests 的类注释)。代价是「为什么没了」随之消失,
--    于是被踢的人只能看到一句笼统的「登录已失效」。
-- ★ 这张表就补这一口:**只存一句给用户看的原因,不含任何凭据**,所以它复活不了任何东西。
-- ★ 它尤其重要的场合是「平台权限被关掉」:取消 SSO(贝塔通 P84/P98)之后,
--    每点一次登录都要**完整输一遍密码**才被拒 —— 不告诉他真实原因,他会一遍遍白输。
CREATE TABLE IF NOT EXISTS revoked_session_notices (
  session_id   TEXT PRIMARY KEY,                      -- 已被删掉的那条会话的 id(cookie/凭证里还留着)
  reason       TEXT NOT NULL,                         -- 贝塔通的 reason,或本地口径(exam_logout 等)
  revoked_at   REAL NOT NULL
);

-- 贝塔通撤权通知的幂等台账(其 P44/P69·rp-contract「/internal/revoke」) ----
-- 贝塔通判成功的口径是 **2xx**,超时只有 5 秒 —— 处理成功但花了 6 秒的一发会被判失败并重投,
-- 于是同一个 `jti` 必然会来第二次。「反正只会来一次」是错的,幂等不是可选项。
-- ★ 重投时 `jti` **不变**,所以按它做主键即可;记下处置结果好让重投原样回同一个答案。
CREATE TABLE IF NOT EXISTS betapass_revocations (
  jti           TEXT PRIMARY KEY,                      -- 幂等键(贝塔通队列行 id)·重投不变
  sub           TEXT NOT NULL,                         -- 被撤权的账号(贝塔通稳定身份)
  client_id     TEXT NOT NULL,                         -- 令牌的 aud:区分撤的是采集面还是监考台
  reason        TEXT,                                  -- 只用于留痕与提示语,**不参与是否清会话的判断**
  received_at   REAL NOT NULL,
  revoked_count INTEGER NOT NULL                       -- 这一发实际清掉几条本地会话
);
CREATE INDEX IF NOT EXISTS ix_betapass_revocations_sub ON betapass_revocations(sub);

-- Agent 心跳 / 在线状态 --------------------------------------
CREATE TABLE IF NOT EXISTS agent_heartbeats (
  agent_id   TEXT NOT NULL,
  exam_id    TEXT NOT NULL,
  seat_id    TEXT NOT NULL,
  ts         REAL NOT NULL,
  status     TEXT NOT NULL,                           -- alive|degraded|...
  -- PK 含 exam/seat:同一 agent_id 换座复用 + 同毫秒 ts 时,不同 seat 的心跳不再撞同一行、seat 归属不被覆盖污染在线判定
  PRIMARY KEY (exam_id, seat_id, agent_id, ts)
);
-- 看板在线判定按 (exam, ts>=cut) 查,否则全表扫心跳表
CREATE INDEX IF NOT EXISTS ix_hb_exam_ts ON agent_heartbeats(exam_id, ts);
