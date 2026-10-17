# 維度七 · 漂移審查（Drift — 程式碼/文件/設計意圖不一致）

> 審查口徑：程式碼與文件、程式碼與架構設計意圖、DDL 與實做之間的不一致。

---

## F1 〔P3〕`architecture-v0.2.md` 內部對 sqlite-vec 表述不一致

**狀態（2026-07-15）**：✅ 已實施。D9 元件表改為「SQLite + 檔案系統（圖像向量存普通 image_embeddings 表 + C# 暴力餘弦檢索）」；§7 資料流「SQLite + 檔案（圖像向量普通表 + C# 暴力餘弦）」；§8 去掉「`vec_images` 虛表留大規模餘量」。聯動：`docs/api-contract-m1.md` §4 表（含 `image_embeddings` 行）與 §4.1 關係（`→ image_embeddings`）同步修正。
**證據**
- D9 元件表（`architecture-v0.2.md:23`）：`存储后端 | SQLite + 文件系统 + sqlite-vec`。
- 資料流感慨圖（`architecture-v0.2.md:37`）：`接收·校验·落库(SQLite + 文件 + sqlite-vec)`。
- 但同文件（`:126` / `:141`）已正確說明「`vec_images` 虛表預留但未啟用、embedding 存普通 `image_embeddings` BLOB 表、C# 暴力餘弦、不相依 sqlite-vec 原生擴充」。

**影響**：權威設計文件自相矛盾；新人按 D9/§7 會誤以為系統相依 sqlite-vec 擴充。

**修復**：D9 與 §7 資料流刪除 sqlite-vec，統一為「SQLite + 檔案系統 +（預留）C# 暴力餘弦檢索」。

## F2 〔P3〕`schema.sql` 仍含 `vec_images` 虛表 DDL，與「未用 sqlite-vec」實做衝突

**狀態（2026-07-15）**：✅ 已實施（見 02-A1）。`vec_images USING vec0` 死 DDL 已從 `schema.sql` 刪除，`Schema.cs` 移除對應執行環境剝離；`vec_images` 虛表徹底不存在。
**證據**
- `schema/schema.sql:106-111` 聲明 `vec_images USING vec0`；`schema.sql:113-120` 註解自承「M3 實做走普通表 + C# 暴力餘弦」。
- `server/Data/Schema.cs:26` 執行環境 `Where(!Contains("USING vec0"))` 剝離才不崩。

**影響**：死 DDL，與全域結論「未用 sqlite-vec」（README:53/98、AGENTS.md:104）衝突，易被誤讀。

**修復**：見 02-A1（刪 DDL 或顯式標註 + CI 斷言）。

## F3 〔P3〕`schema.sql:122` 註解「判題網頁前端埋點上報」與實作不符

**狀態（2026-07-15）**：✅ 已實施（見 08-T1）。`keystroke_samples` 註解改為「擊鍵節奏(外部判題後端經 KSK 旁路 POST /ingest/keystroke 寫入；前端埋點已撤)」。
**證據**
- `schema.sql:122`：`-- 击键节奏(判题网页前端埋点上报)`。
- 實際寫入路徑：`server/Ingest/KeystrokeIngest.cs`——外部判題後端經 `POST /ingest/keystroke` + `X-Horus-KSig`（KSK 工作階段簽名）寫入；前端埋點已撤（README:34 / AGENTS.md「待做：擊鍵前端埋點已撤」）。

**影響**：註解誤導，讀檔人以為還走前端埋點。

**修復**：改為 `-- 击键节奏(外部判题后端经 KSK 旁路 POST /ingest/keystroke 写入；前端埋点已撤)`。

## F4 〔P2〕M5 「看板健康告警」意圖與實作漂移
**證據**
- 意圖：AGENTS.md:101/104「M5 = 檢測 + 上報 + **看板健康告警**」。
- 實作：`Suspicion.KindFor`（`Suspicion.cs:35-37`）把 M5 三類入 `suspicious_queue`；前端 `KIND_META` 缺標籤（01-U2）→ 監考員看到 raw `screen_obscured` 串，而非可讀健康告警。
- 同時 `suspected_suspend`（`RiskModel=0`）既不入隊也無獨立呈現（05-M8），等於意圖裡的「健康告警」只實作了一半。

**影響**：設計意圖（可讀健康告警）與完成實作（raw 串 + 部分信號靜默）明顯漂移。

**修復**：
1. 短期：補 3 個前端標籤（01-U2），讓健康信號可讀。
2. 中期：按 01-U3 方案區分 `health` vs `os_signal` vs `vision` 來源，落實「健康告警」獨立呈現。

**狀態（2026-07-15）**：✅ 已實施。F4 的兩條修復均隨 01-U2（補標籤）/ 01-U3（選方案 A：`source` 列 + 獨立「採集健康」面板）完成實作。`suspected_suspend`（05-M8）仍按原設計 `RiskModel=0` 僅作健康提示、不入隊，本次未改（屬另一待辦，非本決策範圍）。

## F5 〔P3〕`README.md:34` 稱「225 項測試全綠」需與當前實際對齊

**狀態（2026-07-15）**：✅ 已實施。`README.md` 狀態行改為「測試持續全綠·見 dotnet test 實際輸出」；`server/README.md` 的測試說明改為「持續全綠·見 CI 輸出」，徹底去掉硬編碼計數（隨測試增長自動對齊）。
**證據**：README / AGENTS.md 多處寫「225 項測試全綠」「測試數 130→…」。隨 M3/M4/M5 增改，測試數已變化，建議在 CI 或文件裡用變數/動態數字，避免硬編碼過時計數漂移至失真。

**修復**：文件改為「測試持續全綠（見 CI 報告）」或引用 `dotnet test` 實際輸出，不硬編碼具體數字。

---

## 優先級建議
1. F4（P2，意圖漂移，隨 01-U2/U3 修）。
2. F1 / F2 / F3（P3，文件與 DDL 漂移，順手清）。
3. F5（P3，數字去硬編碼）。
