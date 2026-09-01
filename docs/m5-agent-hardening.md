---
type: reference
title: M5 采集端硬化 —— 保活 / 防挂起 / 防遮蔽 / 防降权限（设计与任务）
---

# M5 采集端硬化 —— 保活 / 防挂起 / 防遮蔽 / 防降权限（设计与任务）

- 项目：**Horus** · 里程碑：**M5 采集端硬化（Agent 可靠性 / 抗规避）**
- 日期：2026-07-03 · 状态：**已实现（M5 里程碑时 176 测试全绿·0 警告·累计 225 全绿；Windows 专属部分待 owner 真机验收）**
- 关联：[architecture-v0.2.md](architecture-v0.2.md)（§10 威胁模型 / 铁律1 预防层为零）· [api-contract-m1.md](api-contract-m1.md)

## 0. 哲学定位（关键）

四项在 Horus **纯检测铁律**（预防层为零）下的正确形态 = **"检测 + 上报 + 看板健康告警"**，外加**保活维持采集工具存活**。**owner 拍板：不做内核 / 驱动级对抗**（违反哲学、反病毒必然误报、军备竞赛）。"防"不等于阻断——而是让**规避行为暴露**给监考员，由人工（物理监考）介入。

## 1. 已锁定决策（owner 2026-07-03）

| # | 决策点 | 选定 |
|---|---|---|
| H1 | 保活机制 | **三层纵深都要**：① Windows 服务(LocalSystem)看门狗 ② 兄弟 watchdog 进程互拉 ③ 心跳检测告警(兜底) |
| H2 | 对抗边界 | **检测 + 上报 + 看板健康告警**（不做内核对抗） |
| H3 | 本轮范围 | **全部实现含 Windows 专属**（本机仅编译 + 逻辑审查·真机由 owner 验收） |
| — | 规模 | 单场 30~60 人 × 2h **单服务器吞吐/存储绰绰有余**（余量 2~3 数量级）;多服务器= **冗余非扩展**,按需再做 |

## 2. 四项 → 检测信号

新增 4 个 `SignalType`（Contracts·两端 canonical 一致·snake_case）：

| 信号 | 触发 | 载荷 | 服务器处置 |
|---|---|---|---|
| `watchdog_restart` | 采集进程被结束后由看门狗/兄弟 watchdog 拉起,重启时上报 | `{reason, downMs?}` | 存 + 看板"异常重启次数"（关采集→暴露的取证） |
| `suspected_suspend` | 采集循环 wall-clock 跳变超阈值(被 suspend / 睡眠 / 锁屏) | `{gapMs, expectedMs}` | 存 + 看板"疑似挂起"标记 |
| `screenshot_obscured` | 截图纯色 / 低熵 / 尺寸异常(黑屏 / 遮挡 / DRM 保护窗口) | `{reason, metric}` | **抬 server_risk + 入可疑队列**（考中遮屏可疑） |
| `capability_degraded` | 非管理员 / 某信号源(ETW/UIA/WMI)持续失败 | `{capability, status, detail}` | 存 + 看板"采集能力健康度"降级 |

## 3. 模块划分（决定可测性）

- **Contracts**（`Wire.cs`）：+4 `SignalType`。两端共用。
- **agentcore**（平台无关·**可单测**）：
  - `Hardening/ScreenQuality.cs`：给定廉价像素统计(min/max/mean/方差/直方图)判"遮蔽"(纯色/低熵/尺寸异常)。**分类器与采集解耦 → 单测**。
  - `Hardening/SuspendMonitor.cs`：给定(上次 tick, 现 tick, 期望间隔)判 wall-clock 跳变 → suspend。**纯逻辑单测**。
  - `Hardening/CapabilityHealth.cs`：各能力状态聚合模型。**单测**。
  - `Hardening/UnexpectedExitMarker.cs`：本地落一枚"上次正常退出"标记;启动时若缺失/进程仍在→判定异常重启(供 watchdog_restart)。**逻辑单测**。
- **agent**（Windows 专属·**真机验收**）：管理员自检(`WindowsIdentity`)、信号源健康、`Bitmap` 像素统计(喂 agentcore 分类器)、suspend 接线、**兄弟 watchdog 进程**、**Windows 服务看门狗**(SCM 安装/卸载 + session0→session1 拉起采集 exe)。
- **server**：`ScreenshotObscured` → 复用 Suspicion/RiskModel 抬 server_risk + 入队;其余健康信号落库 + `seats` API 聚合"座位健康度"(isAdmin / 降级能力 / 疑似挂起次数 / 异常重启次数 / 最近遮蔽)。
- **看板**：座位卡健康徽章 + 抽屉健康度行。

## 4. 验证边界（诚实）

- **可单测**（本机绿）：agentcore 4 个纯逻辑核 + server 信号处置/座位健康聚合 + 看板渲染。
- **仅编译 + 逻辑审查**（真机验收）：Windows 服务看门狗(SCM / session0 隔离 / CreateProcessAsUser)、兄弟 watchdog 拉起、真实 suspend / 遮蔽 / 降权场景、UIA/ETW/WMI 真实失败。owner 真机跑一遍验收。

## 5. 残留（诚实标注·哲学一致）

- **第二显示器 / 虚拟桌面**仍是结构盲区（架构 §11）——遮蔽检测只抓"主屏纯色覆盖"一类,其余靠物理监考。
- **看门狗本身可被管理员权限停**（学员标准账户停不掉 LocalSystem 服务;但真管理员账户能）——停服务 / 关采集都靠**心跳断 → 看板告警**兜底,保活只缩短规避窗口,不做内核保护。
- 不阻断作弊——M5 强化的是"采集不被静默削弱",不改 Horus 纯检测取证本质。

## 6. 实现落地（2026-07-03·176 测试全绿·累计 225 全绿）

**✅ 已实现 + 单测（本机绿）**
- Contracts:`Wire.cs` +4 `SignalType`(`watchdog_restart`/`suspected_suspend`/`screenshot_obscured`/`capability_degraded`)。
- agentcore `Hardening/`:`ScreenQuality`(遮蔽分类)、`SuspendMonitor`(挂起)、`CapabilityTracker`(能力降级去抖)、`RestartClassifier`(异常重启)——4 纯逻辑核 + **16 单测**(`HardeningTests.cs`)。★坑:阈值 `record struct` 的 `new()` 零值绕过默认参数 → 改**引用类型 record**。
- server:`RiskModel.Derive` 给遮蔽 60 / 能力降级 55 / 异常重启 55(独立赋分·不信 Agent 自报)、`EventIngest.ParseType` + `Suspicion.KindFor` 认新类型、`/api/exams/{id}/seats` 加 `healthAlerts` 聚合 —— **2 服务端测**(遮屏 risk0 仍入队 / 座位健康计数)。
- 看板:座位卡健康告警标(⚠N·内联样式)+ 抽屉健康行 + tooltip。

**✅ 已实现 · 编译通过 · 待真机验收(Windows 专属)**
- agent 检测接线(`Program.cs` + `Capture/ScreenshotCapturer.cs` 每帧 luma 统计喂分类器 + `Hardening/AgentHardening.cs` 管理员自检/重启标记):遮蔽/挂起/能力降级/异常重启四类信号自检上报,复用 agentcore 已测核。
- **保活层2** `Hardening/Watchdog.cs`:`--watchdog` supervisor(启动+监控采集子进程·异常退出退避重启·命名 Mutex 单例·`--adopt` 守现有进程)+ 采集端 `GuardWatchdogAsync`(看门狗被杀→重拉 adopt 互拉)。
- **保活层1** `Hardening/WindowsService.cs`(`install-service`/`uninstall-service` 走 sc.exe·`--service` 用 `UseWindowsService` 托管 supervisor·LocalSystem) + `Hardening/SessionLauncher.cs`(`CreateProcessAsUser` 把采集拉进用户交互会话·session 0 截不到屏的必需项·失败回退普通启动)。
- **层3** 心跳告警:M1 已有,看板离线即暴露。

**真机验收清单(owner)**:① 服务 install/start → 采集在用户会话起、能截屏 ② Task Manager 杀采集 exe → 看门狗秒级重拉 ③ 杀看门狗 → 采集端重拉 adopt 看门狗 ④ 标准账户杀不掉 LocalSystem 服务 ⑤ suspend/睡眠恢复 → 看板 `suspected_suspend` ⑥ 纯色窗盖屏 → `screenshot_obscured` 入队 ⑦ 非管理员起 → `capability_degraded`。
