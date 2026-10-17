# 維度八 · 錯漏審查（Typos / Naming / Stale Comments）

> 審查口徑：拼寫、命名規範、過時/誤導性註解、缺註解導致的誤讀。本維度多為 P3 整潔度項，單獨列出便於「順手清」。

---

## T1 〔P3〕`schema.sql:122` 過時註解（與實作不符）

**狀態（2026-07-15）**：✅ 已實施（見 07-F3）。註解改為外部判題後端 KSK 旁路寫入。
- 原文：`-- 击键节奏(判题网页前端埋点上报)`。
- 問題：前端埋點已撤，真實路徑是外部判題後端 KSK 旁路（見 07-F3）。屬字面錯漏 + 誤導。
- 修復：改為 `-- 击键节奏(外部判题后端经 KSK 旁路 POST /ingest/keystroke 写入；前端埋点已撤)`。

## T2 〔P3〕`architecture-v0.2.md` 殘留舊術語 / 過時儲存表述

**狀態（2026-07-15）**：✅ 已實施（見 07-F1）。D9 / §7 / §8 舊 sqlite-vec 表述統一修正。
- D9（`:23`）與 §7 資料流（`:37`）仍寫 sqlite-vec（見 07-F1）。屬文件錯漏，需同步修正。

## T3 〔P3〕`ImageSearchStore.TopN` 過濾 `score > -1` 意圖不清

**狀態（2026-07-15）**：✅ 已實施（見 04-B1）。改為可配 `cosineFloor`（`>= cfg.EmbedCosineFloor`）過濾近正交圖，語義清晰。
- `ImageSearchStore.cs:51` 的 `.Where(x => x.score > -1)` 本意疑似「丟棄完全反向匹配」，但實際近正交圖（≈0）仍通過，語義與註解/命名不符，易讓維護者誤以為「已過濾無關圖」。
- 修復：改名/加註解明確閾值語義，或改為可配 `cosineFloor`（見 04-B1）。

## T4 〔P3〕`SignalType` 枚舉 → kind 命名風格混用，缺映射總表

**狀態（2026-07-15）**：✅ 已實施。`Suspicion.cs` 頂部新增「SignalType → kind → 標籤」總表註解；`SuspicionTests.cs` 斷言 `KindFor` 所有分支（含 `SuspectedSuspend→suspected_suspend`），把「新增 kind 必補前端標籤」變成可斷言契約，防迴歸。
- `ScreenshotObscured`（PascalCase 枚舉）→ `screen_obscured`（snake_case kind）；`WatchdogRestart` → `watchdog_restart`。契約統一 snake_case 是對的，但**缺乏一張「SignalType → kind → 前端標籤」總表**，導致後人新增 kind 時（如第 4 個健康信號）極易漏補 `KIND_META` 標籤（這正是 01-U2 的根因）。
- 修復：在 `docs/` 或 `Suspicion.cs` 頂部維護一張總表註解；並加測試斷言「`KIND_META` 覆蓋 `Suspicion.KindFor` 所有可能回傳值」（聯動 04-B3）。

## T5 〔P3〕`vec_images` 虛表命名暗示「已啟用」實則預留

**狀態（2026-07-15）**：✅ 已實施（見 02-A1 / 07-F2）。`vec_images` 虛表已徹底刪除，不存在「預留」誤導。
- `schema.sql:106` `vec_images` 命名與「未啟用」狀態不符（見 02-A1 / 07-F2）。若保留，建議註解明確「預留·永不啟用」，避免讀檔人誤判。

## T6 〔P3〕建議補充的缺註解點

**狀態（2026-07-15）**：✅ 已實施。`UplinkClient.OnCaptureNow` 註解點明「未注入 → 看板點名抓圖回傳 pushed:true 但 Agent 不抓圖（靜默失效），Program 注入後須保證非空」；`ImageEmbedService.Enabled` 上方註解說明「嵌入器未註冊（off/mock 未配）時為 false → 整服務 no-op；看板據 `/api/authmode` 的 imageSearchEnabled 決定按鈕顯隱」。
- `UplinkClient.OnCaptureNow`（`agentcore/Transport/UplinkClient.cs:33`）可空，建議在欄位上方註解「未注入則 capture_now 靜默失效（見 02-A3）」。
- `ImageEmbedService.Enabled`（`ImageEmbedService.cs:18`）建議註解明確「嵌入器未註冊（off/mock 未配）時為 false → 整服務 no-op」，便於讀檔人理解 `imageSearchEnabled` 來源鏈。

---

## 優先級建議
- T1 / T2 隨 07-F3 / 07-F1 一併改（同源）。
- T3 隨 04-B1 改。
- T4 是根因性整潔項，建議與 04-B3 測試一起做，長期防迴歸。
- T5 / T6 順手補。
