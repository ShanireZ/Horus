# 維度四 · BUG 審查（Runtime / Logic / Boundary）

> 審查口徑：執行環境錯誤、邏輯錯誤、邊界條件、未覆蓋的潛在缺陷源。
> 說明：本專案 225 項測試全綠、契約位元組級共享、hash 鏈複驗、logout 競態已修（見 AGENTS.md）。**本次靜態審查未發現 P0/P1 級崩潰 bug**，以下為 P2/P3 級邏輯/邊界風險與「未測路徑」隱患。

---

## B1 〔P2〕`ImageSearchStore.TopN` 近正交圖不過濾，檢索品質下降

**狀態（2026-07-15）**：✅ 已實施。`ServerConfig` 新增 `EmbedCosineFloor`（預設 0.2）；`ImageSearchStore.TopN` 改為 `.Where(x => x.score >= cfg.EmbedCosineFloor)`（由端點傳入 `cfg.EmbedCosineFloor`），近正交（score≈0）影格被過濾；`server.config.sample.json` 同步暴露該設定與註解。

**證據**
- `server/Analysis/Search/ImageSearchStore.cs:48-54`：
  ```csharp
  .Where(x => x.score > -1)      // 餘弦 ∈ [-1,1]，近正交圖 score≈0 仍通過
  .OrderByDescending(x => x.score)
  .Take(n <= 0 ? 20 : n)
  ```
- 過濾 `score > -1` 僅丟棄「完全反向」匹配；CLIP 餘弦對不相關圖常 ≈0（甚至略正），仍會進入結果。當語料 < topN 時，「按圖搜圖」會混入大量 score≈0 的無關影格。

**影響**：非崩潰，但檢索品質降級，監考員在結果條裡看到大量無關縮略圖，干擾研判。

**修復方案**
- 將閾值改為可配的 `cosineFloor`（CLIP 經驗值 ~0.2）：`.Where(x => x.score >= cfg.EmbedCosineFloor)`。
- 或在回傳中保留 `score`，由前端按閾值隱藏低分結果；閾值建議在 `server.config.sample.json` 暴露。

---

## B2 〔P3〕`search-image` 的 `topN` 僅做下界備援，無上界 clamp

**狀態（2026-07-15）**：✅ 已實施（隨 B1）。`Endpoints.cs` 端點 `topN = Math.Clamp((int?)body?["topN"] ?? 20, 1, 100)`；`ImageSearchStore.TopN` 內部 `Math.Clamp(n, 1, 100)` 雙保險，杜絕超大 `topN` 輸入。

**證據**
- `Endpoints.cs:77` `int topN = (int?)body?["topN"] ?? 20;`；`ImageSearchStore.TopN` 僅 `n <= 0 ? 20 : n`（`ImageSearchStore.cs:53`）。
- 傳入超大 `topN`（如 100000）雖因語料僅單場幾千而實際影響有限，但屬未防禦的輸入。

**修復方案**：`topN = Math.Clamp(topN, 1, 100);`（或設定上界），與 B1 一併修。

---

## B3 〔P3〕M5 kind 映射與風險分無單測（潛在 bug 源）

**狀態（2026-07-15）**：✅ 已實施。新增 `tests/SuspicionTests.cs`（3 項理論）：`Suspicion.KindFor` 全分支（含 `SuspectedSuspend→"suspected_suspend"`、`WindowFocus→"suspect"` 備援）、`RiskModel.Derive` M5 分值（60/55/55/0）、`SourceForKind`（`health` vs `suspicion`）。把「新增 kind 必補前端標籤」變成可斷言契約（聯動 08-T4）。

**證據**
- `Suspicion.KindFor`（`Suspicion.cs:35-37`）對 M5 三類映射、`RiskModel` 中 M5 分值（`ScreenshotObscured=60 / CapabilityDegraded=55 / WatchdogRestart=55`，`suspected_suspend=0`）在 `tests/` 中**0 檔案**覆蓋（關鍵字 `ScreenObscured` / `Suspicion` 命中 0）。
- 一旦這些值或映射被改（如新增第 4 個健康信號、改分值），無測試報警，且會直接連累 01-U2 的繪製（缺標籤→raw 串）。

**修復方案**：新增 `SuspicionTests.cs` 覆蓋 `KindFor` 全部分支（含 M5 三類與預設 `suspect` 備援）、`RiskModel` 各 SignalType 分值（含 M5）。這同時把「新增 kind 必補前端標籤」變成可斷言的契約。

---

## B4 〔P3〕`capture_now` 端到端無測試（潛在迴歸）

**狀態（2026-07-15）**：✅ 已實施。新增 `tests/CaptureTests.cs`（2 項）：線上 Agent `POST /api/agents/ag-A07/capture` → `pushed:true` 且經 WS 收到 `capture_now` 影格；不線上 Agent → `pushed:false`。用 `app.ConnectEventsAsync` + `Ws` 助手斷言。

**證據**：`tests/` 中 `capture_now` 僅 1 處弱引用，無「伺服器端 push → Agent 抓圖 → 圖片入證據流」的端到端斷言（見 05-缺失）。

**修復方案**：在 `ReliabilityTests` 或新增 `CaptureTests` 中模擬 Agent 連上後，伺服器端 `POST /capture` → 斷言 `pushed:true` 且隨後收到一張 `trigger=capture_now` 的證據圖。

---

## 已核對「非 bug」項（避免誤報）
- **按圖搜圖**：端點（`Endpoints.cs:71`）、前端按鈕顯隱（`app.js:217/1125`）、`searchSimilar`（`app.js:1141`）均接通；嵌入器關時 `imageSearchEnabled=false` 按鈕隱藏，行為正確——**非失效**。
- **capture_now**：後端 + 採集端接通，僅缺 UI（歸 01-U1 / 05-缺失），**無執行環境 bug**。
- **雜湊鏈複驗 / 歸檔 / OIDC / 預檢 / 裁決**：均有實作與測試支撐。

---

## 優先級建議
1. B1（P2，改一行閾值，檢索品質立竿見影）。
2. B2+B1 合併改 `TopN`。
3. B3 / B4（P3，補測試，前置 02-A2）。
