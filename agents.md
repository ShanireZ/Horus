# AGENTS.md — Horus

> 本文件遵循并指回工作区根准则 [`../AGENTS.md`](../AGENTS.md)。先读根准则，再读本文件。

## 项目一句话
**Horus** — 本地局域网**考试监考系统**，防止学员在编程 / OJ 考试中用 AI 做题或联网搜题。学员**本地 IDE 写 C++ + 网页判题**提交；服务器为局域网内 1+ 台笔记本。架构 = **纯检测 + 取证**（已决定不做网络/主机预防层），**元数据优先、图像为辅**，系统只初筛、人工裁决。权威设计见 [docs/architecture-v0.2.md](docs/architecture-v0.2.md)。

## 语言
所有文档、注释、提交信息一律用中文。

## 组件与技术栈
- **共享契约** [contracts/](contracts/)（`Horus.Contracts`，net8.0）：线协议 / canonical / HMAC / 枚举 / 事件模型。Agent 与 Server **共用同一实现**，保证哈希链与签名两端逐字节一致。
- **采集核心** [agentcore/](agentcore/)（`Horus.Agent.Core`，net8.0）：平台无关的传输（WS/HTTP + 握手/hello/ack + **断线重连指数退避** + **续传**）、断网缓冲、配置、哈希链封装。刻意非 -windows，便于被测试直接引用。
- **采集端 Agent**（考试机，每台一个）：C#/.NET 单文件 exe，需管理员权限（ETW / UIAutomation / WMI）。Windows 专属部分（抓屏 / 信号源）。代码 [agent/](agent/)（`Horus.Agent`，net8.0-windows，引用 Core）。
- **监考服务器**（笔记本）：接收 + 分析 + 落库 + Web 看板。**.NET 8 / ASP.NET Core**（minimal API + WebSocket）+ **Microsoft.Data.Sqlite** + 文件系统（+ M3 起 sqlite-vec）。代码 [server/](server/)（`Horus.Server`，net8.0）。**仅服务器对外联网，且只为云 OCR**。
- **监考端 / 复核台**：实时看板 + 可疑队列复核。纯原生单页看板在 [server/wwwroot/](server/wwwroot/)。
- **测试**：[tests/](tests/)（`Horus.Server.Tests`，xUnit）——端到端覆盖 WS 握手/验签/幂等、图片去重、击键、人工裁决、canonical 黄金格式。

## 设计铁律（任何改动都必须守）
1. **预防层为零，检测必须扎实** — 控不了考场网络、也不做主机防火墙，联网搜题 / 网页 AI 只能靠 URL / 进程 / 截图**检测取证**（事后），不可阻断。**浏览器 URL 监控是第一防线**。
2. **唯一出网 = 云 OCR** — 除 L2 OCR 外，所有数据（元数据 / 原图 / 向量 / 看板）不出局域网。上云的图必须**最小化上传 + 裁剪 + 打码身份**，**原图永不出网**。见 architecture §5。
3. **系统只初筛、人工裁决** — 任何风险分 / 命中只是线索，处分由人判。
4. **元数据优先** — 能用 OS 信号判的不拍图；图只给可疑时刻 + 随机基线（专抓 IDE 插件）留证。
5. **诚实标注盲区** — 手机 / 第二设备 / Agent 未覆盖的多屏是结构盲区，靠物理监考兜，不假装覆盖。

## 关键决策（已锁定，见 architecture §0）
网页判题 + 本地 IDE · 无网络预防层 · 无主机防火墙 · C#/.NET Agent · 服务器集中 + 外部云 OCR · 1080p WebP q75 随机 30–90s · SQLite + 文件 + sqlite-vec · 留存 30 天后关键数据转 archive。

## 留存与归档
热数据（SQLite live + 文件）保留 **30 天**；30 天后**关键数据**（可疑/已判事件 + 其证据图 + OCR/Logo 结果 + 裁决记录 + 考试元数据 + 哈希锚）转入 archive DB，其余（干净基线图 / 低危例行事件 / 心跳）清理。详见 architecture §13、[schema/schema-archive.sql](schema/schema-archive.sql)。

## 目录
- [docs/architecture-v0.2.md](docs/architecture-v0.2.md) — 总体架构（**权威设计**）
- [docs/api-contract-m1.md](docs/api-contract-m1.md) — M1 接口契约（Agent↔Server 协议 + 数据模型）
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
- ⏳ 待办（记入 architecture §10.1/§10.2/§15）：**分析增强 M2**（L2 云 OCR + L3 Logo + 风险评分聚合，L2 待供应商）；M3（完整 canonical 复算 + 哈希链复验 / 归档清理任务 / CLIP 按图搜图）。里程碑见 architecture §15。

## 提交约定
默认不提交，除非用户明确要求。commit 信息用中文，简洁。
