---
type: status
title: Horus 里程碑狀態（M1–M5）
description: 從 AGENTS.md 移出的完整里程碑與稽核記錄；AGENTS.md 只留「已完成，任何改動不許倒退」的硬線。
tags: [milestone, status, audit]
status: current
---

# Horus 里程碑狀態

> 這裡是**進度與稽核記錄**；不許倒退的硬線留在 `AGENTS.md` 的「已完成」節。

## P147/P149 本機遷移（2026-10-07）

2026-10-09：owner原話“horus暫緩”，決定見[BetaPass PLAN P151](../../BetaPass/PLAN.md)。本輪不切換Horus現場Server/Agent、私有設定、公開主頁、下載或hr.cc DNS，已有本機候選證據保留；後續獨立遷移與設備驗收。若舊設定仍指向pass.cn，核心單issuer切換後不能沿用舊登入，不開備用issuer，也不能據此稱P149整體完成。owner已推送7ef6e99；本儲存庫未設定Actions門，不稱CI通過。

暫緩前已將固定Server ZIP實際解包，以合成設定/記憶體庫只綁定迴環執行自包含Horus.Server.exe：共20項檢查通過、零skip：真實BetaPass signServiceToken對horus-client/horus-dashboard、新舊issuer及health/revoke用途12項；包入口/看板資源/匿名登入跳轉/兌換缺欄位8項。未使用現場憑證、未啟採集器/視覺出網；Windows測試處理程序終止不證明Linux優雅關閉。報告僅在本機忽略證據目錄，真實設定、設備、登入與下載投遞仍未驗。

2026-10-08接續：固定 `215e7a7` 的獨立原始碼副本，官方.NET8.0.424歸檔SHA512驗證後，`dotnet build Horus.sln -c Debug --disable-build-servers -m:1`、`dotnet test Horus.sln -c Debug --no-build`及win-x64自包含單檔案發佈均成功；369測試、零skip，建置20條NuGet漏洞警告/0錯誤，保留原相依未升級。新Agent候選SHA256為`a3c94024cddb383e46dd13b5b68344ec8dba80c74574bf7b6a10ae4fab2a74d8`，只產出未執行採集器，現場設備/真實登入/下載仍未驗。最終Spec/Standards獨立評審完成，README與當前預設issuer說明已對齊cc；歷史英文提交偏差記錄保留，不改寫歷史。未推送/部署，舊公開下載未更換。

2026-10-08繼續準備：同一固定原始碼/SDK另執行 `dotnet publish server/Horus.Server.csproj -c Release -r win-x64 --self-contained true --disable-build-servers`，發佈exit0，10條既有NuGet漏洞警告。win-x64 Server自包含ZIP共347項，靜態閉包核對入口、SQLite原生庫和看板資源通過；真實/範例server.config均未隨包發佈。ZIP SHA256為`41ef670f3e28f5197d480a746d513a04de84c2855dcf65c0fadc27d55dc2593d`，僅本機候選，未啟動Server或設備。公開頁另組裝Linux靜態快照，以保留rate_limit模組的共享Caddy2.11.4參與六站隔離入口設定故障/恢復演練；hr根頁/字體/CSP及CN TLS拒絕通過，頁面保持無來源閘門。公開DNS現查hr.cc不解析、舊共享片段仍只綁定hr.cn；未改變線上或舊下載中繼資料。上述入口演練不覆蓋Server/Agent現場OIDC、設備、機器綁定設定與恢復，不能據此放行開考。

四項新增案例已在舊設定上實際 RED（4失敗/0通過/0skip）：預設/範例、PS256新 issuer、真實原生 loopback 授權位址及 Caddy 主機範本。本機 .NET8.0.424（官方歸檔 SHA512 檢核）建置零警告/錯誤，完整369項通過、零skip；定向72項通過。實際 Caddy2.10.2 以自有 CA 驗三類靜態回應、安全/快取頭、無來源門、休眠CN TLS拒絕通過。真實 loopback 新檢查連續重現原有正文連接重置，補明確 Content-Length 後完整成功頁讀取通過，PKCE/動態連接埠/LAN/ECDH保持。系統8.0.425的首次完整門因 Baseline8.0.424一項失敗，不計全綠。真正 BetaPass signServiceToken 與實際撤權/探活 verifier16項通過；自有CA和正確主機名檢核的pass.betaoi.cc隔離執行個體，實際Agent動態loopback、MapOidc/OidcExchange、userinfo與ECDH工作階段一致性通過（合成記憶體SQLite，無現場硬體），最終獨立評審歸 Task8；未部署、未改現場私有設定，公開下載仍為歷史建置，不能宣稱 Windows 發佈物已遷移。決策見 architecture §0 D10；未部署、未改現場私有設定，公開下載仍為歷史建置。

## 狀態
**M1 最小閉環已實作並通過端到端驗證**：
- ✅ `Horus.Contracts` + `Horus.Agent.Core` + `Horus.Agent`（編譯通過·0 警告）+ `Horus.Server`（WS/HTTP ingest + 寫入資料庫 + 看板）。
- ✅ **Agent 端可靠性完成**：握手鑑權、hello/hello_ack、ack、**斷線重連（指數退避）**、**斷網緩衝 + 續傳**（`UplinkClient` + `LocalBuffer`，伺服器冪等去重），圖片 HMAC 簽名 + 補傳。
- ✅ **config_update 熱更新**：伺服器 `POST /api/exams/{examId}/config` → `AgentHub` 推送給線上 Agent（新連/重連在 hello 時補推）→ Agent `LiveConfig` 原子應用（白名單/閾值/截圖參數，下一輪採集即生效）。
- ✅ **證據圖跨重連關聯**：觸發型抓圖**用戶端預生成 imageId**（`X-Horus-Image-Id`，伺服器沿用、跳過 pHash 去重、冪等），離線緩衝 + 斷線重連補傳後 `evidenceImageId` 關聯不斷。
- ✅ **三路獨立稽核（安全 / 並行與資料完整性 / 正確性與契約）+ 修復**：修掉兩條會丟證據的 Critical（`ack` 改**逐條確認**杜絕空洞誤刪、**序號高水位持久化**杜絕重新啟動重複使用）、`trigger` 映射為契約值、`is_evidence` 亂序回填、`url_unreadable` 強制入隊、緩衝原子壓實 + 崩潰恢復、Schema DDL 切分健壯化；安全加固：**管理權杖 `X-Horus-Admin`（/api 全鑑權）**、圖片體/WS 影格大小上限、`X-Horus-Image-Id` 納入簽名、`LiveConfig` 上下限鉗制、路徑穿越邊界、舊連接 Abort、Dispose 競態。
- ✅ **第二輪複審（效能/負載/可靠性 · 安全 · 邏輯/迴歸 三路並行）+ 修復**：修上一輪引入的兩條迴歸——`url_unreadable` 每 2s 刷爆佇列（改為只在進入不可讀態發一次）、逐條 ack 的 O(N²) 全檔案重寫（改**攢批壓實**）；+ 效能（看板/回填索引、圖片體 8→2MB、重連 jitter）+ 安全（**fail-closed** 非 loopback 缺憑證拒啟動、`FixedTimeEquals` 不洩漏長度、圖片 `?t=` 加 no-referrer/no-store、clientId 驗簽前檢核）+ 邏輯（`LiveConfig` 只下發一端不篡改另一端）。
- ✅ **41 項測試全綠**（含兩輪稽核的迴歸：逐條/攢批 ack · 重新啟動不復用 seq · trigger 映射 · is_evidence 回填 · Schema 分號健壯 · 管理鑑權 401·200·?t= · fail-closed · FixedTimeEquals · LiveConfig 單端下發）+ 真機 curl 驗證。

**M2 信任模型加固（已實作 · 50 項測試全綠 · 三路獨立稽核放行）**：
- ✅ **伺服器側 risk 重算**（閉合 §10.1 頭號殘留）：不採信 Agent 自報 `risk`，憑獨立黑名單（AI 站 / 搜尋引擎 / 遠控工具）+ 該考試**已下發**白名單重算 `server_risk`，以**有效風險 = max(agentRisk, serverRisk)** 決定入隊；`serverRisk≥阈值` 而 `agentRisk<阈值` 記 `agent_risk_understated` 篡改標記。學員把「訪問 AI 站」簽成 `risk=0` 也壓不住入隊（`events` 加 `server_risk` 列 + 冪等遷移）。
- ✅ **keystroke 工作階段鑑權**（閉合 §10.1 栽贓）：判題後端持 **KSK** 對整條 body 簽名 `X-Horus-KSig=HMAC(KSK,"keystroke\n"+sha256(body))`；改 seatId 栽贓即破簽 → 401。域分隔前綴防跨通道重用。
- ✅ **admin 權杖改 HttpOnly cookie + SameSite=Strict + CSP**：`POST /api/login` 檢核後種 cookie（JS 讀不到、`<img>` 自動攜帶、不進 URL）；gate 接受 cookie/頭/`?t=`（後二者相容）；`/api/login /api/logout` 免鑑權；全回應附 CSP/nosniff/X-Frame-Options/Referrer-Policy。
- ✅ **DB 讀寫分離**（§10.2 吞吐天花板）：單寫連接（寫鎖串行）+ 獨立唯讀連接（WAL 並行讀）；看板 6 個 GET 走唯讀連接與寫路徑互不阻塞；`:memory:` 退回單連接（測試無感）；`Pooling=false`。單寫者仍用寫鎖（Channel 因 ack 契約無收益,作規模餘量留後續）。
**M2 分析增強 · 視覺 LLM 引擎（骨架已實作 · 57 項測試全綠）**：
- ✅ **L2 視覺 LLM 識圖取代 OCR + L3 Logo（owner 拍板·合併單一視覺級）**：一次「看懂畫面」同時做文字提取 + AI 對話介面/搜尋頁/IDE 幽靈補全/遠控工具識別 + 分類,結構化直出 `{suspicious,category,confidence,evidence}`。**provider-agnostic**:`IVisionAnalyzer` 介面 + `MockVisionAnalyzer`(確定性·測試整合測試·不出網)+ `OpenAiCompatibleVisionAnalyzer`(DeepSeek-V4 / 小米 MiMo-V2.5 / Qwen-VL / GLM-4V 皆 OpenAI 相容 → 換 `visionBaseUrl`+`visionModel`+`visionApiKey` 即換供應商)。
- ✅ **非同步後台分析**(`VisionAnalysisService` + `Channel`,**不佔 ingest 熱路徑**):圖入庫 → 入隊 → 判定 → 落 `ocr_results` + 標證據 + 引用事件抬 `server_risk` + 入可疑佇列(note=`vision:证据`)。視覺關時整鏈 no-op。
- ✅ **供應商已定 = 小米 MiMo-V2.5 託管 API(境內雲·OpenAI 相容)**:`visionProvider=openai` · `visionBaseUrl=https://token-plan-cn.xiaomimimo.com/v1` · `visionModel=MiMo-V2.5`。**API key 不存明文·自動加密**:`SecretProtect`(Windows DPAPI 機器範圍)。**UX = 維運直接把明文填進 config 的 `visionApiKey`,啟動即自動加密為 `visionApiKeyEnc` 並清空明文(密文回寫檔案·行內替換保留註解)**;亦可 `Horus.Server protect-secret <key>` 預生成密文,或整合測試用 `HORUS_VISION_KEY` env 明文注入(優先級最高·不寫入磁碟)。啟動時 `SecretProtect.Resolve` 解密進記憶體。真機 E2E 驗證過(啟動紀錄「已加密回寫·明文已清除」+ 檔案密文替換 + 註解保留)。
- ✅ **真機 smoke 已過**(2026-07-01·真 key):合成 ChatGPT 截圖 → MiMo 回傳 `{suspicious:true,category:web_ai,confidence:100,hits:[ChatGPT,C++,Dijkstra],evidence:...,text:提取的可见文字}` —— 端點通、key 有效、**adapter 請求格式被接受、MiMo 回傳正好是 `Parse` 期望的 JSON schema、一次呼叫同時介面識別+文字提取+分類**。★**model id = `mimo-v2.5`(小寫)**(不是 `MiMo-V2.5`;`/v1/models` 列出 mimo-v2.5 / -pro / -asr / -tts)。成本 ≈717 token/圖(448 圖 token)。
- ✅ **§5 派生處理已實作**(`server/Analysis/Vision/VisionImagePrep.cs`·ImageSharp 3.1.12):送雲前**降取樣**(`visionMaxEdge` 預設 1600·壓 token)+ **剝離中繼資料**(EXIF/XMP/IPTC/ICC)再重編碼 WebP;**原圖位元組唯讀、永不出網**,只送派生圖;`visionMaxEdge≤0` 直通不解碼(mock 測試走此路)。★**打碼/裁剪已按 owner 決策(2026-07-02)移除**(逐考場配矩形負擔>收益·供應商=境內雲 MiMo·PIPL 無跨境·已告知學員)。
- ✅ **基線抽樣策略**(收尾):`VisionAnalyzeBaseline=true` 時按 `VisionBaselineSampleRate` 做**確定性 1/N 抽樣**(FNV-1a over imageId·跨重新啟動一致),未抽中基線標 `analysis_state=1` 終結、補償重掃不再拾回,控雲成本。預設 1=全分析。

**M3 取證強化(已實作兩項 · 三路獨立稽核放行 · 累計 130 項測試全綠)**:
- ✅ **雜湊鏈完整性複驗**(閉合 §10.1「伺服器不重算 canonical」)=①**ingest 時**從原始 payload 文字 + 寫入資料庫欄位逐位元組復算 `canonicalCore`(`EventCanonical.CoreRaw`,與 Agent 端 `Core` 逐位元組一致·黃金測試鎖定 ~20 反例含中文/代理對/U+2028/數字格式)、復算 `hashSelf`,不符 **`bad_hash` 拒收不寫入資料庫** → `hashSelf`/`sig` 成為**真正錨定 payload** 的取證錨點(此前 sig 只綁 hashSelf 字串,payload 未被簽名覆蓋);②**離線稽核** `IntegrityAudit` + `GET /api/exams/{id}/integrity`:按 (agent,seq) 複驗**錨點自洽**(寫入資料庫後改 payload → hashMismatch)+ **鏈連續**(刪/插/重排 → continuityBreak)。落 `machine_id` 列(canonicalCore 含 machineId,離線復算必須)。**誠實殘留**:持 PSK 者仍可建構自洽偽造鏈(內容為假·靠截圖/視覺/人工備援)。
- ✅ **歸檔/清理作業** `ArchiveService`(後台 `PeriodicTimer` 每 6h + 手動 `POST /api/archive/run`)=掃描結束超 `RetentionDays`(預設30)天考試 → 關鍵資料(有效風險≥閾值 或 被 suspicious_queue 引用的**事件/證據圖/OCR/Logo/擊鍵樣本**+裁決+彙總+hash錨)轉獨立 **archive 庫**(證據圖移冷存 `archive/<exam>/<seat>/<id>.webp`)→ 就地清理 live → `status='archived'` → VACUUM。**證據不丟**:pending 未裁決**跳過整場**;copy(INSERT OR IGNORE)+檔案移動(容忍已遷)冪等,copy 後/delete 前崩潰重跑收斂。
- ★**三路獨立稽核(丟證據/安全/正確性契約)+全修**:修 **C1 Critical**=被 confirmed 裁決引用的**擊鍵樣本**原會被無條件刪且無歸檔表→定罪證據永久丟失(補 `archive_keystroke_samples`+ParseRefs 認 `keystroke:`+清理前先歸檔關鍵擊鍵);修 **High/Med**=`machine_id` 遷移使 M3 前舊事件被離線稽核**誤報"篡改"**(改歸 `unverifiable` 單列·**絕不誤報歷史資料**);修 **Med**=已歸檔考試 integrity 端點回傳空 `ok:true` 偽裝全綠(改 `applicable:false`)。稽核**驗證為非缺陷**:CoreRaw≡Core 逐位元組(實測)·path traversal 雙層封堵·新端點 admin gate 全覆蓋·hashSelf 複驗不誤拒合法線上事件·歸檔冪等/崩潰安全/FK 順/鎖模型均穩。
- ✅ **CLIP 按圖搜圖已完成實作**(見下方 M3 續批·本機 ONNX CLIP·**C# 暴力餘弦·未用 sqlite-vec**)。擊鍵節奏**前端埋點**已撤(考試都在外部洛谷·無本機判題頁·核心粘貼信號 Agent OS 級剪貼簿已覆蓋·server 側 KSK 收 + 歸檔保留就緒)。

**第三輪獨立審查(5 並行 agent)+ 全批修復(owner 拍板全修 · 130 項測試全綠)**:取證錨點核心(canonical 逐位元組·驗簽綁定·ingest 複驗·歸檔崩潰安全/冪等·並行單寫者·注入/DoS/金鑰)5 路複核**均無新缺陷**;修復集中在視覺鏈失敗態語義/URL 判據邊界/歸檔讀路徑/文件漂移:
- **F1(High)**視覺臨時雲失敗→證據圖**永久漏析**(佔位早置 uploaded_to_ocr、backstop 只撈未佔位、失敗不重置)→ 拆 `analysis_state`(處理閂鎖·0待分析/1終結)與 `analysis_attempts`(重試上限 `visionMaxAttempts`),臨時失敗**不終結**由補償重掃按 attempts<上限重試,確定態失敗(檔案缺失/派生失敗)才終結;**F2** `uploaded_to_ocr` 語義拆淨=**僅真出網(SendsOffNetwork 成功送出)才置 1**(mock/本機/失敗恆 0·不再撒謊),meta 端點加 `analyzed`。
- **F3** 歸檔後證據無 HTTP 讀路徑 → `/api/images/{id}`+`/meta` **回落 archive 庫+冷存** + 新增 `GET /api/archive/exams/{id}` 唯讀複核。
- **F4** 同一事件重複入隊(ingest 一條+視覺一條)→ 視覺命中若已有 pending 引用該 event 則**併入**(score 取 max·note 追加 vision)不另插。
- **F5** URL 黑名單 `Contains` 子串假陽性(richardbard→bard·foryou.com→you.com)→ `RiskModel.HostMatchesAny` 改**按 DNS 標籤**(裸詞配整段標籤·dotted 配域後綴);處理程序名保留子串(改名遠控)。HostOf 解析失敗返空不撞整串。**F11** 空 host(about:blank/data:)不再誤判高危。
- **F6** `LocalBuffer.MaxBufferedSeq` 不再全量讀圖位元組(只解析檔案名)。**F7** config 端點補 `IsSafeId`。**F8** no-referrer/no-store 提全域中介軟體。**F9** ParseConfidence 整數 1 不再放大成 100(僅嚴格 0<d<1)。**F10** 視覺解碼 `Image.Identify` 先探尺寸拒超 4096² 防解碼炸彈。
- **D2** `capture_now` 影格從文件承諾變**真**(`POST /api/agents/{agentId}/capture` 推影格)。**D6** integrity 報告加 `sigVerified`/`note`(psk=null 整合測試"綠"不偽裝取證清白)。文件漂移全清(測試數 130·event:manual·舊術語"雲OCR/裁剪打碼"·尾部截斷誠實殘留)。
- **A1/A2 事件通道跨身分栽贓 + seq 搶佔**在共享 PSK 模式下是真實風險；當前貝塔通 OIDC 路徑以 per-user 身分和工作階段綁定閉合它，`psk` / `both` 僅保留為 legacy / 灰度模式。詳見 architecture §10.1 與 [m4-identity-oidc.md](m4-identity-oidc.md)。

**考試派發 + 常態待命 + 全場遠程登出(owner 決策 2026-07-03 · 207 項測試全綠)**:
- ✅ **examId 不再由 Agent 設定攜帶**:`/oidc/exchange` 伺服器端指派「當前活躍考試」(status='active' 最近建立一場;無則 `no_active_exam` 拒,且**先於 token 交換**判定不白耗一次性授權碼);**seatId := OIDC username**(空/不安全退回 sub·`ExamDispatch.SeatFrom`)—— 座位=身分,學員無法自報;協定/canonical/schema/KSK **零遷移**(欄位保留,來源改變),物理定位由 machineId/agentId 承擔。
- ✅ **Agent 常態待命循環**(oidc 模式):啟動即待命輪詢 `GET /oidc/active-exam`(**不採集不抓屏** —— 視窗外持續監控是隱私紅線)→ 開考自動彈瀏覽器 OIDC 登入 → 採集 → 收 `exam_ended`(end 線上推送 / 重連 hello 按考試狀態補發·留 5s 排空緩衝)或 `session_revoked`(立即停)→ 棄工作階段回待命等下一場;每 60s `GET /oidc/session` 探針備援離線錯過的推送。psk(legacy)模式保持設定 examId/seatId 的單場語義。
- ✅ **全場遠程登出** `POST /api/exams/{id}/logout`(admin 門內):`SessionStore.RevokeByExam` 吊銷全部採集工作階段 + 推 `session_revoked` + **強斷線上連接**(吊銷工作階段的舊 WS 不能續用),重連 401;`POST /api/exams/{id}/end` 現在向線上 Agent 推 `exam_ended`(回應帶 notified)。
- ✅ **換場緩衝衛生**:新 OIDC 工作階段開始即 `LocalBuffer.PurgeSession`(舊 K_sess 已死,殘留緩衝必 bad_sig 永不 ack → 每次重連重放-被拒死循環);seq 高水位保留 + hello_ack 對齊不撞舊 seq。
- ✅ **預設管理員執行**:agent exe 內嵌 `requireAdministrator` manifest(雙擊即 UAC 提權,免右鍵);⚠️ requireAdministrator 程式**不能**掛 Run 鍵自啟(系統靜默跳過)—— 本就不常駐自啟(考試前手動打開),保活場景走 `install-service`(LocalSystem 無 UAC)。看門狗單例鍵改 `agentId_machineId`(與考試解耦)。
- ✅ **Agent 近零設定**(owner 決策 2026-07-03):`AgentConfig` 所有欄位內建預設,**設定檔案整個可選**(缺檔案即全預設)。authMode=oidc / issuer=`https://pass.betaoi.cc`（P149 修訂） / scope=`openid profile` / 採集參數 / 白名單(洛谷 + 常見 IDE)全烤預設;**agentId/machineId 留空由主機名自動推導**(machineId=主機名·agentId="ag-"+主機名·`ApplyIdentityDefaults`);examId/seatId/psk 在 oidc 無需配。**唯一去不掉 = 伺服器位址**(Agent 連上前須知道伺服器在哪·無法下發)→ owner 拍板烤固定預設 `192.168.32.145:8080`(IP 不符才覆蓋);**dist/client 不再帶任何設定檔案**(純預設零設定)。★STJ init 集合屬性:設定提供的 whitelist **替換**(非合併)內建預設。

**M4 身分層 + M5 採集端硬化（承前·補記狀態·功能面見對應 docs）**:
- ✅ **M4 採集面 OIDC 取代共享 PSK**（閉合 §10.1 A1 跨身分栽贓 / A2 seq 搶佔）：學員機使用貝塔通 per-user 身分，事件體身分 == 工作階段身分強制一致；`both` 灰度共存、預檢判據要求全部遷 OIDC 才切。見 [m4-identity-oidc.md](m4-identity-oidc.md)。
- ✅ **監考員看板 OIDC 登入**：★★ **2026-08-07 起存取資格由貝塔通的 `horus-admin` 平台開關回答**（其 P83），本機**不再判角色** —— `user_type == 'elder'` 那行已整個刪除。~~wentian 長老 = 監考員，弟子 = 考生；缺 `user_type` fail-safe 到 disciple 絕不誤授~~（**已隨 P83 整個消失**）；**移除靜態權杖後門**（adminAuthMode=oidc 時，仍然成立）；自簽 HTTPS + 貝塔通 `horus-dashboard` client（★ 歸屬平台 `horus-admin`，`redirect_uri` **每臺監考機一條**）。
- ✅ **M5 採集端硬化**（純檢測 = 檢測 + 上報 + 看板健康告警·不做核心對抗）：三層保活（Windows 服務 LocalSystem + 兄弟看門狗互拉 + 心跳告警）+ 4 健康信號（防掛起 `suspected_suspend` / 防遮蔽 `screenshot_obscured` / 防降權限 `capability_degraded` / 服務保活）。見 [m5-agent-hardening.md](m5-agent-hardening.md)。

**工作階段模型三道門（2026-08-11 完工·隨貝塔通 P88/P101）**:
- ✅ **三道門**：心跳 15min / idle 30min / **absolute 6h**（原 3h）。兩張工作階段表加 `last_heartbeat_at` + `last_seen_at`（冪等遷移，舊行回填 `issued_at`）；心跳端點 `POST /api/heartbeat` → 204（150s 節流，判據按欄位分開）。
- ★★ **兩個用戶端的心跳規格必須分開**：看板 `horus-dashboard` 是瀏覽器（定時器 + `BroadcastChannel` 選主）；採集端 `horus-client` 是 exe，用 `GetLastInputInfo` 判真人活動並**重複使用既有 WS 上行**，不新開端點。**照抄瀏覽器那套會把學員踢下線。**
- ★ **任何業務請求都不得續 idle** —— 續期只認心跳這一條通道。
- ✅ 開考預檢自算 absolute 剩餘（採集端橫幅告警 + 看板 `/api/preflight` 的 `session_lifetime`）；存活探針 `GET /internal/health`。
- ⏳ **卡 owner**：`post_logout_redirect_uri` 需在貝塔通登記。

**M3 續批 · CLIP 按圖搜圖（已完成實作）+ 維運 UX 收尾（2026-07-03）**:
- ✅ **本機 ONNX CLIP 嵌入器**（`OnnxClipEmbedder`·OnnxRuntime CPU·零出網）：預處理 resize + center-crop 224² + CLIP 歸一化 → 512 維單位向量；★實測小米 MiMo-V2.5 **無** `/v1/embeddings` 端點 → owner 拍板改本機 ONNX。**規模小 → C# 暴力餘弦，無需 sqlite-vec**（`vec_images` 虛表留大規模餘量）。僅嵌證據圖省算力；`ImageEmbedService` 後台 backstop 補嵌；看板燈箱「按圖搜圖」。⚠️真 ONNX 模型（HF `Qdrant/clip-ViT-B-32-vision` 的 `model.onnx`）屬部署項，冒煙測試發現模型才跑。
- ✅ **考試管理看板 UI**：topbar 三按鈕（開始 / 結束 / 全場登出·後端端點本就有·純連 UI）。
- ✅ **OIDC token 交換重試**（`OidcHttp.PostFormWithRetryAsync`·瞬時 TLS/網路/5xx 重試·4xx 不重試不重複消費授權碼）：緩解伺服器端出站偶發 TLS 攔截。
- ✅ **多金鑰自動加密回寫**（DPAPI）：視覺 key + 兩個 OIDC secret（client + dashboard）填明文啟動即加密為 `*Enc` 並清明文；★必在 env 覆蓋之前跑（絕不把 env 注入的秘密寫回盤）。
- ✅ **Agent 近零設定**：設定檔案整個可選·authMode=oidc / 採集參數 / 白名單全烤預設·agentId/machineId 主機名自動推導·**唯一須配 = 伺服器位址**（預設 `192.168.32.145:8080`）。

**★收口稽核 + 修復（2026-07-05 · 225 項測試全綠）**:對 M3 三輪審查（130 項）以來 **~4257 行未經獨立稽核**的新程式碼（考試派發 / 工作階段吊銷 · CLIP 按圖搜圖 · 零設定 · OIDC 重試 · 考試 UI）做**四維並行獨立稽核**（身分工作階段安全 / 並行資料完整性 / 正確性邏輯契約 / 前端串接 + 文件），逐條對抗性複核去偽，修 **7 項確認缺陷**：
- **[Medium] 全場登出競態**：`EventIngest` 收影格循環握手後從不復查工作階段 → `RevokeByExam`（DELETE 恆先於 abort 遍歷）漏斷「登出瞬間正在握手」的連接 → 登記後補一次工作階段複查閉合（DELETE 先於 abort，凡錯過 abort 者必登記於 DELETE 之後 → 複查必發現吊銷）。
- **[Medium·部署地雷] 連接埠 5199 ↔ 8080 分裂**：原始碼預設 / `server.config.sample.json` / `server/README` 舊 5199，而 Agent 烤死 / dist 成品 / 部署文件全 8080 → 維運照 sample 起服連不上 → 三處提交物對齊 8080。
- **[Low] `/api/exams/{id}/end` 無狀態守衛**：可把 archived/archiving 復活成 ended → 繞過 `/integrity` 的 `applicable:false` 保護偽裝 ok:true → 加 `status IN('active','ended')` 守衛 + `IsSafeId`。
- **[Low] 歸檔清理漏 `image_embeddings`**（裸 PK 表無 FK）→ 向量行懸掛指向已刪圖永久滯留 → 補顯式清理（刪 images 之前）。
- **[Low] 視覺臨時失敗殭屍態**：達 `VisionMaxAttempts` 後卡 `analysis_state=0`（claim/backstop 都按 attempts<上限 過濾·既不重試也不終結）→ 達上限置終態放棄。
- **[Low] `esc()` 不轉義單引號**（防禦縱深·當前不可利用）+ **[Low] `OidcTokenValidator` 補 `nbf` 檢核**。
- 稽核**驗證為非缺陷**（不改）：PS256 驗簽 / A1 身分強制 / 貝塔通平台存取資格 / 金鑰回寫 / 常數時間比較 / 歸檔崩潰安全·冪等 / CLIP center-crop（確用 `ResizeMode.Crop`）/ DNS 標籤匹配 F5·F11 / host 裸詞假陽（寧可錯殺·白名單備援）。補 **4 項迴歸測試**（nbf×2 · 向量孤兒清理 · end 狀態守衛）。

## 提交約定
預設不提交，除非使用者明確要求。commit 資訊用中文，簡潔。
