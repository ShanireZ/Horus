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

## 身份提供方是「贝塔通 BetaPass」，不是问天录（2026-08-07 起）

★★ 改任何与登录、claims、令牌、端点、撤权有关的东西之前，先读 `../BetaPass/docs/rp-contract.md`（**通用 RP 契约，以它为准**）与本仓 `docs/m4-identity-oidc.md` 首段的现状订正表。

六条最容易照旧文做错的：**PS256 不是 RS256**（允许清单只有一项）· **端点在根路径**不是 `/oauth/*` · **scope 是 `openid profile`**（`horus_profile` 永不登记）· **身份只有 `sub`/`name`/`username` 三项**（`username` 是**座位标识**不是显示字段）· **看板准入不在本地判**（贝塔通 `horus-admin` 平台开关）·
★★ **姓名与用户名走 userinfo（`/me`），不在 id_token 里** —— 问天录曾专门为本项目设 `conformIdTokenClaims: false`，**贝塔通没有这条豁免**。照旧从 id_token 取的表现是那两项恒为空串、座位号对每个人静默回退成 `sub`，**不报错、不抛异常、测试全绿**（测试令牌是自己签的、手工塞了那两个 claim）。现在 `OidcTokenValidator` 只给得出 `OidcSubject`，要身份必须经 `Userinfo.FetchAsync`。

## 目录

| 位置 | 是什么 |
|---|---|
| [docs/architecture-v0.2.md](docs/architecture-v0.2.md) | 总体架构（**权威设计**） |
| [docs/api-contract-m1.md](docs/api-contract-m1.md) | Agent↔Server 协议与数据模型 |
| [docs/m4-identity-oidc.md](docs/m4-identity-oidc.md) · [docs/m5-agent-hardening.md](docs/m5-agent-hardening.md) | 身份层 · 采集端硬化 |
| [docs/status.md](docs/status.md) | 里程碑与审计记录 |
| `schema/schema{,-archive}.sql` | live / archive 两个 SQLite DDL |
| `contracts/` · `agentcore/` · `agent/` · `server/` · `tests/` | 线协议库 · 平台无关采集核心 · Windows 采集端 · 服务器与看板 · 端到端测试 |

## Web Platform Baseline 与 Analytics 硬约束

两者都有门守着：Baseline 是 `WebBaselineContractTests`（拦截受监视但未登记、或只在注释里说了却没有真实检测与回退代码的现代 API）；Analytics 是「`.cc` 自动 / `.cn` 手工、统一 token、LAN/IP 零采集、单页最多一个 beacon」的合同测试。六字段与批准窗口在 [`baseline.config.json`](baseline.config.json)。下面只列**无人守卫**的：

- 看板是 `controlled-web`：原生静态资源**没有转译/打包阶段**，`buildTarget` 必须诚实标 `not-applicable`，不许伪造一个构建目标。
- 受控浏览器合同是机构管理的 Chrome / Edge 当前及前一主版本，**不含 downstream**。★ **开考前必须在实际监考工作站**跑登录、座位刷新、复核、灯箱与考试控制 —— 仓内测试代替不了这一步。
- ★★ **关键监考操作不得因浏览器缺少 Newly 能力而静默消失**，必须保留现有原生路径。
- ★★ **本地监考部署不得产生新的分析外联**：hostname 门控必须排除 `.cc`、localhost、IP 与 LAN。
- 改看板入口 / CSP / 配置下发 / loader 时，**必须保留上述合同测试**；CSP 只能在现有业务来源上追加，不能被 Analytics 改造覆盖。

## 构建 / 测试（需 .NET 8 SDK，无需 VS）
```
dotnet build Horus.sln -c Debug      # 全量编译(Agent 走 net8.0-windows)
dotnet test  Horus.sln -c Debug      # 运行端到端测试
```

## 状态

里程碑（M1–M5）、三路审计记录与测试计数在 [`docs/status.md`](docs/status.md)。**不许倒退的那几条硬线在上面的「已完成」节**，不在这里。

## Agent skills

- **Issue tracker：本仓 GitHub Issues。**
- triage 标签、domain 文档布局、OKF 文档系统沿用工作区约定：[`docs/agents/index.md`](docs/agents/index.md)。
- 进入工作区后必须读取根 [`../Docs/dev_guide.md`](../Docs/dev_guide.md) 的环节守则、完成判据与技能对照；Claude 由根 `CLAUDE.md` 显式导入，其他运行时不得假定自动加载。
