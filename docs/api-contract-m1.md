---
type: spec
title: M1 介面契約 — Agent ↔ Server
---

# M1 介面契約 — Agent ↔ Server

- 專案：**Horus** · 里程碑：M1（最小閉環）
- 日期：2026-06-28
- 關聯：[architecture-v0.2.md](architecture-v0.2.md) · [../schema/schema.sql](../schema/schema.sql) · [../agent/](../agent/)

本契約定義採集端 Agent 與監考伺服器之間的兩條通道，以及寫入資料庫資料模型。欄位命名與 `Horus.Agent` 程式碼、`schema.sql` 嚴格一致。

## 0. 通用約定

- **傳輸基址**：`ws(s)://<server>:<port>` 與 `http(s)://<server>:<port>`，區域網路內同一臺/同一組伺服器。
- **編碼**：所有 JSON 為 UTF-8、**camelCase** 欄位名；枚舉值為 **snake_case** 字串。
- **時間**：`ts` = Agent 本機時鐘，Unix 秒（含小數，毫秒精度）；`recvTs` = 伺服器接收時鐘。考前 NTP 對齊。
- **序號 `seq`**：每個 Agent 維護單調遞增計數器，事件與圖片**共用**同一序號空間。伺服器按 `(agentId, seq)` 冪等去重，支援斷網續傳重發。
- **採集面鑑權 / 防篡改**：預共享金鑰（PSK，每場考試或每 Agent 一把）。
  - 事件：每條帶 `sig = HMAC-SHA256(PSK, hashSelf + "\n" + seq)`。
  - 圖片：HTTP 頭帶 `X-Horus-Sig = HMAC-SHA256(PSK, canonical(headers) + sha256(body))`；`canonical(headers)` 順序 = exam, seat, agent, seq, trigger, phash, ts, **imageId**（含 `X-Horus-Image-Id` 防其被篡改汙染證據關聯）。
  - 握手：WebSocket 連接時附 `X-Horus-Auth` 頭（見 §1.1）。
- **管理面鑑權**：所有 `/api/*`（看板讀 + 管理寫 + 圖片位元組，**除 `/api/login`、`/api/logout`**）需帶管理權杖,三選一憑證：**① HttpOnly cookie `horus_admin`**（M2 起,瀏覽器首選；`POST /api/login {token}` 檢核後下發,`SameSite=Strict`+`HttpOnly` → JS 讀不到、`<img>` 同源自動攜帶、不進 URL）；**② `X-Horus-Admin: <令牌>` 頭**（curl / 指令碼用戶端）；**③ `?t=<令牌>` 查詢**（向後相容,UI 已棄用）。未配權杖則放行（僅整合測試）。**防止學員機調 `/api/exams/{id}/config` 下發白名單關掉全場檢測、或拉取全班證據圖、或抹除自己的可疑裁決。** 權杖與採集面 PSK 相互獨立。回應統一附 CSP / `X-Content-Type-Options` / `X-Frame-Options` / `Referrer-Policy` 安全頭。

### 0.1 雜湊鏈 canonical 規則（兩端必須逐位元組一致）
`hashSelf = SHA256( hashPrev + "\n" + canonicalCore )`，其中 `canonicalCore` =
對下列欄位**按此固定順序**做 JSON 序列化（camelCase 鍵、snake_case 枚舉、無多餘空白、null 欄位省略）：
```
examId, seatId, agentId, machineId, ts, type, payload, risk, evidenceImageId, seq
```
首條 `hashPrev = "GENESIS"`。`sig`、`hashPrev`、`hashSelf` 本身**不參與** `canonicalCore`。

> 注：歸檔清理非關鍵事件後整鏈會斷（見 architecture §13.2），故複驗以"單條事件 ↔ 其 `hashSelf`/`sig`"為準。

## 1. 事件通道（WebSocket）

### 1.1 連接
```
GET  ws://<server>:<port>/ingest/events?examId=E1&seatId=A07&agentId=ag-A07
Header: X-Horus-Auth: <hex(HMAC-SHA256(PSK, examId+"|"+seatId+"|"+agentId))>
```
握手成功後，**Agent 首影格**傳送 `hello`，**伺服器**回 `hello_ack`（含伺服器已知的最大 `seq`，用於 Agent 決定從哪續傳）。

### 1.2 Agent → Server 影格

**事件影格**（一條信號一影格）：
```jsonc
{
  "v": 1,
  "type": "event",
  "event": {
    "examId": "E1", "seatId": "A07", "agentId": "ag-A07", "machineId": "PC-A07",
    "ts": 1750000000.123,
    "type": "browser_url",            // 見 §3 事件類型
    "payload": { "process": "chrome", "url": "https://chat.openai.com/", "whitelisted": false },
    "risk": 80,
    "evidenceImageId": "img_8f1c…",   // 若該事件觸發了抓圖；否則省略
    "hashPrev": "…", "hashSelf": "…", "seq": 1287
  },
  "seq": 1287,
  "sig": "…hmac…"
}
```
**hello 影格**：
```jsonc
{ "v":1, "type":"hello", "agentId":"ag-A07", "examId":"E1", "seatId":"A07",
  "machineId":"PC-A07", "agentVersion":"0.1.0", "ts":1750000000.0 }
```

### 1.3 Server → Agent 影格
| type | 含義 | 欄位 |
|---|---|---|
| `hello_ack` | 握手確認 | `maxSeq`（伺服器已收到的最大 seq，僅作安全網；Agent 的序號真相是本機持久化高水位） |
| `ack` | **逐條確認** | `seq`（伺服器已持久化的**該條** seq；Agent 精確刪除緩衝中此 seq）。**不用範圍 upto**——因事件/圖片共用序號空間天然有空洞，範圍壓實會誤刪從未送達的低 seq 證據 |
| `config_update` | 下發新設定 | `config`（白名單 / 閾值 / 截圖參數，熱更新） |
| `capture_now` | 請求立即抓圖 | `reason`（監考員手動點名抓圖）。監考端調 `POST /api/agents/{agentId}/capture` 觸發（見 §5 M3 增補） |
| `exam_ended` | **考試結束**(考試派發 2026-07-03) | `examId`。`POST /api/exams/{id}/end` 時推給該考試所有線上 Agent;Agent 離線錯過的,重連 `hello` 時按考試狀態**補發**。Agent 收到:留 5s 排空緩衝續傳,然後停採、棄工作階段回待命 |
| `session_revoked` | **全場遠程登出**(同上) | `examId`。`POST /api/exams/{id}/logout` 吊銷該考試全部 OIDC 工作階段後推送,隨後**強斷**連接(吊銷的工作階段不能續用);Agent 立即停採回待命,重連 401 須重新登入 |
| `ping` / `pong` | 保活 | `ts` |

> **config_update（熱更新）**：監考端調 `POST /api/exams/{examId}/config`（body = 下列 camelCase 設定物件），伺服器快取並推送 `config_update` 給該考試所有線上 Agent；新連 / 重連 Agent 在 `hello` 後也會補推一次。Agent 收到即原子應用（`LiveConfig`），**下一輪採集生效**。可熱更欄位（僅出現的才更新）：
> ```jsonc
> { "whitelistHosts":["judge.exam.cn"], "whitelistProcs":["code"],
>   "largePasteThreshold":200, "targetHeight":1080, "webpQuality":75,
>   "baselineMinSeconds":30, "baselineMaxSeconds":90 }
> ```

### 1.4 可靠性
- Agent **傳送前先持久化到本機緩衝**（至少一次投遞），收到 `ack.seq == 该条 seq` 才刪除該條。
- **序號單調不復用**：Agent 把序號高水位持久化到磁碟（`seq.state`，按塊預留），處理程序重新啟動後從高水位**之上**繼續，杜絕與緩衝中未確認事件撞號被伺服器冪等吞掉。
- 斷網：事件落本機緩衝；重連 → `hello` → 續傳所有未確認事件（**先補圖後發事件**，使證據圖關聯可命中）。
- 伺服器按 `(agentId, seq)` 唯一約束去重，重複影格靜默忽略。

## 2. 圖片通道（HTTP）

### 2.1 上傳截圖
```
POST http://<server>:<port>/ingest/images
Content-Type: image/webp
X-Horus-Exam:    E1
X-Horus-Seat:    A07
X-Horus-Agent:   ag-A07
X-Horus-Seq:     1288
X-Horus-Trigger: event:browser        // event:browser | event:paste | event:process | event:usb | event:manual | baseline_random
X-Horus-Phash:   9f3c1a22b0e4d7f1     // dHash 64bit, 16 hex
X-Horus-Ts:      1750000000.456
X-Horus-Sig:     <hex hmac>
X-Horus-Image-Id: img_8f1c…           // 可选:客户端预生成 id(触发型抓图),服务器沿用
<body = WebP 字节>
```
**回應 200**：
```jsonc
{ "stored": true, "imageId": "img_8f1c…", "duplicate": false, "ocrQueued": true }
```
- `imageId`：預設伺服器分配（uuid）。**若請求帶合法 `X-Horus-Image-Id`（`img_` + ≤64 位字母數字），伺服器沿用該 id**——用於**觸發型抓圖**：Agent 預生成 id，既寫進隨後那條事件的 `evidenceImageId`，又作為圖片 id 上傳；即使離線緩衝、斷線重連後補傳，事件與證據圖仍以同一 id 關聯不斷。帶用戶端 id 的上傳**跳過 pHash 去重**（尊重事件關聯）；同 id 重傳冪等（`duplicate:true`，不另存）。
- `duplicate: true`：pHash 命中近重複（無用戶端 id 時），或用戶端 id 已存在（續傳重發）；未另存原圖，回傳已存 imageId。
- 未帶 `X-Horus-Image-Id` 的（如隨機基線抓圖）走伺服器分配 + pHash 去重。
- 原圖按 `images/<examId>/<seatId>/<imageId>.webp` 存檔案系統；DB 只存指針（見 §4 `images`）。

### 2.2 擊鍵節奏上報（判題網頁 → 伺服器，旁路）
```
POST http://<server>:<port>/ingest/keystroke
Content-Type: application/json
X-Horus-KSig: <hex hmac>              // M2:会话鉴权(配了 KSK 时必带),见下
{ "examId":"E1", "seatId":"A07", "submissionId":"sub_42", "ts":1750000000.0,
  "timeline":[12,98,210,…],            // keydown 相对毫秒(可降采样)
  "features":{ "pasteCount":1, "maxBurstCharsPerSec":140, "idleThenBlock":true } }
```
> 該通道來自判題網頁、不經 Agent；伺服器據 `features` 給 `keystroke_samples.risk` 打分。
>
> **工作階段鑑權（M2）**：瀏覽器 JS 無法安全持金鑰,故由**判題後端**(可安全持 KSK)對整條提交體簽名：
> `X-Horus-KSig = HMAC-SHA256(KSK, "keystroke\n" + sha256(body))`。任何篡改(含改 `seatId` 栽贓他人)
> 都會破壞簽名 → 401。`"keystroke\n"` 域分隔前綴防跨通道簽名重用。KSK 與採集 PSK / 管理權杖相互獨立；
> 伺服器未配 KSK 時該通道放行(僅整合測試)。**防重放**：按 `(examId, seatId, ts, submissionId)` **冪等寫入資料庫**——
> LAN 明文被嗅探後原樣重放合法簽名體不重複入隊(回應 `{ "stored":false, "duplicate":true }`)。

## 3. 事件類型與 payload

| `type` | 觸發 | payload 關鍵欄位 | 典型 risk |
|---|---|---|---|
| `window_focus` | 前台視窗變化 | `title`, `process`, `hwnd` | 0 |
| `browser_url` | 瀏覽器位址變化 | `process`, `url`, `whitelisted`；或 `url:null,note:"url_unreadable"` | 白名單 0 / 非白名單 80 / 讀不到 40 |
| `process_start` | 處理程序啟動 | `name`, `pid`, `cmd`, `whitelisted` | 白名單 0 / 非白名單 70 |
| `process_exit` | 處理程序退出 | `name`, `pid` | 0 |
| `clipboard` | 剪貼簿更新 | `len`, `lines`, `large`（**不含明文**） | 大段 60 |
| `alt_tab_burst` | 切窗爆發 | `count`, `windowSec` | 40（M2） |
| `usb` | 可行動裝置到達 | `drive` | 50 |
| `screenshot` | 顯式截圖事件（可選） | `trigger`, `imageId` | — |
| `heartbeat` | 30s 心跳 | `status` | 0 |

> `risk` 由 Agent 本機初判。**伺服器不採信該自報值作最終判據**（M2）：伺服器憑獨立黑名單（AI 站 / 搜尋引擎 / 遠控工具）+ 該考試**已下發**白名單重算 `server_risk`，以**有效風險 = max(agentRisk, serverRisk)** 決定是否入 `suspicious_queue`；`serverRisk ≥ 阈值` 而 `agentRisk < 阈值` 時,佇列條目 `note` 記 `agent_risk_understated`（篡改逃逸取證）。L2/L3 後續再疊加。閉合 architecture §10.1 頭號殘留（持 PSK 學員機把「訪問 AI 站」簽成 `risk=0` 逃逸）。

## 4. 資料模型（live DB，權威 DDL = schema.sql）

核心表（欄位詳見 [../schema/schema.sql](../schema/schema.sql)）：

| 表 | 作用 | 關鍵列 |
|---|---|---|
| `exams` | 考試 | `exam_id, status, started_at, ended_at` |
| `seats` | 座位↔學員↔機器↔Agent | `(exam_id, seat_id), student_id, agent_id` |
| `events` | 事件流 | `(agent_id, seq) UNIQUE`, `type, payload(JSON), risk`(Agent 自報,留證)`, server_risk`(伺服器復判)`, evidence_image_id, hash_self, sig` |
| `images` | 截圖指針 | `image_id, trigger, phash, file_path, uploaded_to_ocr, is_evidence` |
| `ocr_results` | 視覺 LLM 識圖結果（L2·**視覺 LLM 已取代 OCR**·表名 `ocr_results` 沿用） | `image_id, text, hits, confidence` |
| `logo_hits` | Logo 匹配（L3·已併入 L2 視覺級·表名沿用） | `image_id, label, score, bbox` |
| `image_embeddings` | CLIP 向量（**普通 BLOB 表 + C# 暴力餘弦檢索·未用 sqlite-vec**；`vec_images` 虛表已刪，死程式碼移除） | `image_id, embedding BLOB` |
| `keystroke_samples` | 擊鍵節奏 | `seat_id, timeline, features, risk` |
| `suspicious_queue` | 可疑佇列（人工裁決） | `kind, score, status, refs, reviewer, decided_at` |
| `agent_heartbeats` | 線上狀態 | `agent_id, ts, status` |

### 4.1 關鍵關係
- `events.evidence_image_id → images.image_id`（觸發型抓圖）。
- `images.image_id → ocr_results / logo_hits / image_embeddings`（一圖多分析）。
- `suspicious_queue.refs` = JSON 陣列，引用 `events.id` / `images.image_id`，是歸檔"關鍵資料"的判據之一。

## 5. M1 伺服器最小職責清單
1. 起 WS `/ingest/events` 與 HTTP `/ingest/images`、`/ingest/keystroke`，檢核 `X-Horus-Auth` / `sig`。
2. 寫入資料庫到 `events` / `images`（含 `(agent_id, seq)` 冪等去重），原圖存盤。
3. L1 已在 Agent 完成；伺服器對 `risk ≥ 阈值` 的事件寫 `suspicious_queue`。
4. 簡易看板：座位線上（心跳）+ 可疑佇列列表 + 點開看證據圖與時間線。
5. （M2 起）將需要視覺判定的圖按 §5/architecture 收口送**視覺 LLM 識圖**（已取代 OCR），回填 `ocr_results`（表名沿用）。

> L2 視覺 LLM 識圖（取代 OCR）、L3 Logo（併入 L2）、CLIP 按圖搜圖（已完成實作·C# 暴力餘弦·未用 sqlite-vec）、歸檔作業屬 M2/M3，本契約預留欄位，M1 不實作。
>
> **M3 增補(管理面,已實作)**：
> - `GET /api/exams/{examId}/integrity` — 雜湊鏈完整性離線稽核。回傳 `{ ok, totalEvents, totalHashOk, totalChainOk, totalUnverifiable, totalRestartBoundaries, sigVerified, note?, agents:[{agentId,seatId,total,hashOk,chainOk,unverifiable,restartBoundaries,hashMismatches[],continuityBreaks[]}] }`；已歸檔/歸檔中考試回傳 `{ status:"archived"|"archiving", applicable:false, note }`（歸檔複核走下方 `/api/archive/exams/{id}`），不存在回傳 404。**`sigVerified`**(第三輪 D6):false = 伺服器未配 PSK(整合測試)、`ok` 僅表錨點自洽+鏈連續而**未做 sig 檢核**,`note` 會點明,避免整合測試"綠"被誤讀為取證清白。**誠實邊界**:雜湊鏈**不能檢出尾部截斷**(刪末條事件不產生 hash/鏈不符),靠人工/其它錨備援(見 architecture §10.1)。`hashMismatches` 含兩類:hash_self 與 payload 不符、或(配了 PSK 時)**sig 與 PSK 不符**(非 PSK 方改 payload 重算 hashSelf 使其自洽,唯 sig 識破)。`restartBoundaries` = Agent 重新啟動後新起鏈段(hash_prev=GENESIS 且 seq 續增),**非篡改**。`unverifiable` = 遷移前缺 `machine_id` 的舊事件(不可復算,**非篡改**)。
> - `GET /api/images/{imageId}` / `/meta` — live 未命中時**自動回落 archive 庫 + 冷存**(第三輪 F3:歸檔後證據圖仍可取證讀取;meta 帶 `archived:true`)。
> - `GET /api/archive/exams/{examId}` — **歸檔考試唯讀複核**(第三輪 F3)。回傳 `{ examId, archived:true, exam:{name,startedAt,endedAt,archivedAt,summary}, adjudications:[…], events:[…关键事件…], images:[…证据图…] }`;無歸檔庫/無此考試 → 404。
> - `POST /api/agents/{agentId}/capture` — **監考員點名抓圖**(第三輪 D2:使 `capture_now` 影格成真)。body 可選 `{reason}`,向該線上 Agent 推 `capture_now`,回傳 `{ ok, agentId, pushed }`(agent 不線上 → pushed:false)。
> - **考試派發批(2026-07-03)**:
>   - `GET /oidc/active-exam` — **公開**(Agent 待命輪詢,無憑證):`{ active:false }` 或 `{ active:true, examId, name }`。派發判據 = status='active' 中最近建立的一場。
>   - `GET /oidc/session` — **工作階段探針**(頭 `X-Horus-Session`,sessionId 即憑證):`{ valid:false }` 或 `{ valid:true, examId, seatId, examStatus }`。Agent 每 60s 備援探測「被遠程登出/考試結束」。
>   - `POST /api/exams/{examId}/end` — 回應增加 `notified`(收到 `exam_ended` 推送的線上連接數)。
>   - `POST /api/exams/{examId}/logout` — **全場遠程登出**(admin 門內):吊銷該考試全部 OIDC 採集工作階段 + 推 `session_revoked` + 強斷連接,回傳 `{ ok, examId, revoked, notified }`。
>   - `POST /oidc/exchange` — 請求體**不再收 examId/seatId**(收 `{code, codeVerifier, redirectUri, nonce, agentEcdhPub, agentId, machineId}`);回應增加 `examId`(伺服器端派發)與 `seatId`(= OIDC username,不安全/空退回 sub)。無活躍考試 → 401 `no_active_exam`(先於 token 交換判定,不白耗一次性授權碼)。
> - `POST /api/archive/run` — 手動觸發歸檔作業(後台亦每 `archiveScanIntervalHours` 自動跑)。回傳 `{ now, scanned, archived, skipped, exams:[{examId,outcome,…}] }`。`outcome` ∈ `archived|skipped(pending 未裁决)|error`。
> - 事件雜湊複驗(M3)：ingest 側從原始 payload 復算 `hashSelf`，與自報不一致 → `{ type:"error", code:"bad_hash", seq }` 拒收(見 §0.1)。`events` 表 M3 增 `machine_id` 列以支援離線複驗。
