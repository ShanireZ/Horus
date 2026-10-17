---
type: architecture
title: 監考系統架構設計文件（v0.2 · 完整版）
---

# 監考系統架構設計文件（v0.2 · 完整版）

- 專案：**Horus** — 本機區域網路考試監考系統
- 版本：v0.2（取代 v0.1）
- 日期：2026-06-28（實作進度更新至 2026-07-03）
- 狀態：**M1–M5 + 考試派發均已實作·225 測試全綠**（Windows 專屬採集端硬化待 owner 真機驗收，見 §15）
- 關聯：[api-contract-m1.md](api-contract-m1.md) · [../schema/schema.sql](../schema/schema.sql) · [../schema/schema-archive.sql](../schema/schema-archive.sql) · [../agent/](../agent/)

> 場景：本機區域網路內的編程 / OJ 考試。學員**本機 IDE 寫 C++ + 網頁判題**提交；伺服器為區域網路內 1+ 檯筆記本；考試時長 2h。
> 哲學：**純檢測 + 取證**（預防層已確認不納入）；**中繼資料優先、圖像為輔**；**系統只初篩、人工裁決**。

## 0. 已鎖定決策

| 編號 | 決策 | 選定 |
|---|---|---|
| D1 | 答題環境 | 網頁判題 + 本機 IDE |
| D2 | 網路白名單預防層 | **不納入**（控不了考場網路） |
| D2′ | 主機級防火牆預防 | **不納入**（純檢測取證） |
| D3 | Agent 技術棧 | C#/.NET 單檔案 exe |
| D3′ | 伺服器技術棧 | **C#/.NET 8 · ASP.NET Core**（minimal API + WebSocket）+ Microsoft.Data.Sqlite。與 Agent 同棧,經 `Horus.Contracts` 共享契約 / HMAC canonical,雜湊簽名兩端逐位元組一致 |
| D4 | 圖像分析位置 | 伺服器集中 + **視覺 LLM(識圖)** —— 取代專用雲 OCR + L3 Logo,合併單一視覺級(2026-07-01 owner 拍板,詳見 §4)。provider-agnostic;**已選定並真機驗證 = 小米 MiMo-V2.5 託管 API**(`token-plan-cn.xiaomimimo.com/v1`·`model=mimo-v2.5`·OpenAI 相容·**境內雲**·PIPL 無跨境);key DPAPI 加密儲存不存明文 |
| D8 | 截圖參數 | **1080p · WebP q75 · 隨機 30–90s** |
| D9 | 儲存後端 | SQLite + 檔案系統（圖像向量存普通 image_embeddings 表 + C# 暴力餘弦檢索） |
| — | 留存 | **30 天**，到期關鍵資料轉 archive，其餘清理 |
| D10 | P147/P149 主域遷移 | owner 已批准本機順序實施與最終獨立評審：BetaPass issuer 為 pass.betaoi.cc，公開主頁/下載入口 hr.betaoi.cc；cn 休眠，LAN 與原生動態 loopback 串接保持 |

### 0.1 Web Platform Baseline 合同

- 監考看板執行環境為 `controlled-web`，Newly 是開發能力上限；LAN 工作站合同為 Chrome / Edge 當前及前一主版本，不覆蓋公用網路 downstream。
- 該移動視窗在 2026-08-24 的批准快照為 Chrome / Edge 150–151；實際複核機版本與 .NET SDK 一併記錄在根 `baseline.config.json`，版本視窗升級不得靜默漂移。
- 看板是 ASP.NET Core 直接提供的原生 HTML/CSS/JavaScript，沒有轉譯或打包階段，因此建置目標標記為 `not-applicable`，不以一份虛假的 Browserslist 設定冒充相容性保證。
- 引入 Newly 能力時必須在 `baseline.config.json` 登記檢測與退回的原始碼標記，且註解中的標記不算活動實作；關鍵登入、座位刷新、複核、證據燈箱和考試控制不得靜默降級。
- `WebBaselineContractTests` 檢查策略、原生資源和已監視現代 API；開考前仍須在實際工作站完成瀏覽器預檢。

### 0.2 P147/P149 網域遷移邊界（2026-10-07）

owner 原話“所有betapass相關專案，以betaoi.cc作為主域，betaoi.cn作為備用（且暫時不啟動該域相關設定，以後備案通過後再啟用）”，並選擇“本工作階段順序實施，最終獨立評審”。本輪只改內建 issuer、當前說明及本儲存庫擁有的公開靜態站台片段；Server 仍顯式設定 issuer，Agent 範例繼承內建值。動態 loopback、現場 LAN/看板 HTTPS 回跳、兩用戶端、PS256、openid profile、userinfo 身分來源、用途受眾與 P101 不主動入站拓撲保持。公共下載頁不添加演練門，共享 Caddy 主設定歸 betai；Round1 和公共資產域保持。

本機測試及合成驗收不證明現場登入或已發佈。具體生產協調切換、新 Windows 產物和公開下載發佈另按固定候選審閱；不改設備私有設定，不傳送電子郵件，不開啟 cn。issuer 切換後重新登入；恢復使用已驗 cc 組合，資料只前滾，不回裝舊 cn 整套。公開下載連結目前對應歷史建置，遷移發佈前須固定新版與摘要並驗實際下載，不能只換網頁網域。進度在 status，本節只登記決策。

## 1. 總體架構

```
 ┌──────────── 考试机 Agent（C#/.NET，每台一个）─────────────┐
 │ 采集: 窗口/进程/多浏览器URL/剪贴板/切窗/USB                   │
 │       + 截图(事件触发 / 随机30–90s基线)                       │
 │ 本地: pHash去重 · WebP压缩 · 风险初判 · 哈希链 · 断网缓冲      │
 └───────────────┬──────────────────────────────────────────┘
                 │ WebSocket(事件实时) + HTTP分块(图片) · HMAC签名
                 ▼
 ┌──────────── 监考服务器（笔记本，1+ 台）─────────────────────┐
 │ 接收·校验·落库(SQLite + 文件（图像向量普通表 + C# 暴力余弦）)                    │
 │ 分析: L1 OS信号(本地·server_risk 独立复判)                    │
 │            │ L2 视觉 LLM 识图(取代 OCR+Logo·合并单一视觉级)    │
 │            ▼                                                 │
 │   ┌─────────────────────────────┐                           │
 │   │ 视觉 LLM 端点 ◀── 唯一出网    │  ← 降采样+剥元数据/最小化上传 │
 │   │ (境内云 MiMo / 或本地自托管)  │     原图永不出网,只送派生图    │
 │   └─────────────────────────────┘                           │
 │ 风险聚合(有效风险=max) → 可疑事件队列                          │
 └───────────────┬──────────────────────────────────────────┘
                 ▼
 ┌──────────── 监考端 / 复核台（Web 看板）──────────────────────┐
 │ 实时: 座位热力图·告警弹窗·缩略图   复核: 队列·证据图+时间线·按图搜图 │
 └────────────────────────────────────────────────────────────┘
```

**注意**：除 L2 視覺 LLM 識圖（走雲端點時）外，所有資料（中繼資料、截圖原圖、向量、看板）**全程不出區域網路**。

## 2. 採集端 Agent（C#/.NET）

### 2.1 中繼資料信號（L1，零圖像，幾十~幾百位元組/條）

| 信號 | 來源 | 抓什麼 |
|---|---|---|
| 前台視窗標題 + 處理程序名 | UIAutomation / `GetForegroundWindow` | 當前在用什麼 |
| **多瀏覽器位址欄 URL** | UIAutomation 無障礙介面（Chrome/Edge/Firefox） | **訪問哪個站——本系統對抗網頁 AI 的第一防線** |
| 處理程序啟動/退出 | ETW / WMI | 起沒起非白名單程式、遠程協助工具 |
| 剪貼簿事件 + 內容長度 | 剪貼簿監聽 | 大段粘貼 |
| Alt-Tab / 切窗頻率 | 鉤子 | 頻繁切出 IDE |
| USB 插入 | 設備通知（WMI） | 外接設備 |
| 心跳 | 定時 | Agent 存活 / 防卸載 |

> **因預防層為零，URL 監控是重中之重**：判題站本身在瀏覽器裡合法開著，所以判定要落到 **URL 級別**——判題站網域走白名單，瀏覽器裡出現**任何其它 URL**（AI 站、搜尋引擎、貼板）即高風險。必須覆蓋主流瀏覽器，並對"隱身模式 / 冷門瀏覽器抓不到 URL"的情況降級為"處理程序級瀏覽器告警 + 強制抓圖"。

### 2.2 截圖策略
- **事件觸發**：非白名單處理程序啟動、瀏覽器出現非判題 URL、大段粘貼、切窗爆發、USB 插入 → 立刻 1 張。
- **隨機基線**：每 **[30s, 90s]** 均勻隨機 1 張。**專抓 IDE AI 外掛**——Copilot / Continue 等補全不產生處理程序 / 網路 / URL 事件，唯一證據就是螢幕上"憑空出現整段程式碼"，只能靠定期看屏留底。
- **pHash 去重（保守）**：dHash 漢明距離 ≤3 視為重複，僅丟"短時間多次觸發、畫面幾乎不變"的近似圖。**隨機基線層不去重**（每張都可能是孤證）；閾值刻意敏感，防止"只多幾行補全"的關鍵圖被誤刪。

### 2.3 本機處理
**1080p / WebP q75（≈150KB/張）** → pHash → 風險初判打分 → 雜湊鏈封裝 → 斷網時本機緩衝，恢復後按 seq 續傳去重。

## 3. 資料模型（概要，權威定義見 api-contract-m1.md）

```jsonc
// 事件流（WebSocket 上報）
{ "ts":..., "examId":"...", "seatId":"A07", "agentId":"...", "machineId":"...",
  "type":"window_focus|browser_url|process_start|process_exit|clipboard|alt_tab_burst|usb|screenshot|heartbeat",
  "payload":{...}, "risk":0,                        // 本機初判 0–100
  "evidenceImageId":"...|null",
  "hashPrev":"...", "hashSelf":"...", "seq":12345 } // 雜湊鏈 + 單調序號
// 截圖記錄（HTTP 上傳，原圖存檔案系統）
{ "imageId":"...", "seatId":"A07",
  "trigger":"event:browser|event:paste|baseline_random",
  "phash":"...", "file":"...webp", "w":1920, "h":1080,
  "uploadedToOcr":true, "ocrHits":["chatgpt"], "logoHits":["google"] }
```

## 4. 分析流水線（系統只初篩）

| 級 | 位置 | 做什麼 | 備註 |
|---|---|---|---|
| **L1 OS 信號** | Agent 本機 + **伺服器復判** | 視窗標題 / URL 正則匹配黑名單（`chat.openai`/`doubao`/`deepseek`/`google`/`baidu`/`bing`…）。伺服器側 `RiskModel` **獨立復判**不採信 Agent 自報(M2 已實作,見 §10.1) | 零圖像、最準最便宜 |
| **L2 視覺 LLM（識圖）** | 伺服器 → **視覺 LLM 端點** | **一次「看懂畫面」同時做:文字提取 + AI 對話介面/搜尋頁/IDE 幽靈補全/遠控工具識別 + 分類**,直出 `{suspicious,category,confidence,evidence}`。取代專用 OCR + L3 Logo(合併單一視覺級) | provider-agnostic(OpenAI 相容:DeepSeek-V4/MiMo-V2.5/Qwen-VL/GLM-4V);**雲端點=唯一出網(見 §5)**,或**本機自託管 MiMo(vLLM)= 零出網** |

> **為何視覺 LLM 取代 OCR(2026-07-01 owner 拍板)**:任務本質是「看懂畫面」(認出 AI 介面/搜尋頁/IDE 幽靈補全)而非純文字識別 —— 視覺 LLM 是 OCR 的超集,一次呼叫頂原 L2(OCR)+ L3(Logo),且結構化輸出直接喂風險聚合 + 人話證據(契合「只初篩、人工裁決」)。取捨:每圖成本高於專用 OCR(§5 最小化 + 只送觸發型壓量);畫面攻擊者可控 → 提示注入靠「只作一條線索 + 人工裁決」緩解。

各級命中累加**風險分**(有效風險 = max(Agent 自報, 伺服器 L1 復判, 視覺 LLM 置信)) → 超閾值進**可疑事件佇列** → 人工複核裁決。

## 5. L2 視覺 LLM 資料流與隱私收口（唯一出網，重點）

L2 現為**視覺 LLM 識圖**(取代 OCR,見 §4)。走**雲端點**時這是全系統唯一離開區域網路的環節,按下列約束收口;走**本機自託管**(開放原始碼 MiMo + vLLM)時則**零出網**、收口第 4/5 條不適用。**✅ 派生處理已實作**(`server/Analysis/Vision/VisionImagePrep.cs`):送雲前對截圖做**降取樣**(`visionMaxEdge` 長邊上限,預設 1600·壓 token/少送無關像素)+ **剝離中繼資料**(EXIF/XMP/IPTC/ICC 不隨派生圖出網),再重編碼 WebP;**原圖位元組唯讀、原圖永不出網**,送雲的只有派生圖。`visionMaxEdge≤0` 則直通原位元組。
> **打碼/裁剪已移除(owner 決策 2026-07-02)**:原設計的"打碼身分(visionRedactRects)+ 裁剪(visionCropRect)"需按各考場 UI 逐一配矩形,維運負擔 > 收益;且供應商已定為**小米 MiMo 境內雲**(PIPL 無跨境)、考前已告知學員"截圖可能經外部處理"。故只保留解析度無關、免設定的降取樣,不再做打碼/裁剪。若未來改用無告知場景或跨境供應商,應重新評估身分最小化。

1. **最小化上傳**：不是每張截圖都送雲。先經 L1 本機初篩 + pHash 去重，**只把"需要視覺判定"的圖**（命中可疑信號、或基線層抽樣）送雲，壓低數量與成本。
2. **降取樣**：送雲前按 `visionMaxEdge`（長邊上限·預設 1600）縮小解析度，壓 token、少送無關像素；`≤0` 則直通原位元組。
3. **剝離中繼資料**：派生圖重編碼 WebP 時不帶 EXIF / XMP / IPTC / ICC，中繼資料不隨圖出網。
4. **無留存供應商**：選支援"不留存請求資料"的視覺端點，並在合規說明裡寫明；供應商=小米 MiMo 境內雲（PIPL 無跨境）。
5. **傳輸加密 + 合規告知**：HTTPS；考前告知學員"截圖可能經外部處理"，與供應商簽資料處理協定。
6. **原圖永不出網**：證據原圖只存在區域網路伺服器（原圖位元組唯讀），雲端只收到**降取樣 + 剝中繼資料後的派生圖**。

> 成本提示：每場約 4500 張截圖，若全量送雲約 $5–7/場；上面的"最小化上傳 + 只送觸發型"能再砍一大截。

## 6. 編程考專項：大段粘貼 + 擊鍵節奏
- **大段粘貼**：Agent 監聽剪貼簿，記錄粘貼長度 + 是否粘入 IDE / 答題區；超閾值即高風險並觸發抓圖。**預設只記中繼資料（長度 / 行數），不上傳明文內容**；命中後按策略可留雜湊或截斷樣本。
- **擊鍵節奏**（**⚠️ 前端埋點無落點·2026-07-03 更正**）：原設計假設"判題網頁前端埋點"，但**實際判題走外部洛谷(luogu.com.cn) OJ**（學員本機 IDE 寫碼 → 提交洛谷 → wentian 爬洛谷同步通過題數,見 `WenTian/server/luoguSync.js`）——**洛谷是外部站,無法埋點**。故擊鍵前端節奏採集**不可實作**。**實際替代信號**：Agent 的 **OS 級剪貼簿/粘貼檢測**（`ClipboardWatcher` + `大段粘贴`,已實作）覆蓋"粘貼外部寫好的程式碼"這一核心信號,不相依判題頁。**伺服器擊鍵旁路 `/ingest/keystroke`(KSK 驗簽+歸檔) 保留**:若日後出現自控的本機提交頁,可即接。

## 7. 儲存後端與估算

**SQLite（事件 + `image_embeddings` BLOB 表存 embedding·**C# 暴力餘弦檢索**·不相依 sqlite-vec 原生擴充） + 檔案系統（圖片，按 exam/seat 分目錄）。**

| 資料（30 人 × 2h） | 單人 | 全場 |
|---|---|---|
| 中繼資料事件 | <1 MB | ~30 MB |
| 截圖（~150 張 ×150KB，1080p WebP q75） | ~20–30 MB | ~0.7–1 GB |
| embedding（150×512×fp16） | ~0.15 MB | ~5 MB |
| **合計** | **~25–35 MB** | **~1 GB** |

對比全程錄屏（每人數 GB）仍是數量級節省；1TB 筆記本可存約 1000 場。

## 8. 向量化與檢索（明確邊界）
- **CLIP embedding = 檢索索引，不是儲存替代**。用途：按圖搜圖（選一張已知作弊圖撈全場相似畫面）、聚類。
- **不可逆**：~1KB 向量無法還原百萬像素截圖（資訊論硬約束）。**證據永遠是 WebP 原圖**，embedding 只是找到它的鑰匙。
- pHash 管"近似影格去重"，CLIP 管"語義相似檢索"，分工不重疊。
- **✅ M3 已實作（2026-07-03·owner 拍板）**：**provider-agnostic 圖像嵌入器**（`IImageEmbedder`）；**規模小(單場幾千圖)→ C# 暴力餘弦 KNN,不相依 sqlite-vec 原生擴充**（embedding 存普通 `image_embeddings` BLOB 表）；**僅嵌證據圖**(is_evidence·省算力·後台 `ImageEmbedService` 補掃)；`POST /api/exams/{id}/search-image` 暴力餘弦 top-N + 看板燈箱「按圖搜圖」。
  - **嵌入源（★探測後定案）**：`probe-embed` 診斷實測 **小米 MiMo-V2.5 無 `/v1/embeddings` 端點**(404·模型僅 chat/vision/asr/tts)——"多模態能看圖對話 ≠ 有 embeddings 端點",重複使用視覺 key **走不通**。故 owner 拍板走 **本機 ONNX CLIP**(`OnnxClipEmbedder`·`Microsoft.ML.OnnxRuntime` CPU·**零出網·無跨境·無需 GPU**·CLIP ViT-B/32 圖像編碼器 ONNX 512 維·模型部署提供)。`ClipPreprocess`(resize+center-crop 224²+CLIP 歸一化)與推理解耦可單測。
  - **其它嵌入器(備)**：`MockImageEmbedder`(確定性·測試)、`OpenAiImageEmbedder`(OpenAI 相容 `/v1/embeddings`·重複使用視覺 key·**僅當某雲端點確有圖像 embedding 模型時**)。同一 `IImageEmbedder` 介面可切。
  - **部署**:`embedProvider=onnx` + 模型放 `dataDir\model.onnx`(★約定檔案名 = `model.onnx`,`embedOnnxModelPath` 留空即預設;推薦 HF `Qdrant/clip-ViT-B-32-vision` 原名即 model.onnx,Xenova 的 vision_model.onnx 下載後改名);啟動紀錄「ONNX CLIP 嵌入器就緒」即成。

## 9. 傳輸與可靠性
- 事件走 **WebSocket** 即時推（看板要即時）；圖片走 **HTTP 分塊 POST**。
- **斷網續傳**：Agent 本機緩衝 + 序號，恢復後補傳、按 seq 去重。
- **多伺服器**：單機起步。**30~60 人 × 2h 單機吞吐/儲存綽綽有餘(餘量 2~3 數量級)**,故多伺服器定位為**冗餘 / 高可用(防單點故障)非吞吐擴充**——如需抗單點可加一臺熱備;真正約束是雲視覺成本與人工複核能力。

## 10. 防篡改與完整性
- 事件**雜湊鏈**（`hashPrev → hashSelf`），改動 / 刪除 / 插入**鏈中段**任一條會使後續連續性檢核失敗（見 §10.1；**尾部截斷**是結構性盲區，不可檢——誠實標註）。
- Agent↔Server **HMAC 簽名**（預共享金鑰），防偽造上報。
- **雙時間戳**：Agent 本機 `ts` + Server 接收 `recvTs`；考前 NTP 對齊。
- **管理面權杖**：`/api/*` 看板與管理寫端點獨立鑑權，防同網學員機操縱 config / 裁決 / 拉證據圖。M2 起瀏覽器走 **HttpOnly cookie**（`POST /api/login` 下發，`SameSite=Strict`，JS 讀不到、不進 URL），指令碼用戶端仍可用 `X-Horus-Admin` 頭（`?t=` 相容）。回應統一附 CSP / nosniff / X-Frame-Options / Referrer-Policy。
- **擊鍵旁路 KSK 簽名**：`/ingest/keystroke` 由判題後端持 KSK 對整條 body 簽 `X-Horus-KSig`，防同網學員機偽造 / 栽贓他人 `seatId`（見 §10.1、api-contract §2.2）。
- 可靠性收口（M1 已實作）：事件先寫入磁碟再發、**逐條 ack 精確刪除**（非範圍 upto，避免共享 seq 空洞誤刪證據）、**序號高水位持久化**（重新啟動不復用 seq）、斷線指數退避重連 + 續傳、緩衝原子壓實 + 崩潰恢復。

### 10.1 殘留風險（誠實標註，屬威脅模型本質，非缺陷）
- **PSK 駐留學員機**：Agent 跑在學員機、PSK 在本機設定。持 PSK 者可對事件正確簽名。
  - **✅ M2 已閉合「謊報 risk 逃逸」**：伺服器**不再採信 Agent 自報 `risk` 作最終判據**——憑獨立黑名單（AI 站 / 搜尋引擎 / 遠控工具）+ 該考試已下發白名單**重算 `server_risk`**，以**有效風險 = max(agentRisk, serverRisk)** 決定入隊；`serverRisk≥阈值` 而 `agentRisk<阈值` 時記 `agent_risk_understated` 篡改標記。學員把「訪問 AI 站」簽成 `risk=0` 也壓不住入隊。大段粘貼亦從 payload 的 `len`/`lines` 獨立復判,不信 Agent 的 `large` 自報。
    - **缺口（誠實標註）**：若該考試**未下發白名單**(從未 POST `/api/exams/{id}/config`),伺服器對「非黑名單」站/處理程序無獨立判據 → `server_risk=0` 退回靠 Agent 自報;黑名單已覆蓋的站(openai/baidu 等)仍兜得住,但黑名單未覆蓋的小眾搜題/AI 站可漏網。**緩解**:建考試應隨附並下發白名單;看板宜對 active 考試無白名單給維運告警(產品/流程面)。
  - **✅ M3 已實作「完整 canonical 復算 + 雜湊鏈複驗」**（閉合本條大半）：
    - **ingest 時**伺服器從**原始 payload 文字 + 寫入資料庫欄位**逐位元組復算 `canonicalCore`(`EventCanonical.CoreRaw`,與 Agent 端 `Core` 逐位元組一致,黃金測試鎖定)、復算 `hashSelf`,要求與自報一致,否則 **`bad_hash` 拒收不寫入資料庫**。於是 `hashSelf`/`sig` 成為**真正錨定 payload/欄位**的取證錨點(此前 sig 只綁 hashSelf 字串,payload 未被簽名覆蓋)。
    - **離線稽核** `IntegrityAudit` + `GET /api/exams/{examId}/integrity`:按 (agent,seq) 複驗 ①錨點自洽(寫入資料庫後 payload/欄位被改 → 報 hashMismatch)②**簽名自洽**(sig = HMAC-PSK,配了 PSK 時驗:hashSelf 是無金鑰 SHA256,非 PSK 方改 payload 後可重算 hashSelf 使①自洽,唯 sig 能識破 → 報 hashMismatch「sig 不符」)③鏈連續(hash_prev 鏈前驅,刪除/插入/重排 → 報 continuityBreak)。**Agent 重新啟動**新起鏈段(hash_prev=GENESIS,seq 續增)認作 `restartBoundary` 合法段起點、**不誤報鏈斷**(偽造的重新啟動邊界會被②的 sig 檢核識破)。遷移前缺 machine_id 的舊行歸 `unverifiable`,**不誤報篡改**。
    - **仍屬結構性殘留(誠實)**:持 PSK 者仍可建構**自洽的**偽造鏈(payload 內容本身為假、但 hashSelf/sig/鏈全過)——如把真實 AI 站 URL 簽成判題站。此類靠截圖 / L2 視覺 / 人工裁決備援,非雜湊能防。緩解:每 Agent 獨立 PSK(洩露隔離到單座位)、金鑰機器保護而非明文。
    - **尾部截斷不可檢(誠實·第三輪補記)**:雜湊鏈能抓「改動/刪除/插入**中段**」,但**刪除末尾若干條事件**只是讓鏈變短、前綴仍完全自洽,`IntegrityAudit` 報 `ok:true`(即便配了 PSK 也不產生 hash/sig/鏈不符)。這是無「已簽名事件計數/最大 seq 高水位錨」的雜湊鏈**結構極限**。`hello_ack.maxSeq` 僅安全網、未持久化納入稽核。緩解:靠隨機基線截圖 / 人工在場 / 後續可選的「伺服器側 seq 高水位持久化 + 稽核」備援,非本里程碑範圍。
- **事件通道跨身分栽贓 + seq 搶佔（共享 PSK 下真實；OIDC 路徑已閉合）**：握手 `X-Horus-Auth=HMAC(PSK, exam|seat|agent)` 與事件 `sig` 都用全場共享 PSK 時，持 PSK 的學員機可冒充他人身分並搶佔未來 seq；僅檢核“事件體身分 == 握手 query”沒有用，因為握手本身也可偽造。當前貝塔通 OIDC 路徑以 per-user `sub`、PS256 id_token 檢核、userinfo 身分快照和工作階段綁定強制事件身分一致，閉合這兩條風險；`psk` / `both` 僅為 legacy / 灰度模式，仍不具備該保證。拓撲、遷移與歷史判據見 [m4-identity-oidc.md](m4-identity-oidc.md)。
- **✅ 擊鍵旁路已簽名 + 防重放（M2）**：`/ingest/keystroke` 改由**判題後端持 KSK 簽名**（`X-Horus-KSig`,見 api-contract §2.2）——同網學員機無 KSK 無法偽造/栽贓他人 `seatId`；且按 `(exam,seat,ts,submissionId)` **冪等寫入資料庫**,LAN 明文被嗅探後**原樣重放**合法簽名體也不重複入隊（防灌佇列 DoS）。殘留:改了內容的重放會破簽→401;完全新造需 KSK。
- **簽名無時效防重放（事件）**：**刻意不加事件 `ts` 時窗**——與「斷網緩衝 + 續傳」可靠性設計衝突（一條網路中斷期緩衝的事件到達時 `ts` 天然滯後 20 分鐘,時窗會誤殺合法續傳）；而事件重放已由 `(agent,seq)` 去重 + 簽名（`ts` 已進 canonical→`hashSelf`）覆蓋,持 PSK 者本可直接簽新影格,時窗對其無增益。故不引入。圖片靠 imageId 去重。
- **PSK / 管理權杖留空 = 放行**：僅供本機整合測試。**已 fail-closed**：非 loopback 綁定卻缺 PSK 或管理權杖時**拒絕啟動**（`allowInsecure=true` 僅整合測試可繞）。KSK 未納入硬 fail-closed（擊鍵為次級通道,強制會誤傷尚未接判題埋點的部署）；非 loopback 缺 KSK 時僅告警,生產應配。
- **✅ 管理權杖不再落 localStorage / URL（M2）**：改 **HttpOnly cookie**（`POST /api/login` 下發,`SameSite=Strict`+`HttpOnly`）——XSS 讀不到權杖、`<img>` 不再靠 `?t=` 把權杖塞進 URL（防 Referer / 紀錄外洩）；配 CSP 收緊指令碼源防注入外鏈外發。`?t=` / 頭仍向後相容。

### 10.2 規模 / 效能（M1 已夠 30 Agent × 2h；下為已修 + M2 待辦）
- **已修（第二輪複審）**：逐條 ack 改**攢批壓實**（避免每 ack 全檔案重寫的 O(N²)）；重連退避加 **jitter**（打散多 Agent 同步重連風暴）；看板查詢加索引（`events(…,risk)` 覆蓋 / `evidence_image_id` 部分索引 / `agent_heartbeats(exam_id,ts)`）；`is_evidence` 回填僅對觸發型圖；圖片體上限 8→2MB；`url_unreadable` 只在進入不可讀態發一次（不再每 2s 刷爆佇列）。
- **✅ 已做（M2）讀寫分離**：`Db` 從「單連接 + 全域寫鎖」改為 **單寫連接（寫鎖串行）+ 唯讀連接池（WAL 並行讀）**；看板 6 個唯讀 GET 端點走唯讀池,與採集寫路徑**互不阻塞**（監考端每 5s 輪詢不再與寫搶鎖）。**讀與讀之間也並行**(池大小預設 4)：完整性稽核(全考試事件 SHA256)/歸檔 copy 讀不再串行阻塞交互看板輪詢。`:memory:` 例外（記憶體庫每連接獨立且不支援 WAL）→ 退回單連接,測試無感。連接 `Pooling=false`（單例常駐,免 Dispose 後扣檔案句柄妨礙歸檔 VACUUM）。
- **✅ 已做 config 持久化(閉合重新啟動白名單退化)**：下發的每考試設定(`POST /api/exams/{id}/config`)落 `exam_config` 表,伺服器重新啟動時 `AgentHub` 回填記憶體快取 → 白名單不丟,`server_risk` 復判不退化、Agent 重連 `hello` 仍能補推。
- **單寫者仍用寫鎖,未上 Channel（刻意）**：寫鎖已保證「單寫者串行」,而 ack 契約要求「持久化完成才回 ack」→ 後台 Channel writer 需逐條 TCS 等完成,延遲等價、僅增佇列複雜度,30 Agent 規模無吞吐收益。**非同步 Channel writer 作為規模餘量留後續**（真遇執行緒池飢餓再上）。次要:單只讀連接下並行看板讀之間仍串行（唯讀連接池屬後續優化）,但已與寫解耦。
- **✅ 已做(M3)歸檔/清理定時任務**:`ArchiveService`(後台 `PeriodicTimer` 每 `ArchiveScanIntervalHours` 掃描 + 手動 `POST /api/archive/run`)掃描**結束超 `RetentionDays` 天**的考試,關鍵資料(有效風險≥閾值 或 被 suspicious_queue 引用的事件 / 其證據圖 + OCR/Logo / 裁決 / **被引用或高危的擊鍵樣本** / 考試彙總 + hash 錨點)遷入獨立 **archive 庫**,其餘就地清理,`status='archived'`,`VACUUM` 回收。**證據不丟**:pending 未裁決 → 跳過整場;copy(INSERT OR IGNORE)+ 檔案移動(容忍已遷)冪等,copy 後 / delete 前崩潰重跑收斂。心跳等非關鍵隨清理刪除,整鏈斷裂屬**預期**(§13.2 複驗以單條錨點為準)。

## 11. 威脅覆蓋矩陣（誠實標註殘留盲區——因無預防層）

| 威脅 | 捕獲機制 | 殘留風險 |
|---|---|---|
| 網頁 AI / 聯網搜題 | L1 URL 監控 + 截圖 + 視覺 LLM 識圖 | **無網路阻斷，只能事後取證**；隱身 / 冷門瀏覽器抓不到 URL 時降級靠截圖 |
| 本機 IDE AI 外掛 | 隨機截圖（程式碼憑空出現）+ 擊鍵節奏 | 低頻截圖有盲區；外掛 UI 隱蔽時靠節奏 |
| 本機離線 LLM（另開程式） | 處理程序監控 + 截圖 | 若在 Agent 未覆蓋的第二顯示器 / 設備則漏 |
| 大段粘貼外部程式碼 | 剪貼簿 + 擊鍵節奏 | 手動逐字謄抄難判 |
| 遠程協助（TeamViewer 等） | 處理程序監控 + 截圖 | 改名處理程序需特徵庫維護 |
| 手機 / 第二設備 | **本系統不覆蓋** | **需物理監考備援** |

> 結論：純檢測取證能覆蓋"機內"作弊並留證，但**手機 / 第二設備、Agent 未覆蓋的多屏**是結構性盲區，須靠現場物理監考補。

## 12. 監考看板與複核
- **即時**：座位網格 + 風險熱力，紅色告警彈可疑事件 + 縮略圖。
- **複核**：按 student / risk 排序的佇列；點開看證據圖 + 事件時間線 + 視覺 LLM 識圖命中高亮；支援"按圖搜圖"（CLIP·暴力餘弦）。
- 系統給線索，處分由人判。

## 13. 隱私、留存與歸檔

### 13.1 隱私
僅考試時段採集；考前告知採集範圍、留存期、**及"截圖可能經外部處理（視覺 LLM 識圖）"**；最小化採集（預設存中繼資料，圖按需）。

### 13.2 留存 30 天 → 關鍵資料歸檔
熱資料（live DB + 檔案）保留 **30 天**。到期由**歸檔作業**處理（`ArchiveService`,後台每 `ArchiveScanIntervalHours`(預設 6h)掃描 + 維運可 `POST /api/archive/run` 手動觸發,按 `exam.ended_at` 判齡）。**✅ M3 已實作**（見 [../server/Jobs/ArchiveService.cs](../server/Jobs/ArchiveService.cs)）:

- **遷入 archive（關鍵資料，長期留存）**：
  - 被可疑項引用、或有效風險(`max(risk,server_risk)`)`≥ 阈值` 的**事件**；
  - 這些事件的**證據圖**（移入冷存目錄 `archive/<exam>/<seat>/<id>.webp`，路徑改寫）+ 其**視覺 LLM 識圖結果**（`ocr_results` / `logo_hits` 表名沿用）；
  - **被裁決引用或高危的擊鍵樣本**（confirmed 裁決的唯一證據常是擊鍵時間線,隨裁決歸檔 → `archive_keystroke_samples`）；
  - **裁決記錄**（`suspicious_queue` 中 `confirmed` / `dismissed` 的條目）；
  - **考試 / 座位中繼資料**與**彙總**（人數 / 告警數 / 確認違規數 / 遷移計數）；
  - 歸檔事件的 **雜湊錨**（`hashSelf` / `sig`），以便日後完整性複驗。
- **就地清理（不入檔）**：乾淨基線截圖、低危例行事件、心跳、被清圖的 embedding。
- **門禁**：仍有 `pending` 未裁決的考試**跳過整場**（不 purge 未決案證據，留待人工）。
- **冪等 / 崩潰安全**：copy 用 `INSERT OR IGNORE`、證據圖移動容忍已遷；copy 後 / delete 前崩潰重跑收斂,證據先落 archive 才刪 live,不丟關鍵資料。
- 作業完成後 `exam.status = 'archived'`，live 端 `VACUUM` 回收空間（盡力而為:與看板唯讀連接並行偶發 `SQLITE_BUSY` 則記 warning 下次再試,不影響正確性）。

> 歸檔庫 DDL 見 [../schema/schema-archive.sql](../schema/schema-archive.sql)；"關鍵資料"判據可配（預設 `risk ≥ 50` 或被任一 `suspicious_queue` 條目引用）。
> 完整性說明：清理非關鍵事件會使原始雜湊鏈**不連續**；歸檔保留每條關鍵事件的 `hashSelf` 作為獨立錨點，複驗以"單條事件內容 ↔ 其 `hashSelf` / `sig`"為準，不再相依整鏈連續。

## 14. 部署形態
- 伺服器：筆記本跑單處理程序（接收 + 分析 + 看板 Web + SQLite + 檔案儲存）。LAN / IP / localhost 訪問時，**對外聯網僅供視覺 LLM 識圖**（走雲端點時；本機自託管則零出網）。owner 2026-08-04 另行允許正式 `betaoi.cn` Web 主機啟用 Cloudflare Web Analytics / RUM：只有 hostname 屬於 `betaoi.cn` 且設定公開 site token 時載入，`.cc` 由 Cloudflare 自動注入；考場內網訪問不觸發該外聯。
- Agent：考前散佈 exe，登入自啟、綁定座位號、需管理員權限（採 ETW / UIAutomation / WMI），考後停止。

## 15. 分期實作
- **M1 最小閉環**（✅ **已實作並端到端驗證**：伺服器 + 契約 + 看板 + **Agent 採集 / 握手 / 續傳 / 斷線重連**，14 項測試全綠）：Agent 採中繼資料 + L1 URL / 處理程序正則 + 事件 / 隨機截圖 + 伺服器寫入資料庫 + 簡易看板。可靠性(§9)已完成實作：斷網緩衝 + seq 續傳 + 指數退避重連 + 伺服器冪等去重。（介面契約見 api-contract-m1.md；建置/執行見 ../server/README.md）
- **M2 分析增強**：**L2 視覺 LLM(識圖·取代 OCR+Logo)** + 風險評分 + 可疑佇列。**✅ 骨架已實作**(provider-agnostic `IVisionAnalyzer` + Mock + OpenAI 相容 adapter + 非同步後台分析 → `ocr_results` + `server_risk` 聚合 + 可疑佇列)。**✅ 供應商已定並真機驗證 = 小米 MiMo-V2.5 託管 API**(`token-plan-cn.xiaomimimo.com/v1`·`model=mimo-v2.5`·境內雲;key 自動加密存 `visionApiKeyEnc`)。**✅ §5 派生處理已實作**(降取樣 + 剝離中繼資料·原圖永不出網;打碼/裁剪已按 owner 決策移除)。**✅ 基線抽樣策略已實作**(`VisionBaselineSampleRate` 確定性 1/N 抽樣·未抽中標 `analysis_state=1` 終結·控雲成本;預設 1=全分析)。
- **M3 取證強化**：**✅ 已實作「雜湊鏈完整性複驗」**(ingest 時 canonical 逐位元組復算 + `bad_hash` 拒收 + 離線 `GET /api/exams/{id}/integrity` 稽核錨點自洽 / 鏈連續,遷移前舊資料歸 `unverifiable` 不誤報) + **✅「歸檔作業」**(`ArchiveService`:到齡考試關鍵資料轉 archive 庫 + 清理 live + VACUUM,pending 跳過,冪等崩潰安全)。**✅ CLIP 按圖搜圖已實作**(provider-agnostic 嵌入器·**本機 ONNX CLIP**(★實測 MiMo 無 `/v1/embeddings`→改本機·`OnnxClipEmbedder`·零出網 CPU)·C# 暴力餘弦·無需 sqlite-vec·僅嵌證據圖·`/api/exams/{id}/search-image` + 看板燈箱「按圖搜圖」·見 §8)。**擊鍵前端埋點已撤(N/A)**:判題走外部洛谷、無本機判題頁可埋點,核心"粘貼外部程式碼"信號由 Agent OS 級剪貼簿/粘貼檢測覆蓋(見 §6);server 側 KSK 旁路保留待未來自控提交頁。**M3 全部收口**。
- **M4 穩健性 / 身分層**（★ **2026-08-07 起 IdP 是貝塔通**，見 [m4-identity-oidc.md](m4-identity-oidc.md) 首段訂正表）：**✅ OIDC 取代共享 PSK**（採集面 per-user 身分閉合 §10.1 事件通道栽贓 + seq 搶佔，拓撲 A Server-Broker）+ **✅ 管理端平台存取資格**（`horus-admin` 平台開關決定誰能進看板，Horus 本機不判角色；遠端工作站經自簽 https）+ **✅ 考前預檢**（`/api/preflight`：鑑權設定 / 貝塔通可達 / active 考試白名單覆蓋）。**⏳ 待做**：both→oidc 現場灰度驗收。
- **M5 採集端硬化**：**✅ 已實作**——保活 / 防掛起 / 防遮蔽 / 防降權限統一為"檢測 + 上報 + 看板健康告警"(4 新健康信號 + 伺服器獨立賦分入隊 + 座位健康度)+ **三層保活看門狗**(Windows 服務 LocalSystem / 兄弟 watchdog 互拉 / 心跳告警)。純檢測·**不做核心對抗**(owner 拍板)。平台無關檢測核 + 伺服器端 176 測試全綠(M5 批次·累計 **225 全綠**);Windows 專屬(服務 / SessionLauncher / 真實 suspend·遮蔽·降權)待 owner 真機驗收。詳見 [m5-agent-hardening.md](m5-agent-hardening.md)。
- **規模結論**：單場 **30~60 人 × 2h 單伺服器吞吐 / 儲存綽綽有餘**(峰值 ~150KB/s + 十幾 TPS,距 SQLite/NVMe/千兆網瓶頸 2~3 數量級)。**多伺服器 = 冗餘 / 高可用(防單點故障),非吞吐擴充**;按需再做。真正約束是雲視覺成本與監考員人工複核能力,非伺服器吞吐。

## 16. 已採用預設值（次要項，可隨時調）

| 項 | 預設 |
|---|---|
| 大段粘貼閾值 | ≥200 字元 或 ≥5 行 |
| 切窗爆發閾值 | ≥5 次 / 30s |
| pHash 去重 | dHash 漢明距離 ≤3；基線層不去重 |
| 擊鍵特徵 | keydown 時間線 + paste 事件 |
| 留存期 | **30 天**，到期關鍵資料轉 archive，其餘清理 |
| 關鍵資料判據 | `risk ≥ 50` 或被 `suspicious_queue` 引用（可配） |
| 多伺服器 | 單機起步，預留水平擴充 |

## 17. 配套交付物
- 介面契約：[api-contract-m1.md](api-contract-m1.md)
- live DDL：[../schema/schema.sql](../schema/schema.sql)
- archive DDL：[../schema/schema-archive.sql](../schema/schema-archive.sql)
- Agent 採集端：[../agent/](../agent/)（`Horus.Agent` net8.0-windows + 平台無關核 `Horus.Agent.Core`）
