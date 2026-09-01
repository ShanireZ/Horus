---
type: audit
title: Horus 代码审查报告（2026-07-21）
---

# Horus 代码审查报告（2026-07-21）

审查范围：`contracts/`(Horus.Contracts)、`agentcore/`(Horus.Agent.Core)、`agent/`(Horus.Agent)、`server/`(Horus.Server) 全部源码（约 80 个 .cs 文件，排除 obj/ 生成物）。

总体结论：项目经过多轮独立安全/逻辑/性能审计（225 测试全绿、M1–M5 + 身份层 + 归档均已实现），工程质量很高，核心安全不变量（哈希链、HMAC 验签、canonical 两端逐字节一致、fail-closed、读写分离、断网续传）落地扎实。以下为本次审查发现的剩余问题，按严重程度分级。

---

## 一、BUG / 逻辑错误

### [中] 1. `ImageIngest` 缺少请求体大小上限，且 body 在验签/鉴权前全量读入内存
**文件**：`server/Ingest/ImageIngest.cs`（HandleAsync，约 41–52 行）
- 图片通道把 `req.Body` 完整读入 `MemoryStream`（`CopyToAsync`）后才做 PSK/OIDC 验签。
- 全程**没有**像 `KeystrokeIngest` 那样的 `MaxBodyBytes` 显式上限（后者有声明 `Content-Length` 检查 + 实际 `ms.Length` 检查）。
- 兜底仅靠 `Program.cs:172` 的 Kestrel 全局 `MaxRequestBodySize = 2MB`。这意味着：
  - 未鉴权者（LAN 内任意能触达端点的机器）每次可强制服务器缓冲最多 2MB；
  - 若将来全局上限被调大，此处没有任何独立熔断。
- **建议**：在读取 body 前先查 `Content-Length` 并拒绝超限（与 `KeystrokeIngest` 一致），把鉴权失败/超限的判定前移，降低内存压力与放大攻击面。

### [低] 2. `ParseConfidence` 对 `1` / `1.0` 的边界歧义
**文件**：`server/Analysis/Vision/OpenAiCompatibleVisionAnalyzer.cs:140-142`
```csharp
if (d > 0 && d < 1) d *= 100;
```
- 若视觉模型以 0–1 概率返回 `1.0`（意图 100%），因 `d < 1` 为 false → 保持 `1` → 被钳成 1 分。
- 后果：`confidence:1` 且 `suspicious:true` 的图，因 `1 < cfg.VisionConfidenceThreshold(默认 50)` 而**不进可疑队列、也不标证据**。
- 属文档 §F9 已知权衡（刻意不把整数 `1` 放大成 100），但 `1.0` 这一浮点边界不鲁棒。
- **建议**：prompt 已要求 `0-100`，可在解析层对 `== 1.0` 显式归为"整数原样"（即保持 1）或要求供应商严格 0–100；并在真机 smoke 里确认 MiMo 实际返回格式。

### [中] 3. `BrowserUrlSource` 地址栏读取脆弱且低效（对抗网页 AI 的第一防线）
**文件**：`agent/Signals/BrowserUrlSource.cs:81-102`（含自身 TODO 注释）
- 每 2s 对前台浏览器窗口 `root.FindAll(TreeScope.Descendants, Edit 条件)` **全树遍历**，取第一个"像 URL"的 `Edit` 控件值。
- 问题：
  1. **可靠性**：未区分浏览器工具栏结构，多标签 / 地址栏结构变化 / 隐身模式可能读错或读不到 → 触发"url_unreadable"降级（仅强制抓图，靠人工看），削弱第一防线。
  2. **性能**：每次 poll 全树 UIA 遍历，CPU 开销随窗口复杂度上升。
- 代码已自承 TODO："生产应按浏览器缓存 AutomationElement / 用更窄的条件(toolbar→edit)"。
- **建议**：按浏览器类型缓存关键 `AutomationElement`、用 `Condition` 收窄到地址栏（toolbar 内的 edit），或在 UIAutomation 不可用时有更明确的降级策略。这是"检测取证"体系里权重最高的一条信号，值得优先加固。

### [中] 4. 多显示器盲区（实现层面未覆盖第二屏）
**文件**：`agent/Capture/ScreenshotCapturer.cs:64-72`（含 TODO 注释）
- `GrabPrimaryScreen()` 只抓主显示器。第二显示器上的网页 AI / 远程协助工具 / IDE 插件**完全漏采**。
- 架构 §11 已诚实标注"Agent 未覆盖的多屏是结构盲区"，但实现上仍是待补功能。
- **建议**：实现 `Screen.AllScreens` 遍历各屏各抓一张（注意基线/触发抓图的 seq 与去重逻辑要随之扩展），并配合"遮蔽检测"覆盖 DRM 保护窗口。

### [低] 5. `POST /api/exams` 空 seatId 可被插入
**文件**：`server/Api/Endpoints.cs:569-603`
- 校验逻辑：`if (sid.Length > 0 && !IsSafeId(sid)) return BadRequest` —— 仅当 `sid` 非空且非法时拒绝。
- 但插入用 `(string?)sn["seatId"] ?? ""`：空 `seatId` 通过校验、被原样插入 `seats(seat_id="")`。
- **建议**：显式拒绝空 `seatId`（与 `examId` 同等级校验）。属数据质量问题，不致命但会污染看板/席位关联。

### [低] 6. `Envelope.Serialize` 重复输出 `seq`
**文件**：`contracts/Wire.cs:143-154`
- 信封同时序列化 `seq = e.Seq`（外层）与 `@event = e`（内层含 `seq`）。两份 `seq` 值相同、层级不同，无害但冗余，且人为增加每帧体积。
- **建议**：外层 `seq` 用于快速路由、内层 `seq` 用于验签，可考虑去掉内层 `seq`（服务器已从 `hashSelf` 复算），或反之。属可读性/体积优化，非功能 bug。

---

## 二、功能待完善 / 部分实现

以下内容在架构文档中已标注"待 owner 真机验收 / 灰度验收"，列于此供跟踪：

1. **M5 Windows 专属硬化待真机验收**：`Watchdog` / `WindowsService` / `SessionLauncher` 中真实 suspend / obscure / 降权检测路径，文档 §15 标注"待 owner 真机验收"。
2. **`both→oidc` 灰度现场验收**：架构 §15 标注为待办；`/api/preflight` 已能报告"仍有 N 个座位走 PSK"作为 go/no-go 信号。
3. **`ClipboardWatcher` 干净关停**：`agent/Signals/ClipboardWatcher.cs` TODO 注明依赖进程退出回收，未实现消息泵干净退出（`Application.ExitThread`）。
4. **`ForegroundWindowSource` 事件驱动化**：TODO 建议用 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 替代轮询降开销（需消息循环）。
5. **`ProcessWatcher` 改用 ETW**：TODO 注明低延迟可走 ETW；当前依赖 `Process.Start` 事件，`CommandLine` 需管理员权限才非空（影响远控工具命令行特征提取）。
6. **多显示器抓屏**：见 BUG#4。
7. **击键前端埋点已移除（N/A）**：判题走外部洛谷、无本地判题页可埋点；由 Agent OS 级剪贴板/粘贴检测覆盖（架构 §6），属设计决策而非遗漏。

---

## 三、代码质量

1. **Agent 端日志用 `Console.WriteLine/Console.Error`** 而非注入 `ILogger`（`agent/Program.cs`、`agent/Signals/*` 等），与 Server 端结构化日志不一致，难统一收集/分级。（影响可维护性，非缺陷）
2. **风险分魔法数散落多处**：`40/70/80/60/55/50` 等出现在 `RiskModel.Derive`、`Suspicion`、Agent 的 `BrowserUrlSource`(80/40)、`Program.cs`(M5 健康分 60/55)、`KeystrokeIngest.RiskFrom`(70/60/55)。Agent 自报分与服务器复判分各自的"真相来源"易漂移，建议集中为常量/配置。
3. **`Endpoints.MapApi` 单方法约 700 行**：可读性差，建议按资源（exams / seats / suspicious / images / archive / auth）拆分成多个映射方法或 partial 类。
4. **`RiskModel` 与 `Suspicion` 的"信号类型→风险/类别"双套逻辑靠注释同步**：新增 `SignalType` 时需在三处（枚举、`RiskModel.Derive`、`Suspicion.KindFor`）同步，`Suspicion.cs` 顶部已用总表约束，但仍是手动。建议用单一 `SignalType → (risk, kind, source)` 映射表驱动。
5. **`ImageIngest` 与 `KeystrokeIngest` 的 body 读取 + 大小校验 + 鉴权顺序高度重复**：可抽成共享中间件/helper，避免两路安全逻辑日后分叉（BUG#1 正是此类分叉的体现）。
6. **`async void Handle` 在 `agent/Program.cs`**：作为事件回调可接受（已自捕获异常），但 fire-and-forget 的异常栈在诊断时不直观，建议在关键路径加 `TaskScheduler.UnobservedTaskException` 兜底或显式日志。

---

## 四、性能待优化

1. **`VisionAnalysisService` 单 reader 串行（`SingleReader = true`）**：逐张送视觉 LLM，单场数千张图时吞吐受限（网络 IO 串行）。当前按"不压视觉端点/不占 ingest 热路径"保守设计，可接受；若单场量大，可考虑受限并发（Channel 限流 2–4）提速。
2. **`/api/exams/{id}/seats` 每行含多个相关子查询**：虽整条是单 SQL，但 SQLite 对每行执行内部子查询（座位数 × ~6 子查询）。30 座位量级可接受（<200 次查询），更大规模可改 JOIN + 窗口函数一次性聚合。
3. **`IntegrityAudit.Run` 一次性拉全考试事件进内存**：按 agent 分组、seq 升序处理，极大考试（数千事件）内存 O(N)。当前规模 OK；超大规模可改为流式/分批。
4. **`search-image` 每次把整场 embedding 全读进内存再暴力余弦**（`ImageSearchStore.GetExam`）：当前单场几千张 × 512 维，OK；若上量可考虑分页或常驻内存索引（已明确不引 sqlite-vec）。
5. **`Db` 只读连接池固定 4**：完整性审计（全考试 SHA256）/ 归档 copy 读与看板 5s 轮询并行时可能排队；文档已说明为后续优化，当前不阻塞正确性。

---

## 五、已确认正确 / 无问题（抽样核对）

- **canonical 两端一致**：`EventCanonical.Core`(typed) 与 `CoreRaw`(raw text) 字段顺序、类型、null 处理一致；服务器 `VerifyHashSelf` 能锚定 payload。由 `CanonicalTests` 锁定的黄金测试覆盖。
- **断网续传 / seq 高水位**：`LocalBuffer` + `UplinkClient.NextSeq/AlignSeq` 设计正确，无 seq 复用；`RecoverCrashedRewrite`/`CleanupOrphans` 崩溃恢复完备。
- **读写分离**：`Db` 单写连接 + 只读连接池 + `:memory:` 回退正确。
- **OIDC 验签**：`OidcTokenValidator` 验 alg/kid/签名/iss/aud/exp/nbf/nonce，角色 fail-safe 到 `disciple`，正确。
- **归档幂等/崩溃安全**：门禁 + 墓碑 + 复制 + 清理顺序正确，`MoveToArchive` 与 `DeleteLive` 幂等。
- **管理面鉴权**：HttpOnly + SameSite=Strict cookie、FixedTimeEquals、CSP/nosniff/X-Frame-Options/Referrer-Policy 齐备。

---

## 优先级建议

| 优先级 | 项 |
|---|---|
| P1（建议本迭代修） | BUG#1（ImageIngest body 上限）、BUG#3（BrowserUrl 读取加固） |
| P2 | BUG#4（多显示器）、BUG#2（置信度边界）、代码质量#2/#5（风险常量集中、ingest helper 去重） |
| P3 | BUG#5（空 seatId）、BUG#6、其余性能/可读性项、第二节待办项（按 owner 真机验收排期） |
