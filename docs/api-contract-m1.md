# M1 接口契约 — Agent ↔ Server

- 项目：**Horus** · 里程碑：M1（最小闭环）
- 日期：2026-06-28
- 关联：[architecture-v0.2.md](architecture-v0.2.md) · [../schema/schema.sql](../schema/schema.sql) · [../agent/](../agent/)

本契约定义采集端 Agent 与监考服务器之间的两条通道，以及落库数据模型。字段命名与 `Horus.Agent` 代码、`schema.sql` 严格一致。

## 0. 通用约定

- **传输基址**：`ws(s)://<server>:<port>` 与 `http(s)://<server>:<port>`，局域网内同一台/同一组服务器。
- **编码**：所有 JSON 为 UTF-8、**camelCase** 字段名；枚举值为 **snake_case** 字符串。
- **时间**：`ts` = Agent 本机时钟，Unix 秒（含小数，毫秒精度）；`recvTs` = 服务器接收时钟。考前 NTP 对齐。
- **序号 `seq`**：每个 Agent 维护单调递增计数器，事件与图片**共用**同一序号空间。服务器按 `(agentId, seq)` 幂等去重，支持断网续传重发。
- **采集面鉴权 / 防篡改**：预共享密钥（PSK，每场考试或每 Agent 一把）。
  - 事件：每条带 `sig = HMAC-SHA256(PSK, hashSelf + "\n" + seq)`。
  - 图片：HTTP 头带 `X-Horus-Sig = HMAC-SHA256(PSK, canonical(headers) + sha256(body))`；`canonical(headers)` 顺序 = exam, seat, agent, seq, trigger, phash, ts, **imageId**（含 `X-Horus-Image-Id` 防其被篡改污染证据关联）。
  - 握手：WebSocket 连接时附 `X-Horus-Auth` 头（见 §1.1）。
- **管理面鉴权**：所有 `/api/*`（看板读 + 管理写 + 图片字节，**除 `/api/login`、`/api/logout`**）需带管理令牌,三选一凭证：**① HttpOnly cookie `horus_admin`**（M2 起,浏览器首选；`POST /api/login {token}` 校验后下发,`SameSite=Strict`+`HttpOnly` → JS 读不到、`<img>` 同源自动携带、不进 URL）；**② `X-Horus-Admin: <令牌>` 头**（curl / 脚本客户端）；**③ `?t=<令牌>` 查询**（向后兼容,UI 已弃用）。未配令牌则放行（仅联调）。**防止学员机调 `/api/exams/{id}/config` 下发白名单关掉全场检测、或拉取全班证据图、或抹除自己的可疑裁决。** 令牌与采集面 PSK 相互独立。响应统一附 CSP / `X-Content-Type-Options` / `X-Frame-Options` / `Referrer-Policy` 安全头。

### 0.1 哈希链 canonical 规则（两端必须逐字节一致）
`hashSelf = SHA256( hashPrev + "\n" + canonicalCore )`，其中 `canonicalCore` =
对下列字段**按此固定顺序**做 JSON 序列化（camelCase 键、snake_case 枚举、无多余空白、null 字段省略）：
```
examId, seatId, agentId, machineId, ts, type, payload, risk, evidenceImageId, seq
```
首条 `hashPrev = "GENESIS"`。`sig`、`hashPrev`、`hashSelf` 本身**不参与** `canonicalCore`。

> 注：归档清理非关键事件后整链会断（见 architecture §13.2），故复验以"单条事件 ↔ 其 `hashSelf`/`sig`"为准。

## 1. 事件通道（WebSocket）

### 1.1 连接
```
GET  ws://<server>:<port>/ingest/events?examId=E1&seatId=A07&agentId=ag-A07
Header: X-Horus-Auth: <hex(HMAC-SHA256(PSK, examId+"|"+seatId+"|"+agentId))>
```
握手成功后，**Agent 首帧**发送 `hello`，**服务器**回 `hello_ack`（含服务器已知的最大 `seq`，用于 Agent 决定从哪续传）。

### 1.2 Agent → Server 帧

**事件帧**（一条信号一帧）：
```jsonc
{
  "v": 1,
  "type": "event",
  "event": {
    "examId": "E1", "seatId": "A07", "agentId": "ag-A07", "machineId": "PC-A07",
    "ts": 1750000000.123,
    "type": "browser_url",            // 见 §3 事件类型
    "payload": { "process": "chrome", "url": "https://chat.openai.com/", "whitelisted": false },
    "risk": 80,
    "evidenceImageId": "img_8f1c…",   // 若该事件触发了抓图；否则省略
    "hashPrev": "…", "hashSelf": "…", "seq": 1287
  },
  "seq": 1287,
  "sig": "…hmac…"
}
```
**hello 帧**：
```jsonc
{ "v":1, "type":"hello", "agentId":"ag-A07", "examId":"E1", "seatId":"A07",
  "machineId":"PC-A07", "agentVersion":"0.1.0", "ts":1750000000.0 }
```

### 1.3 Server → Agent 帧
| type | 含义 | 字段 |
|---|---|---|
| `hello_ack` | 握手确认 | `maxSeq`（服务器已收到的最大 seq，仅作安全网；Agent 的序号真相是本地持久化高水位） |
| `ack` | **逐条确认** | `seq`（服务器已持久化的**该条** seq；Agent 精确删除缓冲中此 seq）。**不用范围 upto**——因事件/图片共用序号空间天然有空洞，范围压实会误删从未送达的低 seq 证据 |
| `config_update` | 下发新配置 | `config`（白名单 / 阈值 / 截图参数，热更新） |
| `capture_now` | 请求立即抓图 | `reason`（监考员手动点名抓图）。监考端调 `POST /api/agents/{agentId}/capture` 触发（见 §5 M3 增补） |
| `ping` / `pong` | 保活 | `ts` |

> **config_update（热更新）**：监考端调 `POST /api/exams/{examId}/config`（body = 下列 camelCase 配置对象），服务器缓存并推送 `config_update` 给该考试所有在线 Agent；新连 / 重连 Agent 在 `hello` 后也会补推一次。Agent 收到即原子应用（`LiveConfig`），**下一轮采集生效**。可热更字段（仅出现的才更新）：
> ```jsonc
> { "whitelistHosts":["judge.exam.cn"], "whitelistProcs":["code"],
>   "largePasteThreshold":200, "targetHeight":1080, "webpQuality":75,
>   "baselineMinSeconds":30, "baselineMaxSeconds":90 }
> ```

### 1.4 可靠性
- Agent **发送前先持久化到本地缓冲**（至少一次投递），收到 `ack.seq == 该条 seq` 才删除该条。
- **序号单调不复用**：Agent 把序号高水位持久化到磁盘（`seq.state`，按块预留），进程重启后从高水位**之上**继续，杜绝与缓冲中未确认事件撞号被服务器幂等吞掉。
- 断网：事件落本地缓冲；重连 → `hello` → 续传所有未确认事件（**先补图后发事件**，使证据图关联可命中）。
- 服务器按 `(agentId, seq)` 唯一约束去重，重复帧静默忽略。

## 2. 图片通道（HTTP）

### 2.1 上传截图
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
**响应 200**：
```jsonc
{ "stored": true, "imageId": "img_8f1c…", "duplicate": false, "ocrQueued": true }
```
- `imageId`：默认服务器分配（uuid）。**若请求带合法 `X-Horus-Image-Id`（`img_` + ≤64 位字母数字），服务器沿用该 id**——用于**触发型抓图**：Agent 预生成 id，既写进随后那条事件的 `evidenceImageId`，又作为图片 id 上传；即使离线缓冲、断线重连后补传，事件与证据图仍以同一 id 关联不断。带客户端 id 的上传**跳过 pHash 去重**（尊重事件关联）；同 id 重传幂等（`duplicate:true`，不另存）。
- `duplicate: true`：pHash 命中近重复（无客户端 id 时），或客户端 id 已存在（续传重发）；未另存原图，返回已存 imageId。
- 未带 `X-Horus-Image-Id` 的（如随机基线抓图）走服务器分配 + pHash 去重。
- 原图按 `images/<examId>/<seatId>/<imageId>.webp` 存文件系统；DB 只存指针（见 §4 `images`）。

### 2.2 击键节奏上报（判题网页 → 服务器，旁路）
```
POST http://<server>:<port>/ingest/keystroke
Content-Type: application/json
X-Horus-KSig: <hex hmac>              // M2:会话鉴权(配了 KSK 时必带),见下
{ "examId":"E1", "seatId":"A07", "submissionId":"sub_42", "ts":1750000000.0,
  "timeline":[12,98,210,…],            // keydown 相对毫秒(可降采样)
  "features":{ "pasteCount":1, "maxBurstCharsPerSec":140, "idleThenBlock":true } }
```
> 该通道来自判题网页、不经 Agent；服务器据 `features` 给 `keystroke_samples.risk` 打分。
>
> **会话鉴权（M2）**：浏览器 JS 无法安全持密钥,故由**判题后端**(可安全持 KSK)对整条提交体签名：
> `X-Horus-KSig = HMAC-SHA256(KSK, "keystroke\n" + sha256(body))`。任何篡改(含改 `seatId` 栽赃他人)
> 都会破坏签名 → 401。`"keystroke\n"` 域分隔前缀防跨通道签名重用。KSK 与采集 PSK / 管理令牌相互独立；
> 服务器未配 KSK 时该通道放行(仅联调)。**防重放**：按 `(examId, seatId, ts, submissionId)` **幂等落库**——
> LAN 明文被嗅探后原样重放合法签名体不重复入队(响应 `{ "stored":false, "duplicate":true }`)。

## 3. 事件类型与 payload

| `type` | 触发 | payload 关键字段 | 典型 risk |
|---|---|---|---|
| `window_focus` | 前台窗口变化 | `title`, `process`, `hwnd` | 0 |
| `browser_url` | 浏览器地址变化 | `process`, `url`, `whitelisted`；或 `url:null,note:"url_unreadable"` | 白名单 0 / 非白名单 80 / 读不到 40 |
| `process_start` | 进程启动 | `name`, `pid`, `cmd`, `whitelisted` | 白名单 0 / 非白名单 70 |
| `process_exit` | 进程退出 | `name`, `pid` | 0 |
| `clipboard` | 剪贴板更新 | `len`, `lines`, `large`（**不含明文**） | 大段 60 |
| `alt_tab_burst` | 切窗爆发 | `count`, `windowSec` | 40（M2） |
| `usb` | 可移动设备到达 | `drive` | 50 |
| `screenshot` | 显式截图事件（可选） | `trigger`, `imageId` | — |
| `heartbeat` | 30s 心跳 | `status` | 0 |

> `risk` 由 Agent 本地初判。**服务器不采信该自报值作最终判据**（M2）：服务器凭独立黑名单（AI 站 / 搜索引擎 / 远控工具）+ 该考试**已下发**白名单重算 `server_risk`，以**有效风险 = max(agentRisk, serverRisk)** 决定是否入 `suspicious_queue`；`serverRisk ≥ 阈值` 而 `agentRisk < 阈值` 时,队列条目 `note` 记 `agent_risk_understated`（篡改逃逸取证）。L2/L3 后续再叠加。闭合 architecture §10.1 头号残留（持 PSK 学员机把「访问 AI 站」签成 `risk=0` 逃逸）。

## 4. 数据模型（live DB，权威 DDL = schema.sql）

核心表（字段详见 [../schema/schema.sql](../schema/schema.sql)）：

| 表 | 作用 | 关键列 |
|---|---|---|
| `exams` | 考试 | `exam_id, status, started_at, ended_at` |
| `seats` | 座位↔学员↔机器↔Agent | `(exam_id, seat_id), student_id, agent_id` |
| `events` | 事件流 | `(agent_id, seq) UNIQUE`, `type, payload(JSON), risk`(Agent 自报,留证)`, server_risk`(服务器复判)`, evidence_image_id, hash_self, sig` |
| `images` | 截图指针 | `image_id, trigger, phash, file_path, uploaded_to_ocr, is_evidence` |
| `ocr_results` | 云 OCR 结果（L2） | `image_id, text, hits, confidence` |
| `logo_hits` | Logo 匹配（L3） | `image_id, label, score, bbox` |
| `vec_images` | CLIP 向量（sqlite-vec） | `image_id, embedding FLOAT[512]` |
| `keystroke_samples` | 击键节奏 | `seat_id, timeline, features, risk` |
| `suspicious_queue` | 可疑队列（人工裁决） | `kind, score, status, refs, reviewer, decided_at` |
| `agent_heartbeats` | 在线状态 | `agent_id, ts, status` |

### 4.1 关键关系
- `events.evidence_image_id → images.image_id`（触发型抓图）。
- `images.image_id → ocr_results / logo_hits / vec_images`（一图多分析）。
- `suspicious_queue.refs` = JSON 数组，引用 `events.id` / `images.image_id`，是归档"关键数据"的判据之一。

## 5. M1 服务器最小职责清单
1. 起 WS `/ingest/events` 与 HTTP `/ingest/images`、`/ingest/keystroke`，校验 `X-Horus-Auth` / `sig`。
2. 落库到 `events` / `images`（含 `(agent_id, seq)` 幂等去重），原图存盘。
3. L1 已在 Agent 完成；服务器对 `risk ≥ 阈值` 的事件写 `suspicious_queue`。
4. 简易看板：座位在线（心跳）+ 可疑队列列表 + 点开看证据图与时间线。
5. （M2 起）将需要文字判定的图按 §5/architecture 收口送云 OCR，回填 `ocr_results`。

> L2 云 OCR、L3 Logo、CLIP 向量、归档作业属 M2/M3，本契约预留字段，M1 不实现。
>
> **M3 增补(管理面,已实现)**：
> - `GET /api/exams/{examId}/integrity` — 哈希链完整性离线审计。返回 `{ ok, totalEvents, totalHashOk, totalChainOk, totalUnverifiable, totalRestartBoundaries, sigVerified, note?, agents:[{agentId,seatId,total,hashOk,chainOk,unverifiable,restartBoundaries,hashMismatches[],continuityBreaks[]}] }`；已归档/归档中考试返回 `{ status:"archived"|"archiving", applicable:false, note }`（归档复核走下方 `/api/archive/exams/{id}`），不存在返回 404。**`sigVerified`**(第三轮 D6):false = 服务器未配 PSK(联调)、`ok` 仅表锚点自洽+链连续而**未做 sig 校验**,`note` 会点明,避免联调"绿"被误读为取证清白。**诚实边界**:哈希链**不能检出尾部截断**(删末条事件不产生 hash/链不符),靠人工/其它锚兜底(见 architecture §10.1)。`hashMismatches` 含两类:hash_self 与 payload 不符、或(配了 PSK 时)**sig 与 PSK 不符**(非 PSK 方改 payload 重算 hashSelf 使其自洽,唯 sig 识破)。`restartBoundaries` = Agent 重启后新起链段(hash_prev=GENESIS 且 seq 续增),**非篡改**。`unverifiable` = 迁移前缺 `machine_id` 的旧事件(不可复算,**非篡改**)。
> - `GET /api/images/{imageId}` / `/meta` — live 未命中时**自动回落 archive 库 + 冷存**(第三轮 F3:归档后证据图仍可取证读取;meta 带 `archived:true`)。
> - `GET /api/archive/exams/{examId}` — **归档考试只读复核**(第三轮 F3)。返回 `{ examId, archived:true, exam:{name,startedAt,endedAt,archivedAt,summary}, adjudications:[…], events:[…关键事件…], images:[…证据图…] }`;无归档库/无此考试 → 404。
> - `POST /api/agents/{agentId}/capture` — **监考员点名抓图**(第三轮 D2:使 `capture_now` 帧成真)。body 可选 `{reason}`,向该在线 Agent 推 `capture_now`,返回 `{ ok, agentId, pushed }`(agent 不在线 → pushed:false)。
> - `POST /api/archive/run` — 手动触发归档作业(后台亦每 `archiveScanIntervalHours` 自动跑)。返回 `{ now, scanned, archived, skipped, exams:[{examId,outcome,…}] }`。`outcome` ∈ `archived|skipped(pending 未裁决)|error`。
> - 事件哈希复验(M3)：ingest 侧从原始 payload 复算 `hashSelf`，与自报不一致 → `{ type:"error", code:"bad_hash", seq }` 拒收(见 §0.1)。`events` 表 M3 增 `machine_id` 列以支持离线复验。
