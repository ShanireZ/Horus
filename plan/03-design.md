# 維度三 · 設計審查（Design / UI Consistency）

> 審查物件：`server/wwwroot/styles.css`、看板視覺語言、響應式、`KIND_META` 配色一致性、`index.html` 結構。
> 審查口徑：UI 一致性、響應式適配、資訊設計層級。

---

## 總體評價（正面）
- 暗色「取證控制檯」主題統一：`styles.css` 使用 CSS 變數（`--bg/--fg/--accent/--danger` 等），元件風格一致。
- 響應式已有基礎斷點：`@media (max-width: 1100px)` 把右欄（抽屜/詳情）堆疊到主區下方（AGENTS.md 結構一致）。

---

## D1 〔P3〕`KIND_META` 缺 3 個 M5 標籤 → 部分可疑項缺配色/中文，打破主題一致

**狀態（2026-07-15）**：✅ 已實施（隨 01-U2 一併修）。`KIND_META` 增補 `screen_obscured`「螢幕遮擋」/`capability_degraded`「能力降級」/`watchdog_restart`「看門狗重新啟動」三組標籤，並補 `suspected_suspend`「疑似掛起」標籤；嚴格沿用既有 `fg/bg/bd` 暗色取證配色範式。

**證據**
- `app.js:15-26` 的 `KIND_META` 缺 `screen_obscured / capability_degraded / watchdog_restart` 三標籤；退回（`app.js:28`）用統一灰底 + 原始 kind 串。
- 見 01-U2 同一條目（功能維度），此處強調**設計一致性**受損：同一張可疑佇列裡，10 類有專屬配色、3 類退化為灰底機器串，視覺語言斷裂。

**修復方案**：同 01-U2，增補 3 標籤並嚴格沿用既有 `fg/bg/bd` 配色範式。

---

## D2 〔P3〕健康信號與作弊線索視覺層級未區分（資訊設計）

**狀態（2026-07-15）**：✅ 已實施（隨 01-U3 方案 A）。看板以獨立「採集健康」面板（冷色、唯讀、`source='health'`）呈現 M5 健康信號，與「可疑複核」（紅/橙、可裁決、`source='suspicion'`）在顏色/位置/權重上明確分層；M5 kind 配色沿用 D1 暗金冷調，與違規橙紅區分。

**證據/影響**
M5 健康信號與作弊線索共用同一繪製通道、同一張表（見 01-U3）。設計上應讓「採集健康」與「違規嫌疑」在顏色/位置/權重上明顯分層，否則監考員注意力被稀釋。

**修復方案**：與 01-U3 方案聯動——`health` 類用冷色（藍/灰）角標、獨立小面板或獨立 Tab，與違規（紅/橙）區分。

---

## D3 〔P3〕窄螢幕斷點覆蓋不足，燈箱/抽屜移動寬度可用性未驗證

**狀態（2026-07-15）**：✅ 已實施。`styles.css` 增加 `@media (max-width: 760px)` 斷點：座位熱力圖網格縮為 `minmax(84px,1fr)` + gap 7px 防橫向溢出；燈箱 `max-width:92vw` 可捲動；`#similarResults` `max-width:94vw` 橫向捲動；`.panel__head` / `.topbar` `flex-wrap` 在小窗堆疊。

**證據**
- `styles.css` 僅 `@media (max-width: 1100px)` 一個主要斷點（右欄堆疊）。
- 座位熱力圖網格在 760px 以下（平板/小窗）是否橫向溢出、燈箱大圖與「按圖搜圖」結果條在窄螢幕是否可捲動，未見針對性斷點與實測。

**修復方案**
- 增加 `@media (max-width: 760px)` 斷點：熱力圖改為可橫向捲動容器或換行更小單元格；燈箱圖片 `max-width:92vw`、相似結果條橫向捲動。
- 在預檢/ smoketest 中補一條「視窗寬度 375/768/1280 三檔佈局不溢出」的目檢清單。

---

## D4 〔P3〕「按圖搜圖」不可用狀態缺乏引導設計

**狀態（2026-07-15）**：✅ 已實施。`app.js` 燈箱在嵌入器未配時**保留禁用態「按圖搜圖」按鈕**（`btn.hidden=!imageSearchEnabled; btn.disabled=!usable`），`title` 提示「按圖搜圖不可用:未部署 CLIP 模型(model.onnx)」；`searchSimilar` 對 `!state.imageSearchEnabled` 直接 toast 並回傳。聯動 05-M7：預檢新增 `clip_model` 檢查項，指明不可用原因。

**證據**
- `imageSearchEnabled`（`app.js:192` 預設 `false`）由 `/api/authmode` 下發（`Endpoints.cs:67`）；嵌入器未配（無 `model.onnx`）時按鈕常隱。
- 監考員看不到按鈕，也無任何「為何不可用 / 如何啟用」的提示。

**修復方案**：嵌入器未啟用時，燈箱保留一個禁用態「按圖搜圖」按鈕 + tooltip「需部署 CLIP 模型（model.onnx）」，並在 `/api/preflight` 增加「CLIP 模型存在性」檢查項（見 05-缺失）。

---

## 優先級建議
1. D1（P3，隨 01-U2 一併修，無額外成本）。
2. D2（P3，與 01-U3 方案聯動）。
3. D3 / D4（P3，獨立小優化，按排期）。
