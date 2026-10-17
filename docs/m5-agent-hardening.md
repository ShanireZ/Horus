---
type: reference
title: M5 採集端硬化 —— 保活 / 防掛起 / 防遮蔽 / 防降權限（設計與任務）
---

# M5 採集端硬化 —— 保活 / 防掛起 / 防遮蔽 / 防降權限（設計與任務）

- 專案：**Horus** · 里程碑：**M5 採集端硬化（Agent 可靠性 / 抗規避）**
- 日期：2026-07-03 · 狀態：**已實作（M5 里程碑時 176 測試全綠·0 警告·累計 225 全綠；Windows 專屬部分待 owner 真機驗收）**
- 關聯：[architecture-v0.2.md](architecture-v0.2.md)（§10 威脅模型 / 鐵律1 預防層為零）· [api-contract-m1.md](api-contract-m1.md)

## 0. 哲學定位（關鍵）

四項在 Horus **純檢測鐵律**（預防層為零）下的正確形態 = **"檢測 + 上報 + 看板健康告警"**，外加**保活維持採集工具存活**。**owner 拍板：不做核心 / 驅動級對抗**（違反哲學、反病毒必然誤報、軍備競賽）。"防"不等於阻斷——而是讓**規避行為暴露**給監考員，由人工（物理監考）介入。

## 1. 已鎖定決策（owner 2026-07-03）

| # | 決策點 | 選定 |
|---|---|---|
| H1 | 保活機制 | **三層縱深都要**：① Windows 服務(LocalSystem)看門狗 ② 兄弟 watchdog 處理程序互拉 ③ 心跳檢測告警(備援) |
| H2 | 對抗邊界 | **檢測 + 上報 + 看板健康告警**（不做核心對抗） |
| H3 | 本輪範圍 | **全部實作含 Windows 專屬**（本機僅編譯 + 邏輯審查·真機由 owner 驗收） |
| — | 規模 | 單場 30~60 人 × 2h **單伺服器吞吐/儲存綽綽有餘**（餘量 2~3 數量級）;多伺服器= **冗餘非擴充**,按需再做 |

## 2. 四項 → 檢測信號

新增 4 個 `SignalType`（Contracts·兩端 canonical 一致·snake_case）：

| 信號 | 觸發 | 載荷 | 伺服器處置 |
|---|---|---|---|
| `watchdog_restart` | 採集處理程序被結束後由看門狗/兄弟 watchdog 拉起,重新啟動時上報 | `{reason, downMs?}` | 存 + 看板"異常重新啟動次數"（關採集→暴露的取證） |
| `suspected_suspend` | 採集循環 wall-clock 跳變超閾值(被 suspend / 睡眠 / 鎖屏) | `{gapMs, expectedMs}` | 存 + 看板"疑似掛起"標記 |
| `screenshot_obscured` | 截圖純色 / 低熵 / 尺寸異常(黑屏 / 遮擋 / DRM 保護視窗) | `{reason, metric}` | **抬 server_risk + 入可疑佇列**（考中遮屏可疑） |
| `capability_degraded` | 非管理員 / 某信號源(ETW/UIA/WMI)持續失敗 | `{capability, status, detail}` | 存 + 看板"採集能力健康度"降級 |

## 3. 模組劃分（決定可測性）

- **Contracts**（`Wire.cs`）：+4 `SignalType`。兩端共用。
- **agentcore**（平台無關·**可單測**）：
  - `Hardening/ScreenQuality.cs`：給定廉價像素統計(min/max/mean/方差/直方圖)判"遮蔽"(純色/低熵/尺寸異常)。**分類器與採集解耦 → 單測**。
  - `Hardening/SuspendMonitor.cs`：給定(上次 tick, 現 tick, 期望間隔)判 wall-clock 跳變 → suspend。**純邏輯單測**。
  - `Hardening/CapabilityHealth.cs`：各能力狀態聚合模型。**單測**。
  - `Hardening/UnexpectedExitMarker.cs`：本機落一枚"上次正常退出"標記;啟動時若缺失/處理程序仍在→判定異常重新啟動(供 watchdog_restart)。**邏輯單測**。
- **agent**（Windows 專屬·**真機驗收**）：管理員自檢(`WindowsIdentity`)、信號源健康、`Bitmap` 像素統計(喂 agentcore 分類器)、suspend 串接、**兄弟 watchdog 處理程序**、**Windows 服務看門狗**(SCM 安裝/卸載 + session0→session1 拉起採集 exe)。
- **server**：`ScreenshotObscured` → 重複使用 Suspicion/RiskModel 抬 server_risk + 入隊;其餘健康信號寫入資料庫 + `seats` API 聚合"座位健康度"(isAdmin / 降級能力 / 疑似掛起次數 / 異常重新啟動次數 / 最近遮蔽)。
- **看板**：座位卡健康徽章 + 抽屜健康度行。

## 4. 驗證邊界（誠實）

- **可單測**（本機綠）：agentcore 4 個純邏輯核 + server 信號處置/座位健康聚合 + 看板繪製。
- **僅編譯 + 邏輯審查**（真機驗收）：Windows 服務看門狗(SCM / session0 隔離 / CreateProcessAsUser)、兄弟 watchdog 拉起、真實 suspend / 遮蔽 / 降權場景、UIA/ETW/WMI 真實失敗。owner 真機跑一遍驗收。

## 5. 殘留（誠實標註·哲學一致）

- **第二顯示器 / 虛擬桌面**仍是結構盲區（架構 §11）——遮蔽檢測只抓"主屏純色覆蓋"一類,其餘靠物理監考。
- **看門狗本身可被管理員權限停**（學員標準帳戶停不掉 LocalSystem 服務;但真管理員帳戶能）——停服務 / 關採集都靠**心跳斷 → 看板告警**備援,保活只縮短規避視窗,不做核心保護。
- 不阻斷作弊——M5 強化的是"採集不被靜默削弱",不改 Horus 純檢測取證本質。

## 6. 實作完成實作（2026-07-03·176 測試全綠·累計 225 全綠）

**✅ 已實作 + 單測（本機綠）**
- Contracts:`Wire.cs` +4 `SignalType`(`watchdog_restart`/`suspected_suspend`/`screenshot_obscured`/`capability_degraded`)。
- agentcore `Hardening/`:`ScreenQuality`(遮蔽分類)、`SuspendMonitor`(掛起)、`CapabilityTracker`(能力降級去抖)、`RestartClassifier`(異常重新啟動)——4 純邏輯核 + **16 單測**(`HardeningTests.cs`)。★坑:閾值 `record struct` 的 `new()` 零值繞過預設參數 → 改**引用類型 record**。
- server:`RiskModel.Derive` 給遮蔽 60 / 能力降級 55 / 異常重新啟動 55(獨立賦分·不信 Agent 自報)、`EventIngest.ParseType` + `Suspicion.KindFor` 認新類型、`/api/exams/{id}/seats` 加 `healthAlerts` 聚合 —— **2 伺服器端測**(遮屏 risk0 仍入隊 / 座位健康計數)。
- 看板:座位卡健康告警標(⚠N·內聯樣式)+ 抽屜健康行 + tooltip。

**✅ 已實作 · 編譯通過 · 待真機驗收(Windows 專屬)**
- agent 檢測串接(`Program.cs` + `Capture/ScreenshotCapturer.cs` 每影格 luma 統計喂分類器 + `Hardening/AgentHardening.cs` 管理員自檢/重新啟動標記):遮蔽/掛起/能力降級/異常重新啟動四類信號自檢上報,重複使用 agentcore 已測核。
- **保活層2** `Hardening/Watchdog.cs`:`--watchdog` supervisor(啟動+監控採集子處理程序·異常退出退避重新啟動·命名 Mutex 單例·`--adopt` 守現有處理程序)+ 採集端 `GuardWatchdogAsync`(看門狗被殺→重拉 adopt 互拉)。
- **保活層1** `Hardening/WindowsService.cs`(`install-service`/`uninstall-service` 走 sc.exe·`--service` 用 `UseWindowsService` 託管 supervisor·LocalSystem) + `Hardening/SessionLauncher.cs`(`CreateProcessAsUser` 把採集拉進使用者交互工作階段·session 0 截不到屏的必需項·失敗退回普通啟動)。
- **層3** 心跳告警:M1 已有,看板離線即暴露。

**真機驗收清單(owner)**:① 服務 install/start → 採集在使用者工作階段起、能截屏 ② Task Manager 殺採集 exe → 看門狗秒級重拉 ③ 殺看門狗 → 採集端重拉 adopt 看門狗 ④ 標準帳戶殺不掉 LocalSystem 服務 ⑤ suspend/睡眠恢復 → 看板 `suspected_suspend` ⑥ 純色窗蓋屏 → `screenshot_obscured` 入隊 ⑦ 非管理員起 → `capability_degraded`。
