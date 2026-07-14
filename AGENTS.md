# AGENTS.md — Horus

> 本文件遵循并指回工作区根准则 [`../AGENTS.md`](../AGENTS.md)。先读根准则，再读本文件。

## 项目一句话
**Horus** — 本地局域网**考试监考系统**，防止学员在编程 / OJ 考试中用 AI 做题或联网搜题。学员**本地 IDE 写 C++ + 网页判题**提交；服务器为局域网内 1+ 台笔记本。架构 = **纯检测 + 取证**（已决定不做网络/主机预防层），**元数据优先、图像为辅**，系统只初筛、人工裁决。权威设计见 [docs/architecture-v0.2.md](docs/architecture-v0.2.md)。

## 语言
所有文档、注释、提交信息一律用中文。

## 组件与技术栈
- **共享契约** [contracts/](contracts/)（`Horus.Contracts`，net8.0）：线协议 / canonical / HMAC / 枚举 / 事件模型。Agent 与 Server **共用同一实现**，保证哈希链与签名两端逐字节一致。
- **采集核心** [agentcore/](agentcore/)（`Horus.Agent.Core`，net8.0）：平台无关的传输（WS/HTTP + 握手/hello/ack + **断线重连指数退避** + **续传**）、断网缓冲、配置、哈希链封装。刻意非 -windows，便于被测试直接引用。
- **采集端 Agent**（考试机，每台一个）：C#/.NET 单文件 exe，需管理员权限（ETW / UIAutomation / WMI）——exe 内嵌 `requireAdministrator` manifest，**双击即 UAC 提权**（免右键）。Windows 专属部分（抓屏 / 信号源）。代码 [agent/](agent/)（`Horus.Agent`，net8.0-windows，引用 Core）。
- **监考服务器**（笔记本）：接收 + 分析 + 落库 + Web 看板。**.NET 8 / ASP.NET Core**（minimal API + WebSocket）+ **Microsoft.Data.Sqlite** + 文件系统（+ M3 按图搜图走**本地 ONNX CLIP 暴力余弦·未用 sqlite-vec**）。代码 [server/](server/)（`Horus.Server`，net8.0）。**仅服务器可选对外联网，且只为视觉 LLM 识图**。
- **监考端 / 复核台**：实时看板 + 可疑队列复核。纯原生单页看板在 [server/wwwroot/](server/wwwroot/)。
- **测试**：[tests/](tests/)（`Horus.Server.Tests`，xUnit）——端到端覆盖 WS 握手/验签/幂等、图片去重、击键、人工裁决、canonical 黄金格式。

## 设计铁律（任何改动都必须守）
1. **预防层为零，检测必须扎实** — 控不了考场网络、也不做主机防火墙，联网搜题 / 网页 AI 只能靠 URL / 进程 / 截图**检测取证**（事后），不可阻断。**浏览器 URL 监控是第一防线**。
2. **唯一出网 = 视觉 LLM 识图（可选）** — 除 L2 视觉识图外，所有数据（元数据 / 原图 / 向量 / 看板）不出局域网。上云的图必须**最小化上传 + 降采样 + 剥离元数据（EXIF/XMP/IPTC/ICC）**，**原图永不出网**。（★裁剪/打码已于 2026-07-02 按 owner 决策移除：逐考场配矩形负担>收益·供应商=境内云 MiMo·PIPL 无跨境。）见 architecture §5。
3. **系统只初筛、人工裁决** — 任何风险分 / 命中只是线索，处分由人判。
4. **元数据优先** — 能用 OS 信号判的不拍图；图只给可疑时刻 + 随机基线（专抓 IDE 插件）留证。
5. **诚实标注盲区** — 手机 / 第二设备 / Agent 未覆盖的多屏是结构盲区，靠物理监考兜，不假装覆盖。

## 关键决策（已锁定，见 architecture §0）
网页判题 + 本地 IDE · 无网络预防层 · 无主机防火墙 · C#/.NET Agent · 服务器集中 + 外部视觉 LLM 识图（取代 OCR/Logo） · 1080p WebP q75 随机 30–90s · SQLite + 文件 + 本地 ONNX CLIP 按图搜图（暴力余弦·未用 sqlite-vec） · 留存 30 天后关键数据转 archive。

## 留存与归档
热数据（SQLite live + 文件）保留 **30 天**；30 天后**关键数据**（可疑/已判事件 + 其证据图 + 视觉识图结果（表名沿用 `ocr_results`） + 裁决记录 + 考试元数据 + 哈希锚）转入 archive DB，其余（干净基线图 / 低危例行事件 / 心跳）清理。详见 architecture §13、[schema/schema-archive.sql](schema/schema-archive.sql)。

## 目录
- [docs/architecture-v0.2.md](docs/architecture-v0.2.md) — 总体架构（**权威设计**）
- [docs/api-contract-m1.md](docs/api-contract-m1.md) — M1 接口契约（Agent↔Server 协议 + 数据模型）
- [docs/m4-identity-oidc.md](docs/m4-identity-oidc.md) — **M4 身份层**：cpplearn OIDC 取代共享 PSK（拓扑 A · 闭合 §10.1 栽赃/seq 抢占 · §10 RBAC 角色映射 + 监考员 OIDC 登录）
- [docs/m5-agent-hardening.md](docs/m5-agent-hardening.md) — **M5 采集端硬化**：保活/防挂起/防遮蔽/防降权限（纯检测=检测+上报+看板健康告警 + 三层保活看门狗·不做内核对抗）
- [schema/schema.sql](schema/schema.sql) — SQLite **live** DB DDL
- [schema/schema-archive.sql](schema/schema-archive.sql) — SQLite **archive** DB DDL
- [contracts/](contracts/) — 共享线协议库（`Horus.Contracts`）
- [agentcore/](agentcore/) — 平台无关采集核心（`Horus.Agent.Core`：传输/续传/重连/缓冲）
- [agent/](agent/) — Windows 采集端（`Horus.Agent`：抓屏/信号源）
- [server/](server/) — 监考服务器 + 看板（`Horus.Server`，见 [server/README.md](server/README.md)）
- [tests/](tests/) — 端到端测试（`Horus.Server.Tests`）
- [Horus.sln](Horus.sln) — 解决方案（4 个项目）

## 构建 / 测试（需 .NET 8 SDK，无需 VS）
```
dotnet build Horus.sln -c Debug      # 全量编译(Agent 走 net8.0-windows)
dotnet test  Horus.sln -c Debug      # 运行端到端测试
```

## 状态
**M1 最小闭环已实现并通过端到端验证**：
- ✅ `Horus.Contracts` + `Horus.Agent.Core` + `Horus.Agent`（编译通过·0 警告）+ `Horus.Server`（WS/HTTP ingest + 落库 + 看板）。
- ✅ **Agent 端可靠性完成**：握手鉴权、hello/hello_ack、ack、**断线重连（指数退避）**、**断网缓冲 + 续传**（`UplinkClient` + `LocalBuffer`，服务器幂等去重），图片 HMAC 签名 + 补传。
- ✅ **config_update 热更新**：服务器 `POST /api/exams/{examId}/config` → `AgentHub` 推送给在线 Agent（新连/重连在 hello 时补推）→ Agent `LiveConfig` 原子应用（白名单/阈值/截图参数，下一轮采集即生效）。
- ✅ **证据图跨重连关联**：触发型抓图**客户端预生成 imageId**（`X-Horus-Image-Id`，服务器沿用、跳过 pHash 去重、幂等），离线缓冲 + 断线重连补传后 `evidenceImageId` 关联不断。
- ✅ **三路独立审计（安全 / 并发与数据完整性 / 正确性与契约）+ 修复**：修掉两条会丢证据的 Critical（`ack` 改**逐条确认**杜绝空洞误删、**序号高水位持久化**杜绝重启复用）、`trigger` 映射为契约值、`is_evidence` 乱序回填、`url_unreadable` 强制入队、缓冲原子压实 + 崩溃恢复、Schema DDL 切分健壮化；安全加固：**管理令牌 `X-Horus-Admin`（/api 全鉴权）**、图片体/WS 帧大小上限、`X-Horus-Image-Id` 纳入签名、`LiveConfig` 上下限钳制、路径穿越边界、旧连接 Abort、Dispose 竞态。
- ✅ **第二轮复审（性能/负载/可靠性 · 安全 · 逻辑/回归 三路并行）+ 修复**：修上一轮引入的两条回归——`url_unreadable` 每 2s 刷爆队列（改为只在进入不可读态发一次）、逐条 ack 的 O(N²) 全文件重写（改**攒批压实**）；+ 性能（看板/回填索引、图片体 8→2MB、重连 jitter）+ 安全（**fail-closed** 非 loopback 缺凭证拒启动、`FixedTimeEquals` 不泄漏长度、图片 `?t=` 加 no-referrer/no-store、clientId 验签前校验）+ 逻辑（`LiveConfig` 只下发一端不篡改另一端）。
- ✅ **41 项测试全绿**（含两轮审计的回归：逐条/攒批 ack · 重启不复用 seq · trigger 映射 · is_evidence 回填 · Schema 分号健壮 · 管理鉴权 401·200·?t= · fail-closed · FixedTimeEquals · LiveConfig 单端下发）+ 真机 curl 验证。

**M2 信任模型加固（已实现 · 50 项测试全绿 · 三路独立审计放行）**：
- ✅ **服务器侧 risk 重算**（闭合 §10.1 头号残留）：不采信 Agent 自报 `risk`，凭独立黑名单（AI 站 / 搜索引擎 / 远控工具）+ 该考试**已下发**白名单重算 `server_risk`，以**有效风险 = max(agentRisk, serverRisk)** 决定入队；`serverRisk≥阈值` 而 `agentRisk<阈值` 记 `agent_risk_understated` 篡改标记。学员把「访问 AI 站」签成 `risk=0` 也压不住入队（`events` 加 `server_risk` 列 + 幂等迁移）。
- ✅ **keystroke 会话鉴权**（闭合 §10.1 栽赃）：判题后端持 **KSK** 对整条 body 签名 `X-Horus-KSig=HMAC(KSK,"keystroke\n"+sha256(body))`；改 seatId 栽赃即破签 → 401。域分隔前缀防跨通道重用。
- ✅ **admin 令牌改 HttpOnly cookie + SameSite=Strict + CSP**：`POST /api/login` 校验后种 cookie（JS 读不到、`<img>` 自动携带、不进 URL）；gate 接受 cookie/头/`?t=`（后二者兼容）；`/api/login /api/logout` 免鉴权；全响应附 CSP/nosniff/X-Frame-Options/Referrer-Policy。
- ✅ **DB 读写分离**（§10.2 吞吐天花板）：单写连接（写锁串行）+ 独立只读连接（WAL 并发读）；看板 6 个 GET 走只读连接与写路径互不阻塞；`:memory:` 回退单连接（测试无感）；`Pooling=false`。单写者仍用写锁（Channel 因 ack 契约无收益,作规模余量留后续）。
**M2 分析增强 · 视觉 LLM 引擎（骨架已实现 · 57 项测试全绿）**：
- ✅ **L2 视觉 LLM 识图取代 OCR + L3 Logo（owner 拍板·合并单一视觉级）**：一次「看懂画面」同时做文字提取 + AI 对话界面/搜索页/IDE 幽灵补全/远控工具识别 + 分类,结构化直出 `{suspicious,category,confidence,evidence}`。**provider-agnostic**:`IVisionAnalyzer` 接口 + `MockVisionAnalyzer`(确定性·测试联调·不出网)+ `OpenAiCompatibleVisionAnalyzer`(DeepSeek-V4 / 小米 MiMo-V2.5 / Qwen-VL / GLM-4V 皆 OpenAI 兼容 → 换 `visionBaseUrl`+`visionModel`+`visionApiKey` 即换供应商)。
- ✅ **异步后台分析**(`VisionAnalysisService` + `Channel`,**不占 ingest 热路径**):图入库 → 入队 → 判定 → 落 `ocr_results` + 标证据 + 引用事件抬 `server_risk` + 入可疑队列(note=`vision:证据`)。视觉关时整链 no-op。
- ✅ **供应商已定 = 小米 MiMo-V2.5 托管 API(境内云·OpenAI 兼容)**:`visionProvider=openai` · `visionBaseUrl=https://token-plan-cn.xiaomimimo.com/v1` · `visionModel=MiMo-V2.5`。**API key 不存明文·自动加密**:`SecretProtect`(Windows DPAPI 机器范围)。**UX = 运维直接把明文填进 config 的 `visionApiKey`,启动即自动加密为 `visionApiKeyEnc` 并清空明文(密文回写文件·行内替换保留注释)**;亦可 `Horus.Server protect-secret <key>` 预生成密文,或联调用 `HORUS_VISION_KEY` env 明文注入(优先级最高·不落盘)。启动时 `SecretProtect.Resolve` 解密进内存。真机 E2E 验证过(启动日志「已加密回写·明文已清除」+ 文件密文替换 + 注释保留)。
- ✅ **真机 smoke 已过**(2026-07-01·真 key):合成 ChatGPT 截图 → MiMo 返回 `{suspicious:true,category:web_ai,confidence:100,hits:[ChatGPT,C++,Dijkstra],evidence:...,text:提取的可见文字}` —— 端点通、key 有效、**adapter 请求格式被接受、MiMo 返回正好是 `Parse` 期望的 JSON schema、一次调用同时界面识别+文字提取+分类**。★**model id = `mimo-v2.5`(小写)**(不是 `MiMo-V2.5`;`/v1/models` 列出 mimo-v2.5 / -pro / -asr / -tts)。成本 ≈717 token/图(448 图 token)。
- ✅ **§5 派生处理已实现**(`server/Analysis/Vision/VisionImagePrep.cs`·ImageSharp 3.1.12):送云前**降采样**(`visionMaxEdge` 默认 1600·压 token)+ **剥离元数据**(EXIF/XMP/IPTC/ICC)再重编码 WebP;**原图字节只读、永不出网**,只送派生图;`visionMaxEdge≤0` 直通不解码(mock 测试走此路)。★**打码/裁剪已按 owner 决策(2026-07-02)移除**(逐考场配矩形负担>收益·供应商=境内云 MiMo·PIPL 无跨境·已告知学员)。
- ✅ **基线抽样策略**(收尾):`VisionAnalyzeBaseline=true` 时按 `VisionBaselineSampleRate` 做**确定性 1/N 抽样**(FNV-1a over imageId·跨重启一致),未抽中基线标 `analysis_state=1` 终结、补偿重扫不再拾回,控云成本。默认 1=全分析。

**M3 取证强化(已实现两项 · 三路独立审计放行 · 累计 130 项测试全绿)**:
- ✅ **哈希链完整性复验**(闭合 §10.1「服务器不重算 canonical」)=①**ingest 时**从原始 payload 文本 + 落库字段逐字节复算 `canonicalCore`(`EventCanonical.CoreRaw`,与 Agent 端 `Core` 逐字节一致·黄金测试锁定 ~20 反例含中文/代理对/U+2028/数字格式)、复算 `hashSelf`,不符 **`bad_hash` 拒收不落库** → `hashSelf`/`sig` 成为**真正锚定 payload** 的取证锚点(此前 sig 只绑 hashSelf 字符串,payload 未被签名覆盖);②**离线审计** `IntegrityAudit` + `GET /api/exams/{id}/integrity`:按 (agent,seq) 复验**锚点自洽**(落库后改 payload → hashMismatch)+ **链连续**(删/插/重排 → continuityBreak)。落 `machine_id` 列(canonicalCore 含 machineId,离线复算必须)。**诚实残留**:持 PSK 者仍可构造自洽伪造链(内容为假·靠截图/视觉/人工兜底)。
- ✅ **归档/清理作业** `ArchiveService`(后台 `PeriodicTimer` 每 6h + 手动 `POST /api/archive/run`)=扫描结束超 `RetentionDays`(默认30)天考试 → 关键数据(有效风险≥阈值 或 被 suspicious_queue 引用的**事件/证据图/OCR/Logo/击键样本**+裁决+汇总+hash锚)转独立 **archive 库**(证据图移冷存 `archive/<exam>/<seat>/<id>.webp`)→ 就地清理 live → `status='archived'` → VACUUM。**证据不丢**:pending 未裁决**跳过整场**;copy(INSERT OR IGNORE)+文件移动(容忍已迁)幂等,copy 后/delete 前崩溃重跑收敛。
- ★**三路独立审计(丢证据/安全/正确性契约)+全修**:修 **C1 Critical**=被 confirmed 裁决引用的**击键样本**原会被无条件删且无归档表→定罪证据永久丢失(补 `archive_keystroke_samples`+ParseRefs 认 `keystroke:`+清理前先归档关键击键);修 **High/Med**=`machine_id` 迁移使 M3 前旧事件被离线审计**误报"篡改"**(改归 `unverifiable` 单列·**绝不误报历史数据**);修 **Med**=已归档考试 integrity 端点返回空 `ok:true` 伪装全绿(改 `applicable:false`)。审计**验证为非缺陷**:CoreRaw≡Core 逐字节(实测)·path traversal 双层封堵·新端点 admin gate 全覆盖·hashSelf 复验不误拒合法在线事件·归档幂等/崩溃安全/FK 顺/锁模型均稳。
- ✅ **CLIP 按图搜图已落地**(见下方 M3 续批·本地 ONNX CLIP·**C# 暴力余弦·未用 sqlite-vec**)。击键节奏**前端埋点**已撤(考试都在外部洛谷·无本地判题页·核心粘贴信号 Agent OS 级剪贴板已覆盖·server 侧 KSK 收 + 归档保留就绪)。

**第三轮独立审查(5 并行 agent)+ 全批修复(owner 拍板全修 · 130 项测试全绿)**:取证锚点核心(canonical 逐字节·验签绑定·ingest 复验·归档崩溃安全/幂等·并发单写者·注入/DoS/密钥)5 路复核**均无新缺陷**;修复集中在视觉链失败态语义/URL 判据边界/归档读路径/文档漂移:
- **F1(High)**视觉临时云失败→证据图**永久漏析**(占位早置 uploaded_to_ocr、backstop 只捞未占位、失败不重置)→ 拆 `analysis_state`(处理闩锁·0待分析/1终结)与 `analysis_attempts`(重试上限 `visionMaxAttempts`),临时失败**不终结**由补偿重扫按 attempts<上限重试,确定态失败(文件缺失/派生失败)才终结;**F2** `uploaded_to_ocr` 语义拆净=**仅真出网(SendsOffNetwork 成功送出)才置 1**(mock/本地/失败恒 0·不再撒谎),meta 端点加 `analyzed`。
- **F3** 归档后证据无 HTTP 读路径 → `/api/images/{id}`+`/meta` **回落 archive 库+冷存** + 新增 `GET /api/archive/exams/{id}` 只读复核。
- **F4** 同一事件重复入队(ingest 一条+视觉一条)→ 视觉命中若已有 pending 引用该 event 则**并入**(score 取 max·note 追加 vision)不另插。
- **F5** URL 黑名单 `Contains` 子串假阳性(richardbard→bard·foryou.com→you.com)→ `RiskModel.HostMatchesAny` 改**按 DNS 标签**(裸词配整段标签·dotted 配域后缀);进程名保留子串(改名远控)。HostOf 解析失败返空不撞整串。**F11** 空 host(about:blank/data:)不再误判高危。
- **F6** `LocalBuffer.MaxBufferedSeq` 不再全量读图字节(只解析文件名)。**F7** config 端点补 `IsSafeId`。**F8** no-referrer/no-store 提全局中间件。**F9** ParseConfidence 整数 1 不再放大成 100(仅严格 0<d<1)。**F10** 视觉解码 `Image.Identify` 先探尺寸拒超 4096² 防解码炸弹。
- **D2** `capture_now` 帧从文档承诺变**真**(`POST /api/agents/{agentId}/capture` 推帧)。**D6** integrity 报告加 `sigVerified`/`note`(psk=null 联调"绿"不伪装取证清白)。文档漂移全清(测试数 130·event:manual·旧术语"云OCR/裁剪打码"·尾部截断诚实残留)。
- **⚠️ A1/A2 事件通道跨身份栽赃 + seq 抢占**(共享 PSK 下真实·"事件体身份==握手 query"修法**无效**因握手可伪造任意身份)→ **owner 决策 = 学员机改账密登录 + 每次 cpplearn OIDC 授权(per-user 身份取代共享 PSK)**,OIDC 跨系统另立项;已诚实写进 architecture §10.1。

**考试派发 + 常态待命 + 全场远程登出(owner 决策 2026-07-03 · 207 项测试全绿)**:
- ✅ **examId 不再由 Agent 配置携带**:`/oidc/exchange` 服务端指派「当前活跃考试」(status='active' 最近创建一场;无则 `no_active_exam` 拒,且**先于 token 交换**判定不白耗一次性授权码);**seatId := OIDC username**(空/不安全回退 sub·`ExamDispatch.SeatFrom`)—— 座位=身份,学员无法自报;协议/canonical/schema/KSK **零迁移**(字段保留,来源改变),物理定位由 machineId/agentId 承担。
- ✅ **Agent 常态待命循环**(oidc 模式):启动即待命轮询 `GET /oidc/active-exam`(**不采集不抓屏** —— 窗口外持续监控是隐私红线)→ 开考自动弹浏览器 OIDC 登录 → 采集 → 收 `exam_ended`(end 在线推送 / 重连 hello 按考试状态补发·留 5s 排空缓冲)或 `session_revoked`(立即停)→ 弃会话回待命等下一场;每 60s `GET /oidc/session` 探针兜底离线错过的推送。psk(legacy)模式保持配置 examId/seatId 的单场语义。
- ✅ **全场远程登出** `POST /api/exams/{id}/logout`(admin 门内):`SessionStore.RevokeByExam` 吊销全部采集会话 + 推 `session_revoked` + **强断在线连接**(吊销会话的旧 WS 不能续用),重连 401;`POST /api/exams/{id}/end` 现在向在线 Agent 推 `exam_ended`(响应带 notified)。
- ✅ **换场缓冲卫生**:新 OIDC 会话开始即 `LocalBuffer.PurgeSession`(旧 K_sess 已死,残留缓冲必 bad_sig 永不 ack → 每次重连重放-被拒死循环);seq 高水位保留 + hello_ack 对齐不撞旧 seq。
- ✅ **默认管理员运行**:agent exe 内嵌 `requireAdministrator` manifest(双击即 UAC 提权,免右键);⚠️ requireAdministrator 程序**不能**挂 Run 键自启(系统静默跳过)—— 本就不常驻自启(考试前手动打开),保活场景走 `install-service`(LocalSystem 无 UAC)。看门狗单例键改 `agentId_machineId`(与考试解耦)。
- ✅ **Agent 近零配置**(owner 决策 2026-07-03):`AgentConfig` 所有字段内置默认,**配置文件整个可选**(缺文件即全默认)。authMode=oidc / issuer=betaoi.cc / 采集参数 / 白名单(洛谷 + 常见 IDE)全烤默认;**agentId/machineId 留空由主机名自动推导**(machineId=主机名·agentId="ag-"+主机名·`ApplyIdentityDefaults`);examId/seatId/psk 在 oidc 无需配。**唯一去不掉 = 服务器地址**(Agent 连上前须知道服务器在哪·无法下发)→ owner 拍板烤固定默认 `192.168.32.145:8080`(IP 不符才覆盖);**dist/client 不再带任何配置文件**(纯默认零配置)。★STJ init 集合属性:配置提供的 whitelist **替换**(非合并)内置默认。

**M4 身份层 + M5 采集端硬化（承前·补记状态·功能面见对应 docs）**:
- ✅ **M4 采集面 OIDC 取代共享 PSK**（闭合 §10.1 A1 跨身份栽赃 / A2 seq 抢占）：学员机 cpplearn per-user 身份，事件体身份 == 会话身份强制一致；`both` 灰度共存、预检判据要求全部迁 OIDC 才切。见 [docs/m4-identity-oidc.md](docs/m4-identity-oidc.md)。
- ✅ **M4·RBAC 监考员看板 OIDC 登录**：cpplearn **长老 = 监考员**（elder 才进管理端），弟子 = 考生；缺 `user_type` fail-safe 到 disciple 绝不误授；**移除静态令牌后门**（adminAuthMode=oidc 时）；自签 HTTPS + cpplearn `horus-dashboard` client。
- ✅ **M5 采集端硬化**（纯检测 = 检测 + 上报 + 看板健康告警·不做内核对抗）：三层保活（Windows 服务 LocalSystem + 兄弟看门狗互拉 + 心跳告警）+ 4 健康信号（防挂起 `suspected_suspend` / 防遮蔽 `screenshot_obscured` / 防降权限 `capability_degraded` / 服务保活）。见 [docs/m5-agent-hardening.md](docs/m5-agent-hardening.md)。

**M3 续批 · CLIP 按图搜图（已落地）+ 运维 UX 收尾（2026-07-03）**:
- ✅ **本地 ONNX CLIP 嵌入器**（`OnnxClipEmbedder`·OnnxRuntime CPU·零出网）：预处理 resize + center-crop 224² + CLIP 归一化 → 512 维单位向量；★实测小米 MiMo-V2.5 **无** `/v1/embeddings` 端点 → owner 拍板改本地 ONNX。**规模小 → C# 暴力余弦，无需 sqlite-vec**（`vec_images` 虚表留大规模余量）。仅嵌证据图省算力；`ImageEmbedService` 后台 backstop 补嵌；看板灯箱「按图搜图」。⚠️真 ONNX 模型（HF `Qdrant/clip-ViT-B-32-vision` 的 `model.onnx`）属部署项，冒烟测试发现模型才跑。
- ✅ **考试管理看板 UI**：topbar 三按钮（开始 / 结束 / 全场登出·后端端点本就有·纯连 UI）。
- ✅ **OIDC token 交换重试**（`OidcHttp.PostFormWithRetryAsync`·瞬时 TLS/网络/5xx 重试·4xx 不重试不重复消费授权码）：缓解服务端出站偶发 TLS 拦截。
- ✅ **多密钥自动加密回写**（DPAPI）：视觉 key + 两个 OIDC secret（client + dashboard）填明文启动即加密为 `*Enc` 并清明文；★必在 env 覆盖之前跑（绝不把 env 注入的秘密写回盘）。
- ✅ **Agent 近零配置**：配置文件整个可选·authMode=oidc / 采集参数 / 白名单全烤默认·agentId/machineId 主机名自动推导·**唯一须配 = 服务器地址**（默认 `192.168.32.145:8080`）。

**★收口审计 + 修复（2026-07-05 · 225 项测试全绿）**:对 M3 三轮审查（130 项）以来 **~4257 行未经独立审计**的新代码（考试派发 / 会话吊销 · CLIP 按图搜图 · 零配置 · OIDC 重试 · 考试 UI）做**四维并行独立审计**（身份会话安全 / 并发数据完整性 / 正确性逻辑契约 / 前端接线 + 文档），逐条对抗性复核去伪，修 **7 项确认缺陷**：
- **[Medium] 全场登出竞态**：`EventIngest` 收帧循环握手后从不复查会话 → `RevokeByExam`（DELETE 恒先于 abort 遍历）漏断「登出瞬间正在握手」的连接 → 登记后补一次会话复查闭合（DELETE 先于 abort，凡错过 abort 者必登记于 DELETE 之后 → 复查必发现吊销）。
- **[Medium·部署地雷] 端口 5199 ↔ 8080 分裂**：源码默认 / `server.config.sample.json` / `server/README` 旧 5199，而 Agent 烤死 / dist 成品 / 部署文档全 8080 → 运维照 sample 起服连不上 → 三处提交物对齐 8080。
- **[Low] `/api/exams/{id}/end` 无状态守卫**：可把 archived/archiving 复活成 ended → 绕过 `/integrity` 的 `applicable:false` 保护伪装 ok:true → 加 `status IN('active','ended')` 守卫 + `IsSafeId`。
- **[Low] 归档清理漏 `image_embeddings`**（裸 PK 表无 FK）→ 向量行悬挂指向已删图永久滞留 → 补显式清理（删 images 之前）。
- **[Low] 视觉临时失败僵尸态**：达 `VisionMaxAttempts` 后卡 `analysis_state=0`（claim/backstop 都按 attempts<上限 过滤·既不重试也不终结）→ 达上限置终态放弃。
- **[Low] `esc()` 不转义单引号**（防御纵深·当前不可利用）+ **[Low] `OidcTokenValidator` 补 `nbf` 校验**。
- 审计**验证为非缺陷**（不改）：RS256 验签 / A1 身份强制 / RBAC / 密钥回写 / 常量时间比较 / 归档崩溃安全·幂等 / CLIP center-crop（确用 `ResizeMode.Crop`）/ DNS 标签匹配 F5·F11 / host 裸词假阳（宁可错杀·白名单兜底）。补 **4 项回归测试**（nbf×2 · 向量孤儿清理 · end 状态守卫）。

## 提交约定
默认不提交，除非用户明确要求。commit 信息用中文，简洁。
