# 监考系统架构设计文档（v0.2 · 完整版）

- 项目：**Horus** — 本地局域网考试监考系统
- 版本：v0.2（取代 v0.1）
- 日期：2026-06-28
- 状态：M1（最小闭环）设计完成，进入实现
- 关联：[api-contract-m1.md](api-contract-m1.md) · [../schema/schema.sql](../schema/schema.sql) · [../schema/schema-archive.sql](../schema/schema-archive.sql) · [../agent/](../agent/)

> 场景：本地局域网内的编程 / OJ 考试。学员**本地 IDE 写 C++ + 网页判题**提交；服务器为局域网内 1+ 台笔记本；考试时长 2h。
> 哲学：**纯检测 + 取证**（预防层已确认不纳入）；**元数据优先、图像为辅**；**系统只初筛、人工裁决**。

## 0. 已锁定决策

| 编号 | 决策 | 选定 |
|---|---|---|
| D1 | 答题环境 | 网页判题 + 本地 IDE |
| D2 | 网络白名单预防层 | **不纳入**（控不了考场网络） |
| D2′ | 主机级防火墙预防 | **不纳入**（纯检测取证） |
| D3 | Agent 技术栈 | C#/.NET 单文件 exe |
| D3′ | 服务器技术栈 | **C#/.NET 8 · ASP.NET Core**（minimal API + WebSocket）+ Microsoft.Data.Sqlite。与 Agent 同栈,经 `Horus.Contracts` 共享契约 / HMAC canonical,哈希签名两端逐字节一致 |
| D4 | 图像分析位置 | 服务器集中 + **视觉 LLM(识图)** —— 取代专用云 OCR + L3 Logo,合并单一视觉级(2026-07-01 owner 拍板,详见 §4)。provider-agnostic;**已选定并真机验证 = 小米 MiMo-V2.5 托管 API**(`token-plan-cn.xiaomimimo.com/v1`·`model=mimo-v2.5`·OpenAI 兼容·**境内云**·PIPL 无跨境);key DPAPI 加密存储不存明文 |
| D8 | 截图参数 | **1080p · WebP q75 · 随机 30–90s** |
| D9 | 存储后端 | SQLite + 文件系统 + sqlite-vec |
| — | 留存 | **30 天**，到期关键数据转 archive，其余清理 |

## 1. 总体架构

```
 ┌──────────── 考试机 Agent（C#/.NET，每台一个）─────────────┐
 │ 采集: 窗口/进程/多浏览器URL/剪贴板/切窗/USB                   │
 │       + 截图(事件触发 / 随机30–90s基线)                       │
 │ 本地: pHash去重 · WebP压缩 · 风险初判 · 哈希链 · 断网缓冲      │
 └───────────────┬──────────────────────────────────────────┘
                 │ WebSocket(事件实时) + HTTP分块(图片) · HMAC签名
                 ▼
 ┌──────────── 监考服务器（笔记本，1+ 台）─────────────────────┐
 │ 接收·校验·落库(SQLite + 文件 + sqlite-vec)                    │
 │ 分析: L1 OS信号(本地·server_risk 独立复判)                    │
 │            │ L2 视觉 LLM 识图(取代 OCR+Logo·合并单一视觉级)    │
 │            ▼                                                 │
 │   ┌─────────────────────────────┐                           │
 │   │ 视觉 LLM 端点 ◀── 唯一出网    │  ← 降采样+剥元数据/最小化上传 │
 │   │ (境内云 MiMo / 或本地自托管)  │     原图永不出网,只送派生图    │
 │   └─────────────────────────────┘                           │
 │ 风险聚合(有效风险=max) → 可疑事件队列                          │
 └───────────────┬──────────────────────────────────────────┘
                 ▼
 ┌──────────── 监考端 / 复核台（Web 看板）──────────────────────┐
 │ 实时: 座位热力图·告警弹窗·缩略图   复核: 队列·证据图+时间线·按图搜图 │
 └────────────────────────────────────────────────────────────┘
```

**注意**：除 L2 云 OCR 外，所有数据（元数据、截图原图、向量、看板）**全程不出局域网**。

## 2. 采集端 Agent（C#/.NET）

### 2.1 元数据信号（L1，零图像，几十~几百字节/条）

| 信号 | 来源 | 抓什么 |
|---|---|---|
| 前台窗口标题 + 进程名 | UIAutomation / `GetForegroundWindow` | 当前在用什么 |
| **多浏览器地址栏 URL** | UIAutomation 无障碍接口（Chrome/Edge/Firefox） | **访问哪个站——本系统对抗网页 AI 的第一防线** |
| 进程启动/退出 | ETW / WMI | 起没起非白名单程序、远程协助工具 |
| 剪贴板事件 + 内容长度 | 剪贴板监听 | 大段粘贴 |
| Alt-Tab / 切窗频率 | 钩子 | 频繁切出 IDE |
| USB 插入 | 设备通知（WMI） | 外接设备 |
| 心跳 | 定时 | Agent 存活 / 防卸载 |

> **因预防层为零，URL 监控是重中之重**：判题站本身在浏览器里合法开着，所以判定要落到 **URL 级别**——判题站域名走白名单，浏览器里出现**任何其它 URL**（AI 站、搜索引擎、贴板）即高风险。必须覆盖主流浏览器，并对"隐身模式 / 冷门浏览器抓不到 URL"的情况降级为"进程级浏览器告警 + 强制抓图"。

### 2.2 截图策略
- **事件触发**：非白名单进程启动、浏览器出现非判题 URL、大段粘贴、切窗爆发、USB 插入 → 立刻 1 张。
- **随机基线**：每 **[30s, 90s]** 均匀随机 1 张。**专抓 IDE AI 插件**——Copilot / Continue 等补全不产生进程 / 网络 / URL 事件，唯一证据就是屏幕上"凭空出现整段代码"，只能靠定期看屏留底。
- **pHash 去重（保守）**：dHash 汉明距离 ≤3 视为重复，仅丢"短时间多次触发、画面几乎不变"的近似图。**随机基线层不去重**（每张都可能是孤证）；阈值刻意敏感，防止"只多几行补全"的关键图被误删。

### 2.3 本地处理
**1080p / WebP q75（≈150KB/张）** → pHash → 风险初判打分 → 哈希链封装 → 断网时本地缓冲，恢复后按 seq 续传去重。

## 3. 数据模型（概要，权威定义见 api-contract-m1.md）

```jsonc
// 事件流（WebSocket 上报）
{ "ts":..., "examId":"...", "seatId":"A07", "agentId":"...", "machineId":"...",
  "type":"window_focus|browser_url|process_start|process_exit|clipboard|alt_tab_burst|usb|screenshot|heartbeat",
  "payload":{...}, "risk":0,                        // 本地初判 0–100
  "evidenceImageId":"...|null",
  "hashPrev":"...", "hashSelf":"...", "seq":12345 } // 哈希链 + 单调序号
// 截图记录（HTTP 上传，原图存文件系统）
{ "imageId":"...", "seatId":"A07",
  "trigger":"event:browser|event:paste|baseline_random",
  "phash":"...", "file":"...webp", "w":1920, "h":1080,
  "uploadedToOcr":true, "ocrHits":["chatgpt"], "logoHits":["google"] }
```

## 4. 分析流水线（系统只初筛）

| 级 | 位置 | 做什么 | 备注 |
|---|---|---|---|
| **L1 OS 信号** | Agent 本地 + **服务器复判** | 窗口标题 / URL 正则匹配黑名单（`chat.openai`/`doubao`/`deepseek`/`google`/`baidu`/`bing`…）。服务器侧 `RiskModel` **独立复判**不采信 Agent 自报(M2 已实现,见 §10.1) | 零图像、最准最便宜 |
| **L2 视觉 LLM（识图）** | 服务器 → **视觉 LLM 端点** | **一次「看懂画面」同时做:文字提取 + AI 对话界面/搜索页/IDE 幽灵补全/远控工具识别 + 分类**,直出 `{suspicious,category,confidence,evidence}`。取代专用 OCR + L3 Logo(合并单一视觉级) | provider-agnostic(OpenAI 兼容:DeepSeek-V4/MiMo-V2.5/Qwen-VL/GLM-4V);**云端点=唯一出网(见 §5)**,或**本地自托管 MiMo(vLLM)= 零出网** |

> **为何视觉 LLM 取代 OCR(2026-07-01 owner 拍板)**:任务本质是「看懂画面」(认出 AI 界面/搜索页/IDE 幽灵补全)而非纯文字识别 —— 视觉 LLM 是 OCR 的超集,一次调用顶原 L2(OCR)+ L3(Logo),且结构化输出直接喂风险聚合 + 人话证据(契合「只初筛、人工裁决」)。取舍:每图成本高于专用 OCR(§5 最小化 + 只送触发型压量);画面攻击者可控 → 提示注入靠「只作一条线索 + 人工裁决」缓解。

各级命中累加**风险分**(有效风险 = max(Agent 自报, 服务器 L1 复判, 视觉 LLM 置信)) → 超阈值进**可疑事件队列** → 人工复核裁决。

## 5. L2 视觉 LLM 数据流与隐私收口（唯一出网，重点）

L2 现为**视觉 LLM 识图**(取代 OCR,见 §4)。走**云端点**时这是全系统唯一离开局域网的环节,按下列约束收口;走**本地自托管**(开源 MiMo + vLLM)时则**零出网**、收口第 4/5 条不适用。**✅ 派生处理已实现**(`server/Analysis/Vision/VisionImagePrep.cs`):送云前对截图做**降采样**(`visionMaxEdge` 长边上限,默认 1600·压 token/少送无关像素)+ **剥离元数据**(EXIF/XMP/IPTC/ICC 不随派生图出网),再重编码 WebP;**原图字节只读、原图永不出网**,送云的只有派生图。`visionMaxEdge≤0` 则直通原字节。
> **打码/裁剪已移除(owner 决策 2026-07-02)**:原设计的"打码身份(visionRedactRects)+ 裁剪(visionCropRect)"需按各考场 UI 逐一配矩形,运维负担 > 收益;且供应商已定为**小米 MiMo 境内云**(PIPL 无跨境)、考前已告知学员"截图可能经外部处理"。故只保留分辨率无关、免配置的降采样,不再做打码/裁剪。若未来改用无告知场景或跨境供应商,应重新评估身份最小化。

1. **最小化上传**：不是每张截图都送云。先经 L1 / L3 本地初筛 + pHash 去重，**只把"需要文字判定"的图**（命中可疑信号、或基线层抽样）送云，压低数量与成本。
2. **裁剪后再传**：能定位可疑区域（浏览器区 / 检测到的文字块）时，**只传裁剪区**，不传整屏。
3. **打码身份**：上传前对固定 UI 区域的学员姓名 / 学号做模糊 / 遮挡。
4. **无留存供应商**：选支持"不留存请求数据"的 OCR 端点，并在合规说明里写明。
5. **传输加密 + 合规告知**：HTTPS；考前告知学员"截图可能经外部 OCR 处理"，与供应商签数据处理协议。
6. **原图永不出网**：证据原图只存在局域网服务器，云端只收到裁剪 / 打码后的派生图。

> 成本提示：每场约 4500 张截图，若全量 OCR 约 $5–7/场；上面的"最小化上传"能再砍一大截。

## 6. 编程考专项：大段粘贴 + 击键节奏
- **大段粘贴**：Agent 监听剪贴板，记录粘贴长度 + 是否粘入 IDE / 答题区；超阈值即高风险并触发抓图。**默认只记元数据（长度 / 行数），不上传明文内容**；命中后按策略可留哈希或截断样本。
- **击键节奏**：在**判题网页前端埋点**（D1 走通），记录 keydown 时间线 + paste 事件。特征：整段粘贴、超人输入速度、长时间空窗后突现整段代码 → 标记"代码疑似非自己所写"。

## 7. 存储后端与估算

**SQLite（事件，+ sqlite-vec 存 embedding） + 文件系统（图片，按 exam/seat 分目录）。**

| 数据（30 人 × 2h） | 单人 | 全场 |
|---|---|---|
| 元数据事件 | <1 MB | ~30 MB |
| 截图（~150 张 ×150KB，1080p WebP q75） | ~20–30 MB | ~0.7–1 GB |
| embedding（150×512×fp16） | ~0.15 MB | ~5 MB |
| **合计** | **~25–35 MB** | **~1 GB** |

对比全程录屏（每人数 GB）仍是数量级节省；1TB 笔记本可存约 1000 场。

## 8. 向量化与检索（明确边界）
- **CLIP embedding = 检索索引，不是存储替代**。用途：按图搜图（选一张已知作弊图捞全场相似画面）、聚类。
- **不可逆**：~1KB 向量无法还原百万像素截图（信息论硬约束）。**证据永远是 WebP 原图**，embedding 只是找到它的钥匙。
- pHash 管"近似帧去重"，CLIP 管"语义相似检索"，分工不重叠。

## 9. 传输与可靠性
- 事件走 **WebSocket** 实时推（看板要实时）；图片走 **HTTP 分块 POST**。
- **断网续传**：Agent 本地缓冲 + 序号，恢复后补传、按 seq 去重。
- **多服务器**：单机起步；规模大时按座位段水平路由到多台笔记本。

## 10. 防篡改与完整性
- 事件**哈希链**（`hashPrev → hashSelf`），改动 / 删除 / 插入**链中段**任一条会使后续连续性校验失败（见 §10.1；**尾部截断**是结构性盲区，不可检——诚实标注）。
- Agent↔Server **HMAC 签名**（预共享密钥），防伪造上报。
- **双时间戳**：Agent 本机 `ts` + Server 接收 `recvTs`；考前 NTP 对齐。
- **管理面令牌**：`/api/*` 看板与管理写端点独立鉴权，防同网学员机操纵 config / 裁决 / 拉证据图。M2 起浏览器走 **HttpOnly cookie**（`POST /api/login` 下发，`SameSite=Strict`，JS 读不到、不进 URL），脚本客户端仍可用 `X-Horus-Admin` 头（`?t=` 兼容）。响应统一附 CSP / nosniff / X-Frame-Options / Referrer-Policy。
- **击键旁路 KSK 签名**：`/ingest/keystroke` 由判题后端持 KSK 对整条 body 签 `X-Horus-KSig`，防同网学员机伪造 / 栽赃他人 `seatId`（见 §10.1、api-contract §2.2）。
- 可靠性收口（M1 已实现）：事件先落盘再发、**逐条 ack 精确删除**（非范围 upto，避免共享 seq 空洞误删证据）、**序号高水位持久化**（重启不复用 seq）、断线指数退避重连 + 续传、缓冲原子压实 + 崩溃恢复。

### 10.1 残留风险（诚实标注，属威胁模型本质，非缺陷）
- **PSK 驻留学员机**：Agent 跑在学员机、PSK 在本地配置。持 PSK 者可对事件正确签名。
  - **✅ M2 已闭合「谎报 risk 逃逸」**：服务器**不再采信 Agent 自报 `risk` 作最终判据**——凭独立黑名单（AI 站 / 搜索引擎 / 远控工具）+ 该考试已下发白名单**重算 `server_risk`**，以**有效风险 = max(agentRisk, serverRisk)** 决定入队；`serverRisk≥阈值` 而 `agentRisk<阈值` 时记 `agent_risk_understated` 篡改标记。学员把「访问 AI 站」签成 `risk=0` 也压不住入队。大段粘贴亦从 payload 的 `len`/`lines` 独立复判,不信 Agent 的 `large` 自报。
    - **缺口（诚实标注）**：若该考试**未下发白名单**(从未 POST `/api/exams/{id}/config`),服务器对「非黑名单」站/进程无独立判据 → `server_risk=0` 退回靠 Agent 自报;黑名单已覆盖的站(openai/baidu 等)仍兜得住,但黑名单未覆盖的小众搜题/AI 站可漏网。**缓解**:建考试应随附并下发白名单;看板宜对 active 考试无白名单给运维告警(产品/流程面)。
  - **✅ M3 已实现「完整 canonical 复算 + 哈希链复验」**（闭合本条大半）：
    - **ingest 时**服务器从**原始 payload 文本 + 落库字段**逐字节复算 `canonicalCore`(`EventCanonical.CoreRaw`,与 Agent 端 `Core` 逐字节一致,黄金测试锁定)、复算 `hashSelf`,要求与自报一致,否则 **`bad_hash` 拒收不落库**。于是 `hashSelf`/`sig` 成为**真正锚定 payload/字段**的取证锚点(此前 sig 只绑 hashSelf 字符串,payload 未被签名覆盖)。
    - **离线审计** `IntegrityAudit` + `GET /api/exams/{examId}/integrity`:按 (agent,seq) 复验 ①锚点自洽(落库后 payload/字段被改 → 报 hashMismatch)②**签名自洽**(sig = HMAC-PSK,配了 PSK 时验:hashSelf 是无密钥 SHA256,非 PSK 方改 payload 后可重算 hashSelf 使①自洽,唯 sig 能识破 → 报 hashMismatch「sig 不符」)③链连续(hash_prev 链前驱,删除/插入/重排 → 报 continuityBreak)。**Agent 重启**新起链段(hash_prev=GENESIS,seq 续增)认作 `restartBoundary` 合法段起点、**不误报链断**(伪造的重启边界会被②的 sig 校验识破)。迁移前缺 machine_id 的旧行归 `unverifiable`,**不误报篡改**。
    - **仍属结构性残留(诚实)**:持 PSK 者仍可构造**自洽的**伪造链(payload 内容本身为假、但 hashSelf/sig/链全过)——如把真实 AI 站 URL 签成判题站。此类靠截图 / L2 视觉 / 人工裁决兜底,非哈希能防。缓解:每 Agent 独立 PSK(泄露隔离到单座位)、密钥机器保护而非明文。
    - **尾部截断不可检(诚实·第三轮补记)**:哈希链能抓「改动/删除/插入**中段**」,但**删除末尾若干条事件**只是让链变短、前缀仍完全自洽,`IntegrityAudit` 报 `ok:true`(即便配了 PSK 也不产生 hash/sig/链不符)。这是无「已签名事件计数/最大 seq 高水位锚」的哈希链**结构极限**。`hello_ack.maxSeq` 仅安全网、未持久化纳入审计。缓解:靠随机基线截图 / 人工在场 / 后续可选的「服务器侧 seq 高水位持久化 + 审计」兜底,非本里程碑范围。
- **⚠️ 事件通道跨身份栽赃 + seq 抢占(第三轮识别·共享 PSK 下真实·owner 定向 OIDC 闭合)**:握手 `X-Horus-Auth=HMAC(PSK, exam|seat|agent)` 与事件 `sig` 都用**全场共享 PSK**,服务器落库取**事件体自报的** examId/seatId/agentId。故持 PSK 的学员机可:①事件体填**他人 seatId/agentId** + 自洽签名 → 把伪造证据**栽赃到别人座位**,离线审计用同一把共享 PSK **无法追溯真凶**;②用他人 agentId 抢先占据其**未来 seq**,受害者真证据到达时撞 `(agent_id,seq)` 唯一约束被 `ON CONFLICT DO NOTHING` **静默丢弃**(定向压制取证)。**注**:仅"事件体身份==握手 query"的校验在共享 PSK 下**无效**(攻击者持 PSK 可直接为受害者身份伪造合法握手)。**与击键通道的不对称**:击键通道由判题后端持 **KSK**(学员机拿不到)已防住对称的栽赃,事件通道因 PSK 驻留学员机而留白。**owner 决策(2026-07-02)= 学员机改账密登录 + 每次经 cpplearn 的 OIDC 授权登录**,以 **per-user OIDC 身份**取代共享 PSK 根治栽赃与 seq 抢占(泄露隔离到单人、身份不可冒名)。**OIDC 为跨系统独立里程碑**(依赖 cpplearn 身份提供方),落地前此条属**诚实残留**,现场以物理监考 + 每 Agent 独立 PSK(泄露隔离)缓解。
- **✅ 击键旁路已签名 + 防重放（M2）**：`/ingest/keystroke` 改由**判题后端持 KSK 签名**（`X-Horus-KSig`,见 api-contract §2.2）——同网学员机无 KSK 无法伪造/栽赃他人 `seatId`；且按 `(exam,seat,ts,submissionId)` **幂等落库**,LAN 明文被嗅探后**原样重放**合法签名体也不重复入队（防灌队列 DoS）。残留:改了内容的重放会破签→401;完全新造需 KSK。
- **签名无时效防重放（事件）**：**刻意不加事件 `ts` 时窗**——与「断网缓冲 + 续传」可靠性设计冲突（一条网络中断期缓冲的事件到达时 `ts` 天然滞后 20 分钟,时窗会误杀合法续传）；而事件重放已由 `(agent,seq)` 去重 + 签名（`ts` 已进 canonical→`hashSelf`）覆盖,持 PSK 者本可直接签新帧,时窗对其无增益。故不引入。图片靠 imageId 去重。
- **PSK / 管理令牌留空 = 放行**：仅供本机联调。**已 fail-closed**：非 loopback 绑定却缺 PSK 或管理令牌时**拒绝启动**（`allowInsecure=true` 仅联调可绕）。KSK 未纳入硬 fail-closed（击键为次级通道,强制会误伤尚未接判题埋点的部署）；非 loopback 缺 KSK 时仅告警,生产应配。
- **✅ 管理令牌不再落 localStorage / URL（M2）**：改 **HttpOnly cookie**（`POST /api/login` 下发,`SameSite=Strict`+`HttpOnly`）——XSS 读不到令牌、`<img>` 不再靠 `?t=` 把令牌塞进 URL（防 Referer / 日志外泄）；配 CSP 收紧脚本源防注入外链外发。`?t=` / 头仍向后兼容。

### 10.2 规模 / 性能（M1 已够 30 Agent × 2h；下为已修 + M2 待办）
- **已修（第二轮复审）**：逐条 ack 改**攒批压实**（避免每 ack 全文件重写的 O(N²)）；重连退避加 **jitter**（打散多 Agent 同步重连风暴）；看板查询加索引（`events(…,risk)` 覆盖 / `evidence_image_id` 部分索引 / `agent_heartbeats(exam_id,ts)`）；`is_evidence` 回填仅对触发型图；图片体上限 8→2MB；`url_unreadable` 只在进入不可读态发一次（不再每 2s 刷爆队列）。
- **✅ 已做（M2）读写分离**：`Db` 从「单连接 + 全局写锁」改为 **单写连接（写锁串行）+ 只读连接池（WAL 并发读）**；看板 6 个只读 GET 端点走只读池,与采集写路径**互不阻塞**（监考端每 5s 轮询不再与写抢锁）。**读与读之间也并发**(池大小默认 4)：完整性审计(全考试事件 SHA256)/归档 copy 读不再串行阻塞交互看板轮询。`:memory:` 例外（内存库每连接独立且不支持 WAL）→ 回退单连接,测试无感。连接 `Pooling=false`（单例常驻,免 Dispose 后扣文件句柄妨碍归档 VACUUM）。
- **✅ 已做 config 持久化(闭合重启白名单退化)**：下发的每考试配置(`POST /api/exams/{id}/config`)落 `exam_config` 表,服务器重启时 `AgentHub` 回填内存缓存 → 白名单不丢,`server_risk` 复判不退化、Agent 重连 `hello` 仍能补推。
- **单写者仍用写锁,未上 Channel（刻意）**：写锁已保证「单写者串行」,而 ack 契约要求「持久化完成才回 ack」→ 后台 Channel writer 需逐条 TCS 等完成,延迟等价、仅增队列复杂度,30 Agent 规模无吞吐收益。**异步 Channel writer 作为规模余量留后续**（真遇线程池饥饿再上）。次要:单只读连接下并发看板读之间仍串行（只读连接池属后续优化）,但已与写解耦。
- **✅ 已做(M3)归档/清理定时任务**:`ArchiveService`(后台 `PeriodicTimer` 每 `ArchiveScanIntervalHours` 扫描 + 手动 `POST /api/archive/run`)扫描**结束超 `RetentionDays` 天**的考试,关键数据(有效风险≥阈值 或 被 suspicious_queue 引用的事件 / 其证据图 + OCR/Logo / 裁决 / **被引用或高危的击键样本** / 考试汇总 + hash 锚点)迁入独立 **archive 库**,其余就地清理,`status='archived'`,`VACUUM` 回收。**证据不丢**:pending 未裁决 → 跳过整场;copy(INSERT OR IGNORE)+ 文件移动(容忍已迁)幂等,copy 后 / delete 前崩溃重跑收敛。心跳等非关键随清理删除,整链断裂属**预期**(§13.2 复验以单条锚点为准)。

## 11. 威胁覆盖矩阵（诚实标注残留盲区——因无预防层）

| 威胁 | 捕获机制 | 残留风险 |
|---|---|---|
| 网页 AI / 联网搜题 | L1 URL 监控 + 截图 + 云 OCR | **无网络阻断，只能事后取证**；隐身 / 冷门浏览器抓不到 URL 时降级靠截图 |
| 本地 IDE AI 插件 | 随机截图（代码凭空出现）+ 击键节奏 | 低频截图有盲区；插件 UI 隐蔽时靠节奏 |
| 本地离线 LLM（另开程序） | 进程监控 + 截图 | 若在 Agent 未覆盖的第二显示器 / 设备则漏 |
| 大段粘贴外部代码 | 剪贴板 + 击键节奏 | 手动逐字誊抄难判 |
| 远程协助（TeamViewer 等） | 进程监控 + 截图 | 改名进程需特征库维护 |
| 手机 / 第二设备 | **本系统不覆盖** | **需物理监考兜底** |

> 结论：纯检测取证能覆盖"机内"作弊并留证，但**手机 / 第二设备、Agent 未覆盖的多屏**是结构性盲区，须靠现场物理监考补。

## 12. 监考看板与复核
- **实时**：座位网格 + 风险热力，红色告警弹可疑事件 + 缩略图。
- **复核**：按 student / risk 排序的队列；点开看证据图 + 事件时间线 + OCR / Logo 命中高亮；支持"按图搜图"。
- 系统给线索，处分由人判。

## 13. 隐私、留存与归档

### 13.1 隐私
仅考试时段采集；考前告知采集范围、留存期、**及"截图可能经外部 OCR"**；最小化采集（默认存元数据，图按需）。

### 13.2 留存 30 天 → 关键数据归档
热数据（live DB + 文件）保留 **30 天**。到期由**归档作业**处理（`ArchiveService`,后台每 `ArchiveScanIntervalHours`(默认 6h)扫描 + 运维可 `POST /api/archive/run` 手动触发,按 `exam.ended_at` 判龄）。**✅ M3 已实现**（见 [../server/Jobs/ArchiveService.cs](../server/Jobs/ArchiveService.cs)）:

- **迁入 archive（关键数据，长期留存）**：
  - 被可疑项引用、或有效风险(`max(risk,server_risk)`)`≥ 阈值` 的**事件**；
  - 这些事件的**证据图**（移入冷存目录 `archive/<exam>/<seat>/<id>.webp`，路径改写）+ 其 **OCR / Logo 结果**；
  - **被裁决引用或高危的击键样本**（confirmed 裁决的唯一证据常是击键时间线,随裁决归档 → `archive_keystroke_samples`）；
  - **裁决记录**（`suspicious_queue` 中 `confirmed` / `dismissed` 的条目）；
  - **考试 / 座位元数据**与**汇总**（人数 / 告警数 / 确认违规数 / 迁移计数）；
  - 归档事件的 **哈希锚**（`hashSelf` / `sig`），以便日后完整性复验。
- **就地清理（不入档）**：干净基线截图、低危例行事件、心跳、被清图的 embedding。
- **门禁**：仍有 `pending` 未裁决的考试**跳过整场**（不 purge 未决案证据，留待人工）。
- **幂等 / 崩溃安全**：copy 用 `INSERT OR IGNORE`、证据图移动容忍已迁；copy 后 / delete 前崩溃重跑收敛,证据先落 archive 才删 live,不丢关键数据。
- 作业完成后 `exam.status = 'archived'`，live 端 `VACUUM` 回收空间（尽力而为:与看板只读连接并发偶发 `SQLITE_BUSY` 则记 warning 下次再试,不影响正确性）。

> 归档库 DDL 见 [../schema/schema-archive.sql](../schema/schema-archive.sql)；"关键数据"判据可配（默认 `risk ≥ 50` 或被任一 `suspicious_queue` 条目引用）。
> 完整性说明：清理非关键事件会使原始哈希链**不连续**；归档保留每条关键事件的 `hashSelf` 作为独立锚点，复验以"单条事件内容 ↔ 其 `hashSelf` / `sig`"为准，不再依赖整链连续。

## 14. 部署形态
- 服务器：笔记本跑单进程（接收 + 分析 + 看板 Web + SQLite + 文件存储），**对外联网仅供 OCR**。
- Agent：考前分发 exe，登录自启、绑定座位号、需管理员权限（采 ETW / UIAutomation / WMI），考后停止。

## 15. 分期实现
- **M1 最小闭环**（✅ **已实现并端到端验证**：服务器 + 契约 + 看板 + **Agent 采集 / 握手 / 续传 / 断线重连**，14 项测试全绿）：Agent 采元数据 + L1 URL / 进程正则 + 事件 / 随机截图 + 服务器落库 + 简易看板。可靠性(§9)已落地：断网缓冲 + seq 续传 + 指数退避重连 + 服务器幂等去重。（接口契约见 api-contract-m1.md；构建/运行见 ../server/README.md）
- **M2 分析增强**：**L2 视觉 LLM(识图·取代 OCR+Logo)** + 风险评分 + 可疑队列。**✅ 骨架已实现**(provider-agnostic `IVisionAnalyzer` + Mock + OpenAI 兼容 adapter + 异步后台分析 → `ocr_results` + `server_risk` 聚合 + 可疑队列)。**✅ 供应商已定并真机验证 = 小米 MiMo-V2.5 托管 API**(`token-plan-cn.xiaomimimo.com/v1`·`model=mimo-v2.5`·境内云;key 自动加密存 `visionApiKeyEnc`)。**✅ §5 派生处理已实现**(降采样 + 剥离元数据·原图永不出网;打码/裁剪已按 owner 决策移除)。**待收尾**:基线抽样策略。
- **M3 取证强化**：**✅ 已实现「哈希链完整性复验」**(ingest 时 canonical 逐字节复算 + `bad_hash` 拒收 + 离线 `GET /api/exams/{id}/integrity` 审计锚点自洽 / 链连续,迁移前旧数据归 `unverifiable` 不误报) + **✅「归档作业」**(`ArchiveService`:到龄考试关键数据转 archive 库 + 清理 live + VACUUM,pending 跳过,幂等崩溃安全)。**⏳ 待做**:CLIP 按图搜图(需 sqlite-vec + CLIP ONNX 模型,`vec_images` 虚表 M1 起预留但跳过) + 击键节奏**前端埋点**(判题网页,本仓外;server 侧 KSK 收 + 归档已就绪)。
- **M4 健壮性**：断网续传、多服务器、Agent 防卸载。

## 16. 已采用默认值（次要项，可随时调）

| 项 | 默认 |
|---|---|
| 大段粘贴阈值 | ≥200 字符 或 ≥5 行 |
| 切窗爆发阈值 | ≥5 次 / 30s |
| pHash 去重 | dHash 汉明距离 ≤3；基线层不去重 |
| 击键特征 | keydown 时间线 + paste 事件 |
| 留存期 | **30 天**，到期关键数据转 archive，其余清理 |
| 关键数据判据 | `risk ≥ 50` 或被 `suspicious_queue` 引用（可配） |
| 多服务器 | 单机起步，预留水平扩展 |

## 17. 配套交付物
- 接口契约：[api-contract-m1.md](api-contract-m1.md)
- live DDL：[../schema/schema.sql](../schema/schema.sql)
- archive DDL：[../schema/schema-archive.sql](../schema/schema-archive.sql)
- Agent 骨架：[../agent/](../agent/)（`Horus.Agent`，net8.0-windows）
