-- ============================================================
-- Horus 監考系統 · SQLite **live** DB DDL (M1)
-- 熱庫:當前 + 近 30 天。30 天后關鍵資料轉 archive(見 schema-archive.sql),其餘清理。
-- 欄位命名與 api-contract-m1.md / Horus.Agent 對齊(庫內用 snake_case)。
-- ============================================================
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- 考試 --------------------------------------------------------
CREATE TABLE IF NOT EXISTS exams (
  exam_id      TEXT PRIMARY KEY,
  name         TEXT NOT NULL,
  started_at   REAL,                                  -- Unix 秒
  ended_at     REAL,
  status       TEXT NOT NULL DEFAULT 'active',        -- active|ended|archiving|archived（archiving=歸檔進行中,ingest 短路）
  created_at   REAL NOT NULL
);

-- 座位 / 學員 / 機器 / Agent 綁定 ------------------------------
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

-- 事件流(中繼資料信號) -----------------------------------------
CREATE TABLE IF NOT EXISTS events (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id           TEXT NOT NULL,
  seat_id           TEXT NOT NULL,
  agent_id          TEXT NOT NULL,
  machine_id        TEXT,                             -- 機器標識;canonicalCore 含 machineId,須寫入資料庫以支援 M3 鏈複驗
  seq               INTEGER NOT NULL,                 -- agent 單調序號
  ts                REAL NOT NULL,                    -- agent 本機時鐘(Unix 秒)
  recv_ts           REAL NOT NULL,                    -- 伺服器接收時鐘
  type              TEXT NOT NULL,                    -- window_focus|browser_url|process_start|...
  payload           TEXT NOT NULL,                    -- JSON
  risk              INTEGER NOT NULL DEFAULT 0,        -- **Agent 自報**初判(原樣留證,不改)
  server_risk       INTEGER,                          -- **伺服器獨立復判**(不信任 Agent risk);入隊/看板用 max(risk,server_risk)
  evidence_image_id TEXT,                             -- → images.image_id
  hash_prev         TEXT,
  hash_self         TEXT,
  sig               TEXT,
  UNIQUE (agent_id, seq)                              -- 冪等去重 / 斷網續傳(與契約 §1.4 一致，seq 每事件唯一)
);
CREATE INDEX IF NOT EXISTS ix_events_seat_ts ON events(exam_id, seat_id, ts, risk);  -- 含 risk:看板 MAX(risk) 免回表
CREATE INDEX IF NOT EXISTS ix_events_risk    ON events(exam_id, risk);
CREATE INDEX IF NOT EXISTS ix_events_type    ON events(exam_id, type);
-- 圖片入庫反向補標 is_evidence 時按 evidence_image_id 查(部分索引,僅索引有引用的行)
CREATE INDEX IF NOT EXISTS ix_events_evidence ON events(evidence_image_id) WHERE evidence_image_id IS NOT NULL;

-- 截圖中繼資料(原圖存檔案系統,這裡只存指針) --------------------
CREATE TABLE IF NOT EXISTS images (
  image_id        TEXT PRIMARY KEY,                   -- 伺服器分配(uuid)
  exam_id         TEXT NOT NULL,
  seat_id         TEXT NOT NULL,
  agent_id        TEXT NOT NULL,
  ts              REAL NOT NULL,
  recv_ts         REAL NOT NULL,
  trigger         TEXT NOT NULL,                      -- event:browser|event:paste|baseline_random|...
  phash           TEXT NOT NULL,                      -- 16 hex (dHash 64bit)
  file_path       TEXT NOT NULL,                      -- 區域網路內相對路徑 images/<exam>/<seat>/<id>.webp
  width           INTEGER,
  height          INTEGER,
  format          TEXT NOT NULL DEFAULT 'webp',
  bytes           INTEGER,
  -- 隱私稽核:圖**位元組是否真出區域網路**送雲視覺。僅當 SendsOffNetwork 分析器**成功送出**才置 1;
  -- 本機/mock(不出網)或從未成功送出的圖恆 0。**不再兼作處理認領閂鎖**(閂鎖改用 analysis_state,閉合第三輪 F2 語義衝突)。
  uploaded_to_ocr INTEGER NOT NULL DEFAULT 0,
  -- 視覺分析狀態(處理閂鎖):0=待分析 1=已終結(成功寫入資料庫 / 派生失敗 / 檔案缺失等確定態,不再重掃)。
  -- 與 uploaded_to_ocr 解耦:臨時雲失敗**不置 1** → 保持 0 由補償重掃拾回(閉合第三輪 F1 臨時失敗永久漏析)。
  analysis_state  INTEGER NOT NULL DEFAULT 0,
  -- 已認領分析的次數(含失敗):補償重掃按 attempts < 上限 重試臨時失敗,超限則放棄防死循環。
  analysis_attempts INTEGER NOT NULL DEFAULT 0,
  is_evidence     INTEGER NOT NULL DEFAULT 0          -- 是否被某可疑項引用(歸檔保留判據)
);
CREATE INDEX IF NOT EXISTS ix_images_analysis ON images(exam_id, analysis_state) WHERE analysis_state=0;
CREATE INDEX IF NOT EXISTS ix_images_seat_ts ON images(exam_id, seat_id, ts);
CREATE INDEX IF NOT EXISTS ix_images_phash   ON images(exam_id, seat_id, phash);

-- 雲 OCR 結果 (L2) -------------------------------------------
CREATE TABLE IF NOT EXISTS ocr_results (
  image_id    TEXT PRIMARY KEY REFERENCES images(image_id),
  engine      TEXT NOT NULL,                          -- 供應商標識
  text        TEXT,                                   -- 識別全文
  hits        TEXT,                                   -- JSON: 命中關鍵詞列表
  confidence  REAL,
  created_at  REAL NOT NULL
);

-- Logo / 範本匹配 (L3) ---------------------------------------
CREATE TABLE IF NOT EXISTS logo_hits (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  image_id    TEXT NOT NULL REFERENCES images(image_id),
  label       TEXT NOT NULL,                          -- chatgpt|google|deepseek|...
  score       REAL,
  bbox        TEXT,                                   -- JSON [x,y,w,h]
  created_at  REAL NOT NULL
);

-- 圖像向量普通表(M3 實做·按圖搜圖)——規模小(單場幾千圖),C# 暴力餘弦 KNN,不相依 sqlite-vec 原生擴充。
-- embedding = float32[dim] 小端位元組。僅嵌證據/可疑圖(省算力)。
CREATE TABLE IF NOT EXISTS image_embeddings (
  image_id    TEXT PRIMARY KEY,
  dim         INTEGER NOT NULL,
  embedding   BLOB NOT NULL,
  embedded_at REAL NOT NULL
);

-- 擊鍵節奏(外部判題後端經 KSK 旁路 POST /ingest/keystroke 寫入；前端埋點已撤) -----------------------------
CREATE TABLE IF NOT EXISTS keystroke_samples (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id       TEXT NOT NULL,
  seat_id       TEXT NOT NULL,
  submission_id TEXT,
  ts            REAL NOT NULL,
  timeline      TEXT,                                 -- JSON: keydown 時間戳序列(可降取樣)
  features      TEXT,                                 -- JSON: pasteCount/maxBurstCharsPerSec/idleThenBlock 等
  risk          INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_keystroke_seat ON keystroke_samples(exam_id, seat_id, ts);

-- 可疑事件佇列(系統初篩產出 → 人工裁決) ----------------------
CREATE TABLE IF NOT EXISTS suspicious_queue (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  exam_id     TEXT NOT NULL,
  seat_id     TEXT NOT NULL,
  ts          REAL NOT NULL,
  kind        TEXT NOT NULL,                          -- web_ai|search|non_whitelist_proc|large_paste|usb|ide_plugin_suspect|...
  score       INTEGER NOT NULL,
  status      TEXT NOT NULL DEFAULT 'pending',        -- pending|reviewing|confirmed|dismissed
  refs        TEXT,                                   -- JSON: 關聯 events.id / images.image_id
  reviewer    TEXT,
  decided_at  REAL,
  note        TEXT,
  source      TEXT NOT NULL DEFAULT 'suspicion'  -- suspicion(作弊線索·可裁決) | health(採集端健康告警·僅提示·不可裁決)
);
CREATE INDEX IF NOT EXISTS ix_susp_status ON suspicious_queue(exam_id, status, score);

-- 每考試已下發設定(白名單/閾值/截圖參數)持久化 -------------
-- 伺服器重新啟動後回填記憶體快取,使 server_risk 白名單復判不退化、Agent 重連 hello 時能補推(見 architecture §10.2)。
CREATE TABLE IF NOT EXISTS exam_config (
  exam_id    TEXT PRIMARY KEY,
  config     TEXT NOT NULL,                          -- 下發的 camelCase 設定 JSON 原文
  updated_at REAL NOT NULL
);

-- M4 身分層:OIDC 採集工作階段(取代共享 PSK) --------------------
-- Agent 經 wentian OIDC 登入後,伺服器派發一條工作階段:綁定 wentian 身分(sub + 富畫像)到 (exam,seat,agent),
-- 派生的 k_sess(ECDH·32B base64)作採集簽名金鑰。事件體身分須 == 本工作階段綁定值,閉合跨身分栽贓/seq 搶佔。
-- 持久化:伺服器重新啟動後工作階段不丟(考試中途不必強制學員重登)。k_sess 存於可信伺服器 DB(同 PSK 的信任面)。
CREATE TABLE IF NOT EXISTS oidc_sessions (
  session_id   TEXT PRIMARY KEY,
  exam_id      TEXT NOT NULL,
  seat_id      TEXT NOT NULL,
  agent_id     TEXT NOT NULL,
  machine_id   TEXT,
  sub          TEXT NOT NULL,                          -- 貝塔通穩定身分(UUID)·永不變更
  name         TEXT,                                    -- 真實姓名(標準 profile scope 的 name)
  username     TEXT,                                    -- 使用者名(標準 profile 的 preferred_username)·**座位標識的來源**
  -- ★ 此前還有 user_type/nickname/dao_name/avatar/realm/realm_level/combat_power 七列,
  --   全部來自 wentian 自訂 scope `horus_profile`。貝塔通 P81 停發,已移除。
  --   既有 dev 庫裡那幾列會留著(SQLite 不走 DROP COLUMN),無人讀、無害。
  k_sess       TEXT NOT NULL,                          -- base64(32B) ECDH 派生工作階段金鑰(HMAC 簽名金鑰)
  issued_at    REAL NOT NULL,
  -- ★ 三道門(貝塔通 P88–P92):expires_at = absolute(任何活動都推不動它),
  --   另兩道靠下面兩列。判定順序 revoked → absolute → idle → heartbeat,見 SessionGates。
  expires_at   REAL NOT NULL,
  last_heartbeat_at REAL NOT NULL,                     -- 心跳門:最後一次收到心跳的時刻(任何心跳都續)
  last_seen_at      REAL NOT NULL                      -- idle 門:最後一次「人還在」·★ 只由 active:true 的心跳續
);
CREATE INDEX IF NOT EXISTS ix_oidc_sessions_agent ON oidc_sessions(exam_id, agent_id);

-- 監考員看板管理工作階段(貝塔通 dashboard OIDC 登入·取代靜態 adminToken) ----
-- ★★ 存取資格判據在**身分中心**:看板用戶端歸屬平台 `horus-admin`,沒有該平台權限的人
--    在貝塔通的授權階段就被拒、換不到 code(貝塔通 P83)。所以能走到建工作階段這一步的**就是監考員**,
--    本表不再存任何角色欄位。admin gate(AdminAuthMode=oidc)只檢核「此工作階段在且未過期」。
CREATE TABLE IF NOT EXISTS admin_sessions (
  session_id   TEXT PRIMARY KEY,
  sub          TEXT NOT NULL,                          -- 貝塔通穩定身分(UUID)·永不變更
  name         TEXT,                                   -- 真實姓名(標準 profile scope 的 name)
  issued_at    REAL NOT NULL,
  -- 三道門同 oidc_sessions。★ 看板是**持續自動輪詢**的,所以「任何業務請求都不得續 idle」
  --   這條對它尤其致命 —— 輪詢算作活動的話,監考機上開著看板就等於永不登出。
  expires_at   REAL NOT NULL,
  last_heartbeat_at REAL NOT NULL,
  last_seen_at      REAL NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_admin_sessions_sub ON admin_sessions(sub);

-- 工作階段「為什麼沒了」的留痕(供 401 時給使用者一句真話) ------------------
-- ★★ 撤權的處置是**刪行** —— 刪掉才不會被任何一條忘了加過濾條件的裸 SQL 意外復活
--    (本儲存庫反覆吃過這個虧,見 SchemaDriftTests 的類註解)。代價是「為什麼沒了」隨之消失,
--    於是被踢的人只能看到一句籠統的「登入已失效」。
-- ★ 這張表就補這一口:**只存一句給使用者看的原因,不含任何憑證**,所以它復活不了任何東西。
-- ★ 它尤其重要的場合是「平台權限被關掉」:取消 SSO(貝塔通 P84/P98)之後,
--    每點一次登入都要**完整輸一遍密碼**才被拒 —— 不告訴他真實原因,他會一遍遍白輸。
CREATE TABLE IF NOT EXISTS revoked_session_notices (
  session_id   TEXT PRIMARY KEY,                      -- 已被刪掉的那條工作階段的 id(cookie/憑證裡還留著)
  reason       TEXT NOT NULL,                         -- 貝塔通的 reason,或本機口徑(exam_logout 等)
  revoked_at   REAL NOT NULL
);

-- 貝塔通撤權通知的冪等臺賬(其 P44/P69·rp-contract「/internal/revoke」) ----
-- 貝塔通判成功的口徑是 **2xx**,超時只有 5 秒 —— 處理成功但花了 6 秒的一發會被判失敗並重投,
-- 於是同一個 `jti` 必然會來第二次。「反正只會來一次」是錯的,冪等不是可選項。
-- ★ 重投時 `jti` **不變**,所以按它做主鍵即可;記下處置結果好讓重投原樣回同一個答案。
CREATE TABLE IF NOT EXISTS betapass_revocations (
  jti           TEXT PRIMARY KEY,                      -- 冪等鍵(貝塔通佇列行 id)·重投不變
  sub           TEXT NOT NULL,                         -- 被撤權的帳號(貝塔通穩定身分)
  client_id     TEXT NOT NULL,                         -- 權杖的 aud:區分撤的是採集面還是監考臺
  reason        TEXT,                                  -- 只用於留痕與提示語,**不參與是否清工作階段的判斷**
  received_at   REAL NOT NULL,
  revoked_count INTEGER NOT NULL                       -- 這一發實際清掉幾條本機工作階段
);
CREATE INDEX IF NOT EXISTS ix_betapass_revocations_sub ON betapass_revocations(sub);

-- Agent 心跳 / 線上狀態 --------------------------------------
CREATE TABLE IF NOT EXISTS agent_heartbeats (
  agent_id   TEXT NOT NULL,
  exam_id    TEXT NOT NULL,
  seat_id    TEXT NOT NULL,
  ts         REAL NOT NULL,
  status     TEXT NOT NULL,                           -- alive|degraded|...
  -- PK 含 exam/seat:同一 agent_id 換座重複使用 + 同毫秒 ts 時,不同 seat 的心跳不再撞同一行、seat 歸屬不被覆蓋汙染線上判定
  PRIMARY KEY (exam_id, seat_id, agent_id, ts)
);
-- 看板線上判定按 (exam, ts>=cut) 查,否則全表掃心跳錶
CREATE INDEX IF NOT EXISTS ix_hb_exam_ts ON agent_heartbeats(exam_id, ts);
