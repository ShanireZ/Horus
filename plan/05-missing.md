# 維度五 · 缺失審查（Missing）

> 審查口徑：未實作的功能、未處理的異常、缺失的測試、缺失的對接文件/範例。

---

## M1 〔P2〕看板「點名抓圖」按鈕缺失（功能未連 UI）
**狀態（2026-07-15）**：✅ 已實施（見 01-U1）。座位詳情抽屜「點名抓圖」按鈕已接 `POST /api/agents/{agentId}/capture`，僅線上時可用。

## M2 〔P2〕M5 健康信號前端標籤缺失（後端已入隊，前端未呈現）
**狀態（2026-07-15）**：✅ 已實施（見 01-U2 / 03-D1）。`KIND_META` 增補三類 M5 標籤。

## M3 〔P3〕API 契約測試缺失
**狀態（2026-07-15）**：✅ 已實施（見 02-A2）。新增 `tests/EndpointsTests.cs`。

## M4 〔P3〕M5 kind 映射 / 風險分 / capture_now 端到端測試缺失
**狀態（2026-07-15）**：✅ 已實施（見 04-B3 / 04-B4）。新增 `SuspicionTests.cs` + `CaptureTests.cs`。

## M5 〔P3〕視覺分析（Vision）覆蓋偏弱
**狀態（2026-07-15）**：✅ 已實施。新增 `tests/VisionTests.cs`：`VisionVerdict.Kind()` 全分支映射（web_ai/search/ide_plugin→ide_plugin_suspect、remote_tool/other|none→suspect）、Susicious 標記、以及 `VisionAnalysisService.Enqueue` → `ocr_results` 寫入資料庫 + `analysis_state=1` 被輪詢拾回（visionMock:true）。

<a id="m6-p3ksk-擊鍵旁路缺乏對接文件--示例"></a>

## M6 〔P3〕KSK 擊鍵旁路缺乏對接文件 / 範例
**狀態（2026-07-15）**：✅ 已實施。新增 `docs/ksk-keystroke-integration.md`：簽名建構 `HMAC(KSK,"keystroke\n"+sha256(body))`、冪等 `submission_id` 語義、欄位映射表、Python/Node/curl 最小範例、伺服器打分/入隊規則、設定與安全檢查清單。

## M7 〔P3〕預檢未覆蓋 CLIP 模型存在性
**狀態（2026-07-15）**：✅ 已實施（聯動 03-D4）。`/api/preflight` 新增 `clip_model` 檢查項：嵌入器關時為 `ok`；onnx provider 但 `model.onnx`（按 `EmbedOnnxModelPath` 或 `DataDir/model.onnx`）缺失時為 `fail`；remote provider 為 `ok`。看板據以提示「按圖搜圖不可用原因」。

## M8 〔P3〕`suspected_suspend` 信號無看板呈現路徑
**狀態（2026-07-15）**：✅ 已實施（隨 01-U3 方案 A）。`Suspicion.SourceForKind` 把 `suspected_suspend` 歸入 `health`；`EventIngest` 入隊門改為「風險達閾值 ∨ 強制複核 ∨ 是 health 類」——`suspected_suspend`（`RiskModel=0`）因此強制入隊到 `suspicious_queue` 且 `source='health'`，由獨立「採集健康」面板呈現（score 0 顯示為「—」）；`KIND_META` 補 `suspected_suspend`「疑似掛起」標籤。`RiskModel` 註解同步修正為「走採集健康面板」。

---

## 優先級建議
1. M1 + M2（P2，與 01 同源，先做）。
2. M3 + M4 + M5（P3，測試債，排期補）。
3. M6 + M7 + M8（P3，文件/預檢/呈現缺口）。
