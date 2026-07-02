-- ============================================================
-- Horus 监考系统 · SQLite **archive** DB DDL
-- 30 天后从 live 迁入的"关键数据":可疑/已判事件、其证据图、OCR/Logo 结果、
-- 裁决记录、考试元数据、哈希锚。非关键数据(干净基线图/低危例行事件/心跳)在 live 端清理,不入档。
--
-- 归档作业(每日扫描 ended_at > 30 天的考试):
--   1. 选"关键事件":被 suspicious_queue.refs 引用 或 risk >= 阈值(默认 50)。
--   2. 复制关键事件 + 其证据图(移入冷存目录,改写 file_path) + OCR/Logo 结果到本库。
--   3. 复制 confirmed/dismissed 的裁决到 archive_adjudications。
--   4. 写 archive_exams(含 summary 汇总)。
--   5. live 端删非关键数据,VACUUM;exams.status='archived'。
-- 完整性:保留每条关键事件的 hash_self/sig 作独立锚点(整链已因清理而断,见 architecture §13.2)。
-- ============================================================
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS archive_exams (
  exam_id     TEXT PRIMARY KEY,
  name        TEXT,
  started_at  REAL,
  ended_at    REAL,
  archived_at REAL NOT NULL,
  summary     TEXT                                    -- JSON: 人数/告警数/确认违规数/迁移计数等
);

-- 关键事件(被可疑项引用 或 risk>=阈值) ----------------------
CREATE TABLE IF NOT EXISTS archive_events (
  id                INTEGER PRIMARY KEY,              -- 沿用 live events.id
  exam_id           TEXT NOT NULL,
  seat_id           TEXT NOT NULL,
  agent_id          TEXT,
  machine_id        TEXT,                             -- canonicalCore 含 machineId:随档留存才能日后独立逐字节复算 hash_self
  seq               INTEGER,
  ts                REAL NOT NULL,
  type              TEXT NOT NULL,
  payload           TEXT NOT NULL,
  risk              INTEGER,                            -- **原始 agent 自报** risk(canonicalCore 签的正是它,归档后凭 machine_id+risk+payload 可独立复算 hash_self)
  server_risk       INTEGER,                            -- 服务器独立复判(旁注·不入 canonical):留存"为何被归档为关键"的取证依据,避免归档库只见 risk=0
  evidence_image_id TEXT,
  hash_prev         TEXT,
  hash_self         TEXT,                             -- 完整性锚点
  sig               TEXT
);
CREATE INDEX IF NOT EXISTS ix_aevents_seat ON archive_events(exam_id, seat_id, ts);

-- 证据图(迁入冷存,file_path 已改写) --------------------------
CREATE TABLE IF NOT EXISTS archive_images (
  image_id   TEXT PRIMARY KEY,
  exam_id    TEXT NOT NULL,
  seat_id    TEXT NOT NULL,
  ts         REAL NOT NULL,
  trigger    TEXT,
  phash      TEXT,
  file_path  TEXT NOT NULL,                           -- 冷存路径 archive/<exam>/<seat>/<id>.webp
  width      INTEGER,
  height     INTEGER,
  format     TEXT,
  bytes      INTEGER
);
CREATE INDEX IF NOT EXISTS ix_aimages_seat ON archive_images(exam_id, seat_id, ts);

CREATE TABLE IF NOT EXISTS archive_ocr_results (
  image_id   TEXT PRIMARY KEY,
  engine     TEXT,
  text       TEXT,
  hits       TEXT,
  confidence REAL,
  created_at REAL
);

CREATE TABLE IF NOT EXISTS archive_logo_hits (
  id         INTEGER PRIMARY KEY,
  image_id   TEXT NOT NULL,
  label      TEXT,
  score      REAL,
  bbox       TEXT,
  created_at REAL
);

-- 关键击键样本(被裁决引用 或 risk>=阈值)长期留存 ----------------
-- confirmed 裁决可能唯一证据就是击键时间线/特征(整段粘贴/空窗后突现整段代码),必须随裁决一并归档。
CREATE TABLE IF NOT EXISTS archive_keystroke_samples (
  id            INTEGER PRIMARY KEY,                  -- 沿用 live keystroke_samples.id
  exam_id       TEXT NOT NULL,
  seat_id       TEXT NOT NULL,
  submission_id TEXT,
  ts            REAL NOT NULL,
  timeline      TEXT,                                 -- JSON: keydown 时间戳序列
  features      TEXT,                                 -- JSON: pasteCount/maxBurstCharsPerSec/idleThenBlock 等
  risk          INTEGER
);
CREATE INDEX IF NOT EXISTS ix_akeystroke_seat ON archive_keystroke_samples(exam_id, seat_id, ts);

-- 裁决结论(已 confirmed/dismissed)长期留存 --------------------
CREATE TABLE IF NOT EXISTS archive_adjudications (
  id          INTEGER PRIMARY KEY,                    -- 沿用 live suspicious_queue.id
  exam_id     TEXT NOT NULL,
  seat_id     TEXT NOT NULL,
  ts          REAL NOT NULL,
  kind        TEXT NOT NULL,
  score       INTEGER,
  status      TEXT NOT NULL,                          -- confirmed|dismissed
  refs        TEXT,
  reviewer    TEXT,
  decided_at  REAL,
  note        TEXT
);
CREATE INDEX IF NOT EXISTS ix_aadj_seat ON archive_adjudications(exam_id, seat_id);
