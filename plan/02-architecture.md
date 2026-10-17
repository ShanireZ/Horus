# 維度二 · 架構審查（Architecture）

> 審查物件：`Horus.sln` 下分層（`Horus.Contracts` / `Horus.Agent.Core` / `Horus.Agent` / `Horus.Server` / `tests`）、資料流、DB 訪問、`server/Program.cs` 裝配。
> 審查口徑：分層合理性、耦合度、可擴充性、契約一致性。

---

## 總體評價（正面）
- **分層清晰、職責單一**：`Contracts` 承載共享線協定 / HMAC / 規範化（位元組級一致，避免雙端漂移）；`Agent.Core` 平台無關（傳輸 / 緩衝 / 重連），`Agent` 僅做 Windows 採集；`Server` 專注 ingest / 分析 / 看板 / 歸檔。層級邊界明確，未見跨層反向相依。
- **契約位元組級共享**：`Crypto` / `EventCanonical`（`contracts/Crypto.cs`）雙端共用同一實作，hash 鏈（`hashPrev→hashSelf`）與簽名可逐位元組複驗——這是 M3 完整性複驗能成立的根基，設計正確。
- **DB 讀寫分離合理**：單寫連接（寫鎖串行）+ 獨立唯讀連接池（WAL 並行讀），看板 6 個 GET 走唯讀連接與寫路徑互不阻塞（AGENTS.md:66）。規模天花板設計務實。

---

## A1 〔P3〕`schema.sql` 仍聲明 `vec_images` sqlite-vec 虛擬表（誤導死程式碼）

**狀態（2026-07-15）**：✅ 已實施。已從 `schema/schema.sql` 徹底刪除 `vec_images USING vec0` 死 DDL；`server/Data/Schema.cs` 的 `Apply()` 移除 `.Where(!Contains("USING vec0"))` 執行環境剝離（已無此語句可剝）；`tests/ReliabilityTests.cs` 的 vec0 斷言註解同步更新為「`vec_images` 已從 schema 移除，不相依 sqlite-vec」。

**證據**
- `schema/schema.sql:106-111` 仍聲明 `CREATE VIRTUAL TABLE IF NOT EXISTS vec_images USING vec0(...)`。
- 實做已改為普通 `image_embeddings` BLOB 表 + C# 暴力餘弦（schema.sql:113-120 註解已說明「M3 實做·C# 暴力餘弦·不相依 sqlite-vec」；README:53/98、AGENTS.md:104 一致）。
- `server/Data/Schema.cs:26` 在 `Apply` 時 `.Where(st => !st.Contains("USING vec0"))` **執行環境剝離**該語句才不崩潰。

**影響**
該虛表當前是「預留但未啟用」的死 DDL；由於靠 `Schema.cs` 剝離才不回報錯誤，屬於易被忽視的誤導程式碼，新人會誤以為系統真在用 sqlite-vec（與「未用 sqlite-vec」的全域結論衝突，見 07-漂移）。

**修復方案**
- 推薦：直接從 `schema.sql` 刪除 `vec_images` 虛表 DDL，以 `image_embeddings` 為唯一真相。
- 若堅持「預留大規模檢索餘量」：改為顯式註解 `/* 預留·永不啟用·Schema.Apply 會跳過 USING vec0 */`，並加一條 CI 斷言確認 `USING vec0` 語句被跳過、且執行環境不建該虛表。

---

## A2 〔P3〕API 契約層缺乏獨立測試，迴歸無防護

**狀態（2026-07-15）**：✅ 已實施。新增 `tests/EndpointsTests.cs`（7 項斷言）：無 token→401、authmode 形態（psk + imageSearchEnabled）、建立考試入列表、decide 狀態機（confirmed + 誤值→400）、health 項裁決→400、`/health` 與 `/suspicious` 分離、search-image 形態。全測試 265 項全綠（原 227）。

**證據**
- `server/Api/Endpoints.cs` 約 761 行、~20 個端點（login / authmode / exams CRUD / suspicious decide / capture / archive run / search-image / preflight 等）。
- `tests/` 下 16 個測試檔案，但 `Endpoints` 關鍵字命中 **0 檔案**（其餘覆蓋 ingest / integrity / archive / oidc / image-search / hardening / reliability 等）。`TestApp.cs` 可能提供部分集成覆蓋，但無針對契約形狀/狀態碼的聚焦測試。

**影響**
契約一旦改動（欄位增刪、狀態碼、鑑權門），看板/採集端可能在無測試報警的情況下失配。屬於架構層面的「可測試性/可擴充性」缺口。

**修復方案**
- 新增 `EndpointsTests.cs`：用 `TestApp` 起服務 + `HttpClient` 跑關鍵路徑斷言（login 401→200、authmode 形狀、exams 列表、suspicious decide 狀態機、search-image 形狀、capture `pushed` 語義）。至少覆蓋「鑑權門 + 關鍵讀寫 + 錯誤碼」。
- 將契約快照（請求/回應 JSON 形狀）納入測試，作為迴歸基線。

---

## A3 〔P3〕`capture_now` 採集端 handler 缺啟動期斷言（防禦性）

**狀態（2026-07-15）**：✅ 已實施。`agent/Program.cs` 注入 `OnCaptureNow` 後加啟動斷言：未注入則 `Console.Error` 告警「capture_now 處理器未注入 → 點名抓圖將靜默失效」；`UplinkClient.OnCaptureNow` 欄位註解同步點明靜默失效風險（見 08-T6）。

**證據**
- `UplinkClient.OnCaptureNow`（`agentcore/Transport/UplinkClient.cs:33`）為可空 `Action<string>?`；`agent/Program.cs:153` 注入。若未來初始化順序變化導致未注入，伺服器端 `pushed:true` 但 Agent 不抓圖，形成「靜默失效」。

**影響**
屬潛在架構脆弱點（非已確認 bug），部署方難排查。

**修復方案**
- 在 Agent 啟動完成自檢中斷言 `uplink.OnCaptureNow != null`，否則紀錄告警（甚至 fail-fast，取決於部署容許度）。

---

## 優先級建議
1. A1（P3，刪死程式碼 / 加註解 + CI 斷言）→ 順手清。
2. A2（P3，補契約測試）→ 與 05-缺失 的測試缺口一併規劃。
3. A3（P3，防禦性斷言）→ 低優先。
