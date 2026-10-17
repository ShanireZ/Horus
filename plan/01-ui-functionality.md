# 維度一 · 介面與功能審查（UI & Functionality）

> 審查物件：`server/wwwroot/`（看板單頁應用 `index.html` / `app.js` / `styles.css`）、與看板交互的後端端點 `server/Api/Endpoints.cs`、採集端 `agent/` + `agentcore/`。
> 審查口徑：功能完整性、可用性、交互邏輯正確性（按鈕是否接得到後端、繪製是否正確）。
> 嚴重度定義：**P0** 阻斷/安全/資料損壞 → **P1** 主功能失效 → **P2** 功能缺口或明顯正確性/體驗缺陷 → **P3** 整潔度/可選優化。

---

## U1 〔P2〕看板缺「監考員點名抓圖」觸發入口（capture_now 不可達）

**證據**
- 後端已端到端實作：`Endpoints.cs:611-614` 的 `POST /api/agents/{agentId}/capture`（D2 承諾）向線上 Agent 推 `capture_now`；採集端 `agentcore/Transport/UplinkClient.cs:176-177` 解析 `capture_now` 並觸發 `OnCaptureNow`；`agent/Program.cs:153` 把 `OnCaptureNow` 接成 `_ = capturer.CaptureAsync(reason, dedupAgainstLast: false)`。
- 但是：`index.html` 的 topbar 僅含 開始 / 結束 / 全場登出 / 白名單 / 預檢（見 AGENTS.md:105「topbar 三按鈕」描述，未含抓圖）；`app.js` 中**無任何 `capture` / `抓图` / `点名` handler**；`KIND_META` 與座位抽屜均無觸發入口。

**影響**
D2 在「後端 + 採集端」層面已真實可用（AGENTS.md:87 已記「D2 capture_now 影格從文件承諾變真」），但監考員**無法從看板發起點名抓圖**——功能對最終使用者不可達，等於半完成。

**修復方案**
1. 在 topbar 或（更自然）在「座位抽屜 / 線上座位操作選單」增加「點名抓圖」按鈕。
2. 按鈕僅在該 seat 的 agent 線上時可用（可重複使用 `/api/exams/{id}/seats` 或心跳線上判定），離線置灰。
3. 點擊後 `POST /api/agents/{agentId}/capture`（body 可選 `{reason:"proctor_call"}`），依據回傳 `pushed` 給輕提示；成功後該圖進入證據流並可在燈箱查看。
4. 建議同時在前端補一個 `capture_now` 端到端測試（見 05-缺失 / 04-BUG）。

**狀態（2026-07-15）**：✅ 已實施。在「座位詳情」抽屜頂部增加「點名抓圖」按鈕（`app.js` `openSeatDrawer` + `captureNow`），僅在該座位 `online && agentId` 時顯示；點擊呼叫 `POST /api/agents/{agentId}/capture` 並依據回傳 `pushed` toast 提示。後端 D2 原已就緒，無需改動。mock 模式同樣可用。

---

## U2 〔P2〕M5 健康信號在可疑佇列中繪製為原始 snake_case

**證據**
- 後端入隊：`Suspicion.KindFor`（`server/Analysis/Suspicion.cs:35-37`）對 `ScreenshotObscured → "screen_obscured"`、`CapabilityDegraded → "capability_degraded"`、`WatchdogRestart → "watchdog_restart"` 入隊到 `suspicious_queue`；`RiskModel` 對這三類的分值為 `ScreenshotObscured=60 / CapabilityDegraded=55 / WatchdogRestart=55`。
- 前端缺標籤：`app.js` 的 `KIND_META`（`app.js:15-26`）共 10 個標籤，覆蓋 `web_ai / search / non_whitelist_proc / large_paste / usb / ide_plugin_suspect / browser_unreadable / non_whitelist_web / remote_tool / suspect`——**唯獨缺上述 3 個 M5 kind**。
- 退回邏輯：`kindMeta()`（`app.js:28`）對未命中項回傳 `{ label: kind || "未知", ... }`，即把 `screen_obscured` 原樣顯示成灰色原始串。

**影響**
AGENTS.md:101/104 明確 M5 目標是「檢測 + 上報 + **看板健康告警**」，但實際監考員在可疑佇列裡看到的是 `screen_obscured` 這類機器串，違背「健康告警」可讀意圖，且無法與真正作弊線索區分。

**修復方案**
在 `KIND_META` 增補（配色沿用暗色取證主題，與既有 `bd/bg/fg` 風格一致）：
```js
screen_obscured:    { label: "截图被遮蔽",   fg: "#ffd27f", bg: "#3a2c07", bd: "#a9841f" },
capability_degraded:{ label: "采集能力降级", fg: "#ffbe6b", bg: "#3a2a0e", bd: "#a9741f" },
watchdog_restart:   { label: "看门狗重启",   fg: "#7fd3ff", bg: "#12303f", bd: "#2f6f92" },
```
並補一條 `Suspicion.KindFor` → `KIND_META` 的「kind 全表」文件，避免後人新增 kind 時漏標籤（根因見 08-錯漏 / 07-漂移）。

**狀態（2026-07-15）**：✅ 已實施。`KIND_META` 已增補 `screen_obscured`「螢幕遮擋」/`capability_degraded`「能力降級」/`watchdog_restart`「看門狗重新啟動」三組標籤（暗金配色，與既有取證主題一致）。

---

## U3 〔P3〕健康信號與作弊線索混排，資訊層級不清

**證據/影響**
M5 健康信號（如 `screenshot_obscured` 可能僅是全螢幕應用，`suspected_suspend` 在 `RiskModel` 中 `=0` 僅作健康提示、不入隊）以「可疑項」形態進入 `suspicious_queue`，與真正作弊線索（AI 網站 / 遠控工具 / 大段粘貼）混在同一張表、同一繪製通道，無 `source/category` 區分。監考員易誤判或漏看健康告警。

**修復方案（已拍板：選 方案 A）**
- ✅ **方案 A（已實施）**：在 `suspicious_queue` 增加 `source` 列（`suspicion` / `health`），M5 三類（`screen_obscured` / `capability_degraded` / `watchdog_restart`）入隊時標 `source='health'`，其餘（含 vision / keystroke 入隊）預設 `suspicion`。看板用獨立「採集健康」面板呈現 `health` 類，不混入可疑複核、不計入作弊裁決率（`/suspicious` 預設 `source='suspicion'`，新增 `/health` 唯讀端點）。
- 方案 B（未採用）：M5 不進 `suspicious_queue`。因與既有歸檔/取證鏈耦合、改造成本高，且 A 已滿足「解耦呈現」訴求，故未採用。

**狀態（2026-07-15）**：✅ 已實施。後端 `source` 列 + 遷移 + `EnqueueSuspicious` 賦值 + `/health` 端點 + 裁決攔截 health；前端分段切換「可疑複核 / 採集健康」、health 行唯讀並跳轉座位詳情。

---

## 優先級建議
1. U2（P2，純前端標籤，低風險高收益，半小時可修）→ 優先。
2. U1（P2，需加按鈕 + 整合測試，但功能已就緒）→ 緊隨。
3. U3（P3，設計決策，先定方案再動）。

## 待確認（需你/owner 拍板）
> **已於 2026-07-15 全部確認並完成實作，本條關閉。**
- U3：**已選方案 A**（健康信號繼續留在 `suspicious_queue`，加 `source` 列區分；看板獨立「採集健康」面板呈現）。見 U3 / 03-D2 / 05-M8。
- U1：**確認暴露**「點名抓圖」按鈕到看板（非「按預期行為」）。見 U1 / 05-M1 / 06-broken X1。
