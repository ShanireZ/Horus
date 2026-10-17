---
type: audit
title: Horus 程式碼審查報告（2026-07-21）
---

# Horus 程式碼審查報告（2026-07-21）

審查範圍：`contracts/`(Horus.Contracts)、`agentcore/`(Horus.Agent.Core)、`agent/`(Horus.Agent)、`server/`(Horus.Server) 全部原始碼（約 80 個 .cs 檔案，排除 obj/ 生成物）。

總體結論：專案經過多輪獨立安全/邏輯/效能稽核（225 測試全綠、M1–M5 + 身分層 + 歸檔均已實作），工程品質很高，核心安全不變數（雜湊鏈、HMAC 驗籤、canonical 兩端逐位元組一致、fail-closed、讀寫分離、斷網續傳）完成實作紮實。以下為本次審查發現的剩餘問題，按嚴重程度分級。

---

## 一、BUG / 邏輯錯誤

### [中] 1. `ImageIngest` 缺少請求體大小上限，且 body 在驗籤/鑑權前全量讀入記憶體
**檔案**：`server/Ingest/ImageIngest.cs`（HandleAsync，約 41–52 行）
- 圖片通道把 `req.Body` 完整讀入 `MemoryStream`（`CopyToAsync`）後才做 PSK/OIDC 驗籤。
- 全程**沒有**像 `KeystrokeIngest` 那樣的 `MaxBodyBytes` 顯式上限（後者有聲明 `Content-Length` 檢查 + 實際 `ms.Length` 檢查）。
- 備援僅靠 `Program.cs:172` 的 Kestrel 全域 `MaxRequestBodySize = 2MB`。這意味著：
  - 未鑑權者（LAN 內任意能觸達端點的機器）每次可強制伺服器緩衝最多 2MB；
  - 若將來全域上限被調大，此處沒有任何獨立熔斷。
- **建議**：在讀取 body 前先查 `Content-Length` 並拒絕超限（與 `KeystrokeIngest` 一致），把鑑權失敗/超限的判定前移，降低記憶體壓力與放大攻擊面。

### [低] 2. `ParseConfidence` 對 `1` / `1.0` 的邊界歧義
**檔案**：`server/Analysis/Vision/OpenAiCompatibleVisionAnalyzer.cs:140-142`
```csharp
if (d > 0 && d < 1) d *= 100;
```
- 若視覺模型以 0–1 概率回傳 `1.0`（意圖 100%），因 `d < 1` 為 false → 保持 `1` → 被鉗成 1 分。
- 後果：`confidence:1` 且 `suspicious:true` 的圖，因 `1 < cfg.VisionConfidenceThreshold(默认 50)` 而**不進可疑佇列、也不標證據**。
- 屬文件 §F9 已知權衡（刻意不把整數 `1` 放大成 100），但 `1.0` 這一浮點邊界不魯棒。
- **建議**：prompt 已要求 `0-100`，可在解析層對 `== 1.0` 顯式歸為"整數原樣"（即保持 1）或要求供應商嚴格 0–100；並在真機 smoke 裡確認 MiMo 實際回傳格式。

### [中] 3. `BrowserUrlSource` 位址欄讀取脆弱且低效（對抗網頁 AI 的第一防線）
**檔案**：`agent/Signals/BrowserUrlSource.cs:81-102`（含自身 TODO 註解）
- 每 2s 對前台瀏覽器視窗 `root.FindAll(TreeScope.Descendants, Edit 条件)` **全樹遍歷**，取第一個"像 URL"的 `Edit` 控件值。
- 問題：
  1. **可靠性**：未區分瀏覽器工具欄結構，多標籤 / 位址欄結構變化 / 隱身模式可能讀錯或讀不到 → 觸發"url_unreadable"降級（僅強制抓圖，靠人工看），削弱第一防線。
  2. **效能**：每次 poll 全樹 UIA 遍歷，CPU 開銷隨視窗複雜度上升。
- 程式碼已自承 TODO："生產應按瀏覽器快取 AutomationElement / 用更窄的條件(toolbar→edit)"。
- **建議**：按瀏覽器類型快取關鍵 `AutomationElement`、用 `Condition` 收窄到位址欄（toolbar 內的 edit），或在 UIAutomation 不可用時有更明確的降級策略。這是"檢測取證"體系裡權重最高的一條信號，值得優先加固。

### [中] 4. 多顯示器盲區（實作層面未覆蓋第二屏）
**檔案**：`agent/Capture/ScreenshotCapturer.cs:64-72`（含 TODO 註解）
- `GrabPrimaryScreen()` 只抓主顯示器。第二顯示器上的網頁 AI / 遠程協助工具 / IDE 外掛**完全漏採**。
- 架構 §11 已誠實標註"Agent 未覆蓋的多屏是結構盲區"，但實作上仍是待補功能。
- **建議**：實作 `Screen.AllScreens` 遍歷各屏各抓一張（注意基線/觸發抓圖的 seq 與去重邏輯要隨之擴充），並配合"遮蔽檢測"覆蓋 DRM 保護視窗。

### [低] 5. `POST /api/exams` 空 seatId 可被插入
**檔案**：`server/Api/Endpoints.cs:569-603`
- 檢核邏輯：`if (sid.Length > 0 && !IsSafeId(sid)) return BadRequest` —— 僅當 `sid` 非空且非法時拒絕。
- 但插入用 `(string?)sn["seatId"] ?? ""`：空 `seatId` 通過檢核、被原樣插入 `seats(seat_id="")`。
- **建議**：顯式拒絕空 `seatId`（與 `examId` 同等級檢核）。屬資料品質問題，不致命但會汙染看板/席位關聯。

### [低] 6. `Envelope.Serialize` 重複輸出 `seq`
**檔案**：`contracts/Wire.cs:143-154`
- 信封同時序列化 `seq = e.Seq`（外層）與 `@event = e`（內層含 `seq`）。兩份 `seq` 值相同、層級不同，無害但冗餘，且人為增加每影格體積。
- **建議**：外層 `seq` 用於快速路由、內層 `seq` 用於驗籤，可考慮去掉內層 `seq`（伺服器已從 `hashSelf` 復算），或反之。屬可讀性/體積優化，非功能 bug。

---

## 二、功能待完善 / 部分實作

以下內容在架構文件中已標註"待 owner 真機驗收 / 灰度驗收"，列於此供跟蹤：

1. **M5 Windows 專屬硬化待真機驗收**：`Watchdog` / `WindowsService` / `SessionLauncher` 中真實 suspend / obscure / 降權檢測路徑，文件 §15 標註"待 owner 真機驗收"。
2. **`both→oidc` 灰度現場驗收**：架構 §15 標註為待辦；`/api/preflight` 已能報告"仍有 N 個座位走 PSK"作為 go/no-go 信號。
3. **`ClipboardWatcher` 乾淨關停**：`agent/Signals/ClipboardWatcher.cs` TODO 註明相依處理程序退出回收，未實作訊息泵乾淨退出（`Application.ExitThread`）。
4. **`ForegroundWindowSource` 事件驅動化**：TODO 建議用 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 替代輪詢降開銷（需訊息循環）。
5. **`ProcessWatcher` 改用 ETW**：TODO 註明低延遲可走 ETW；當前相依 `Process.Start` 事件，`CommandLine` 需管理員權限才非空（影響遠控工具命令行特徵提取）。
6. **多顯示器抓屏**：見 BUG#4。
7. **擊鍵前端埋點已移除（N/A）**：判題走外部洛谷、無本機判題頁可埋點；由 Agent OS 級剪貼板/粘貼檢測覆蓋（架構 §6），屬設計決策而非遺漏。

---

## 三、程式碼品質

1. **Agent 端紀錄用 `Console.WriteLine/Console.Error`** 而非注入 `ILogger`（`agent/Program.cs`、`agent/Signals/*` 等），與 Server 端結構化紀錄不一致，難統一收集/分級。（影響可維護性，非缺陷）
2. **風險分魔法數散落多處**：`40/70/80/60/55/50` 等出現在 `RiskModel.Derive`、`Suspicion`、Agent 的 `BrowserUrlSource`(80/40)、`Program.cs`(M5 健康分 60/55)、`KeystrokeIngest.RiskFrom`(70/60/55)。Agent 自報分與伺服器復判分各自的"真相來源"易漂移，建議集中為常數/設定。
3. **`Endpoints.MapApi` 單方法約 700 行**：可讀性差，建議按資源（exams / seats / suspicious / images / archive / auth）拆分成多個映射方法或 partial 類。
4. **`RiskModel` 與 `Suspicion` 的"信號類型→風險/類別"雙套邏輯靠註解同步**：新增 `SignalType` 時需在三處（枚舉、`RiskModel.Derive`、`Suspicion.KindFor`）同步，`Suspicion.cs` 頂部已用總表約束，但仍是手動。建議用單一 `SignalType → (risk, kind, source)` 映射表驅動。
5. **`ImageIngest` 與 `KeystrokeIngest` 的 body 讀取 + 大小檢核 + 鑑權順序高度重複**：可抽成共享中介軟體/helper，避免兩路安全邏輯日後分叉（BUG#1 正是此類分叉的體現）。
6. **`async void Handle` 在 `agent/Program.cs`**：作為事件回呼可接受（已自捕獲異常），但 fire-and-forget 的異常棧在診斷時不直觀，建議在關鍵路徑加 `TaskScheduler.UnobservedTaskException` 備援或顯式紀錄。

---

## 四、效能待優化

1. **`VisionAnalysisService` 單 reader 串行（`SingleReader = true`）**：逐張送視覺 LLM，單場數千張圖時吞吐受限（網路 IO 串行）。當前按"不壓視覺端點/不佔 ingest 熱路徑"保守設計，可接受；若單場量大，可考慮受限並行（Channel 限流 2–4）提速。
2. **`/api/exams/{id}/seats` 每行含多個相關子查詢**：雖整條是單 SQL，但 SQLite 對每行執行內部子查詢（座位數 × ~6 子查詢）。30 座位量級可接受（<200 次查詢），更大規模可改 JOIN + 視窗函式一次性聚合。
3. **`IntegrityAudit.Run` 一次性拉全考試事件進記憶體**：按 agent 分組、seq 升序處理，極大考試（數千事件）記憶體 O(N)。當前規模 OK；超大規模可改為流式/分批。
4. **`search-image` 每次把整場 embedding 全讀進記憶體再暴力餘弦**（`ImageSearchStore.GetExam`）：當前單場幾千張 × 512 維，OK；若上量可考慮分頁或常駐記憶體索引（已明確不引 sqlite-vec）。
5. **`Db` 唯讀連接池固定 4**：完整性稽核（全考試 SHA256）/ 歸檔 copy 讀與看板 5s 輪詢並行時可能排隊；文件已說明為後續優化，當前不阻塞正確性。

---

## 五、已確認正確 / 無問題（抽樣核對）

- **canonical 兩端一致**：`EventCanonical.Core`(typed) 與 `CoreRaw`(raw text) 欄位順序、類型、null 處理一致；伺服器 `VerifyHashSelf` 能錨定 payload。由 `CanonicalTests` 鎖定的黃金測試覆蓋。
- **斷網續傳 / seq 高水位**：`LocalBuffer` + `UplinkClient.NextSeq/AlignSeq` 設計正確，無 seq 重複使用；`RecoverCrashedRewrite`/`CleanupOrphans` 崩潰恢復完備。
- **讀寫分離**：`Db` 單寫連接 + 唯讀連接池 + `:memory:` 退回正確。
- **OIDC 驗籤**：`OidcTokenValidator` 驗 alg/kid/簽名/iss/aud/exp/nbf/nonce，角色 fail-safe 到 `disciple`，正確。
- **歸檔冪等/崩潰安全**：門禁 + 墓碑 + 複製 + 清理順序正確，`MoveToArchive` 與 `DeleteLive` 冪等。
- **管理面鑑權**：HttpOnly + SameSite=Strict cookie、FixedTimeEquals、CSP/nosniff/X-Frame-Options/Referrer-Policy 齊備。

---

## 優先級建議

| 優先級 | 項 |
|---|---|
| P1（建議本迭代修） | BUG#1（ImageIngest body 上限）、BUG#3（BrowserUrl 讀取加固） |
| P2 | BUG#4（多顯示器）、BUG#2（信賴度邊界）、程式碼品質#2/#5（風險常數集中、ingest helper 去重） |
| P3 | BUG#5（空 seatId）、BUG#6、其餘效能/可讀性項、第二節待辦項（按 owner 真機驗收排期） |
