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
  status       TEXT NOT NULL DEFAULT 'active',        -- active|ended|archived
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
  uploaded_to_ocr INTEGER NOT NULL DEFAULT 0,         -- 是否已送云 OCR(隐私审计)
  is_evidence     INTEGER NOT NULL DEFAULT 0          -- 是否被某可疑项引用(归档保留判据)
);
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

-- Agent 心跳 / 在线状态 --------------------------------------
CREATE TABLE IF NOT EXISTS agent_heartbeats (
  agent_id   TEXT NOT NULL,
  exam_id    TEXT NOT NULL,
  seat_id    TEXT NOT NULL,
  ts         REAL NOT NULL,
  status     TEXT NOT NULL,                           -- alive|degraded|...
  PRIMARY KEY (agent_id, ts)
);
-- 看板在线判定按 (exam, ts>=cut) 查,否则全表扫心跳表
CREATE INDEX IF NOT EXISTS ix_hb_exam_ts ON agent_heartbeats(exam_id, ts);
