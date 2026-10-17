# 維度六 · 失效審查（Broken — 已實作但不可用）

> 審查口徑：程式碼裡存在、但執行環境實際不工作 / 行為錯誤的功能。
> 結論：**本次審查未發現已確認的「已實作但失效」功能**。以下僅列「潛在失效風險」與「需 owner 確認的邊界」。

---

## 已逐項核對、確認可用（避免誤報為失效）
| 功能 | 端點/程式碼 | 前端 | 結論 |
|---|---|---|---|
| 按圖搜圖 | `Endpoints.cs:71` `POST /api/exams/{id}/search-image` | `app.js:217` 取 `imageSearchEnabled`、`app.js:1125` 顯隱、`app.js:1141` `searchSimilar` | **可用**（嵌入器關時按鈕隱藏，行為正確） |
| 點名抓圖 | `Endpoints.cs:612` + `UplinkClient.cs:176` + `agent/Program.cs:153` | 座位抽屜「點名抓圖」按鈕（`app.js`） | **可用**（UI 已補齊，歸 01-U1，2026-07-15 已實施） |
| 雜湊鏈複驗 | `EventIngest` + `integrity` 端點 | — | 可用（有測試） |
| 歸檔作業 | `ArchiveService` | — | 可用（有測試） |
| OIDC / RBAC | `IngestAuth` / `admin_sessions` | 登入門 | 可用 |
| 考前預檢 | `Endpoints.cs:98` | 預檢按鈕 | 可用 |
| 可疑裁決 | `Endpoints.cs:635` `decide` | 佇列裁決 | 可用 |

---

## X1 〔P3·潛在〕`capture_now` 若 Agent 端 `OnCaptureNow` 未注入 → 靜默失效
**狀態（2026-07-15）**：✅ 已實施（見 02-A3）。`agent/Program.cs` 啟動斷言 + `UplinkClient.OnCaptureNow` 註解點明，未注入即告警。

## X2 〔P3·潛在〕CLIP 模型缺失時「按圖搜圖」整連線路徑不可用且無引導
**狀態（2026-07-15）**：✅ 已實施（見 05-M7 + 03-D4）。預檢 `clip_model` 檢查 + 燈箱禁用態「按圖搜圖」按鈕 + tooltip 提示「未部署 CLIP 模型(model.onnx)」。

---

## 優先級建議
- 本維度**無 P0–P2 項**。僅 X1/X2 兩個 P3 防禦性項，隨 02-A3 / 05-M7 / 03-D4 一併處理即可。
- ~~「點名抓圖按鈕是否有意不暴露」~~ → **已確認暴露到看板**（2026-07-15 實施），01-U1 維持「缺失→已修」，非「按預期行為」。
