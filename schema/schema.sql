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

-- 图像向量 (CLIP) — sqlite-vec 虚拟表,做"按图搜图"相似检索 -----
-- 需先加载扩展:  .load ./vec0     (sqlite-vec)
CREATE VIRTUAL TABLE IF NOT EXISTS vec_images USING vec0(
  image_id  TEXT PRIMARY KEY,
  embedding FLOAT[512]
);

-- 击键节奏(判题网页前端埋点上报) -----------------------------
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
  note        TEXT
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
-- Agent 经 cpplearn OIDC 登录后,服务器派发一条会话:绑定 cpplearn 身份(sub + 富画像)到 (exam,seat,agent),
-- 派生的 k_sess(ECDH·32B base64)作采集签名密钥。事件体身份须 == 本会话绑定值,闭合跨身份栽赃/seq 抢占。
-- 持久化:服务器重启后会话不丢(考试中途不必强制学员重登)。k_sess 存于可信服务器 DB(同 PSK 的信任面)。
CREATE TABLE IF NOT EXISTS oidc_sessions (
  session_id   TEXT PRIMARY KEY,
  exam_id      TEXT NOT NULL,
  seat_id      TEXT NOT NULL,
  agent_id     TEXT NOT NULL,
  machine_id   TEXT,
  sub          TEXT NOT NULL,                          -- cpplearn 稳定身份(UUID)
  username     TEXT, nickname TEXT, dao_name TEXT, avatar TEXT, realm TEXT,
  realm_level  INTEGER, combat_power INTEGER,
  k_sess       TEXT NOT NULL,                          -- base64(32B) ECDH 派生会话密钥(HMAC 签名密钥)
  issued_at    REAL NOT NULL,
  expires_at   REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_oidc_sessions_agent ON oidc_sessions(exam_id, agent_id);

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
