-- ============================================================
-- Horus 監考系統 · SQLite **archive** DB DDL
-- 30 天后從 live 遷入的"關鍵資料":可疑/已判事件、其證據圖、OCR/Logo 結果、
-- 裁決記錄、考試中繼資料、雜湊錨。非關鍵資料(乾淨基線圖/低危例行事件/心跳)在 live 端清理,不入檔。
--
-- 歸檔作業(每日掃描 ended_at > 30 天的考試):
--   1. 選"關鍵事件":被 suspicious_queue.refs 引用 或 risk >= 閾值(預設 50)。
--   2. 複製關鍵事件 + 其證據圖(移入冷存目錄,改寫 file_path) + OCR/Logo 結果到本庫。
--   3. 複製 confirmed/dismissed 的裁決到 archive_adjudications。
--   4. 寫 archive_exams(含 summary 彙總)。
--   5. live 端刪非關鍵資料,VACUUM;exams.status='archived'。
-- 完整性:保留每條關鍵事件的 hash_self/sig 作獨立錨點(整鏈已因清理而斷,見 architecture §13.2)。
-- ============================================================
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS archive_exams (
  exam_id     TEXT PRIMARY KEY,
  name        TEXT,
  started_at  REAL,
  ended_at    REAL,
  archived_at REAL NOT NULL,
  summary     TEXT                                    -- JSON: 人數/告警數/確認違規數/遷移計數等
);

-- 關鍵事件(被可疑項引用 或 risk>=閾值) ----------------------
CREATE TABLE IF NOT EXISTS archive_events (
  id                INTEGER PRIMARY KEY,              -- 沿用 live events.id
  exam_id           TEXT NOT NULL,
  seat_id           TEXT NOT NULL,
  agent_id          TEXT,
  machine_id        TEXT,                             -- canonicalCore 含 machineId:隨檔留存才能日後獨立逐位元組復算 hash_self
  seq               INTEGER,
  ts                REAL NOT NULL,
  type              TEXT NOT NULL,
  payload           TEXT NOT NULL,
  risk              INTEGER,                            -- **原始 agent 自報** risk(canonicalCore 簽的正是它,歸檔後憑 machine_id+risk+payload 可獨立復算 hash_self)
  server_risk       INTEGER,                            -- 伺服器獨立復判(旁註·不入 canonical):留存"為何被歸檔為關鍵"的取證依據,避免歸檔庫只見 risk=0
  evidence_image_id TEXT,
  hash_prev         TEXT,
  hash_self         TEXT,                             -- 完整性錨點
  sig               TEXT
);
CREATE INDEX IF NOT EXISTS ix_aevents_seat ON archive_events(exam_id, seat_id, ts);

-- 證據圖(遷入冷存,file_path 已改寫) --------------------------
CREATE TABLE IF NOT EXISTS archive_images (
  image_id   TEXT PRIMARY KEY,
  exam_id    TEXT NOT NULL,
  seat_id    TEXT NOT NULL,
  ts         REAL NOT NULL,
  trigger    TEXT,
  phash      TEXT,
  file_path  TEXT NOT NULL,                           -- 冷存路徑 archive/<exam>/<seat>/<id>.webp
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

-- 關鍵擊鍵樣本(被裁決引用 或 risk>=閾值)長期留存 ----------------
-- confirmed 裁決可能唯一證據就是擊鍵時間線/特徵(整段粘貼/空窗後突現整段程式碼),必須隨裁決一併歸檔。
CREATE TABLE IF NOT EXISTS archive_keystroke_samples (
  id            INTEGER PRIMARY KEY,                  -- 沿用 live keystroke_samples.id
  exam_id       TEXT NOT NULL,
  seat_id       TEXT NOT NULL,
  submission_id TEXT,
  ts            REAL NOT NULL,
  timeline      TEXT,                                 -- JSON: keydown 時間戳序列
  features      TEXT,                                 -- JSON: pasteCount/maxBurstCharsPerSec/idleThenBlock 等
  risk          INTEGER
);
CREATE INDEX IF NOT EXISTS ix_akeystroke_seat ON archive_keystroke_samples(exam_id, seat_id, ts);

-- 裁決結論(已 confirmed/dismissed)長期留存 --------------------
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
