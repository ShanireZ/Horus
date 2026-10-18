# Horus.Server — 監考伺服器（M1–M5 + 考試派發）

接收採集端上報（WebSocket 事件 + HTTP 圖片/擊鍵）→ 寫入資料庫（SQLite + 檔案系統）→ 簡易 Web 看板。
與 Agent 同為 .NET 8，經 [`Horus.Contracts`](../contracts/) 共享線協定 / HMAC canonical，**雜湊簽名兩端逐位元組一致**。

> 權威設計見 [../docs/architecture-v0.2.md](../docs/architecture-v0.2.md)，介面契約見 [../docs/api-contract-m1.md](../docs/api-contract-m1.md)。

## 前置
- .NET 8 SDK（無需 Visual Studio；`dotnet build/run/test` 即可）。

## 建置 / 測試
```bash
dotnet build ../Horus.sln -c Debug     # 全量(含 Agent，走 net8.0-windows)
dotnet test  ../Horus.sln -c Debug     # 端到端測試(持續全綠·見 CI 輸出)
```

## 執行
```bash
cp server.config.sample.json server.config.json   # 按現場改;生產務必設定 pskBase64
dotnet run -c Debug                                # 或執行已發佈 exe
```
看板：瀏覽器打開 `http://<服务器IP>:8080/`。

燈箱的按圖搜圖按鈕和結果條樣式歸 `wwwroot/styles.css`；顯示狀態繼續由 `hidden` 屬性和現有指令碼控制。

### 設定（`server.config.json`，見 sample）
| 鍵 | 說明 |
|---|---|
| `urls` | Kestrel 綁定，如 `http://0.0.0.0:8080`（預設 8080，與 Agent 內建預設 / dist 成品 / 部署文件一致） |
| `dataDir` | 資料根目錄（SQLite 檔案 + 截圖原圖都在此下） |
| `dbPath` | SQLite 檔案；`:memory:` 走記憶體（測試） |
| `pskBase64` | 採集面預共享 HMAC 金鑰（base64），**與各 Agent 一致**。留空=關閉驗簽（僅整合測試） |
| `keystrokeSecretBase64` | 擊鍵旁路金鑰（base64），判題後端簽 `X-Horus-KSig`。留空=關閉擊鍵鑑權（僅整合測試）。防同網學員機偽造/栽贓擊鍵 |
| `adminToken` | 管理/看板權杖。瀏覽器經 `POST /api/login` 換 **HttpOnly cookie**；指令碼用戶端用 `X-Horus-Admin` 頭（圖片位元組相容 `?t=`）。留空=關閉管理鑑權（僅整合測試）。**生產必配**，防學員機下發設定關檢測/拉證據圖/抹裁決 |
| `riskThreshold` | **有效風險** ≥ 此值入可疑佇列（預設 50）。有效風險 = max(Agent 自報 risk, 伺服器獨立復判 server_risk) |
| `onlineWindowSeconds` / `recentRiskWindowSeconds` | 座位線上判定 / 熱力風險統計視窗 |
| `visionProvider` | 視覺分析（L2·識圖取代 OCR）:留空/`off`=關 · `mock` · `openai`（OpenAI 相容·換 `visionBaseUrl`+`visionModel` 即換供應商）。**已選定並真機驗證 = 小米 MiMo-V2.5 託管 API**（`token-plan-cn.xiaomimimo.com/v1`·`visionModel=mimo-v2.5` 小寫） |
| `visionApiKey` → `visionApiKeyEnc` | **直接把明文 key 填進 `visionApiKey`,啟動即自動 DPAPI 加密為 `visionApiKeyEnc` 並清空明文（密文回寫檔案·保留註解）**。也可 `Horus.Server protect-secret <key>` 預生成密文填 `visionApiKeyEnc`。整合測試可用 `HORUS_VISION_KEY` env 注入明文（優先級最高·不寫入磁碟） |
| `visionConfidenceThreshold` / `visionAnalyzeBaseline` | 視覺命中入隊信賴度閾值（預設 60）/ 是否也分析隨機基線圖（預設否·§5 最小化） |
| `visionMaxEdge` | 送雲前降取樣長邊像素上限（預設 1600·壓 token + 順帶剝離 EXIF/XMP/IPTC/ICC 中繼資料；`≤0`=直通原位元組）。**原圖永不出網**（§5，打碼/裁剪已於 2026-07-02 移除） |

**M4 採集面 OIDC（取代共享 PSK·見 [../docs/m4-identity-oidc.md](../docs/m4-identity-oidc.md)）**
| 鍵 | 說明 |
|---|---|
| `authMode` | 採集面鑑權模式：`psk`（預設·M1-M3 原樣）/ `oidc`（僅 OIDC 工作階段）/ `both`（共存·遷移期退回網） |
| `oidcIssuer` / `oidcEndpointBase` / `oidcClientId` / `oidcClientSecret`(→`Enc`) / `oidcJwksJson` / `oidcSessionMinutes` | 貝塔通 OIDC 接入：必須顯式設定 issuer `https://pass.betaoi.cc`；活動 endpoint base 同為 `.cc`，`.cn` 當前休眠；`horus-client`、client_secret（明文自動 DPAPI 加密為 `Enc`；env `HORUS_OIDC_SECRET`）、PS256 JWKS 內聯（留空則從根路徑 `/jwks` 拉取快取）、工作階段 absolute 門預設 360min |

**M4·RBAC 管理端 OIDC（監考員登入·取代靜態權杖·§10）**
| 鍵 | 說明 |
|---|---|
| `adminAuthMode` | 管理端鑑權：`token`（預設·靜態 `adminToken`）/ `oidc`（貝塔通 `horus-admin` 平台開關已存取資格的帳號；Horus 本機不判角色、無權杖後門） |
| `oidcDashboardClientId` / `oidcDashboardClientSecret`(→`Enc`) / `oidcDashboardRedirectUri` / `adminSessionMinutes` | dashboard web client（`horus-dashboard`·env `HORUS_OIDC_DASHBOARD_SECRET`）、回呼（須與貝塔通登記值精確一致·如 `https://<服务器>/cb`）、管理工作階段 absolute 門預設 360min |
| `httpsCertPath` / `httpsCertPassword` / `httpsSanHosts` | 自簽 HTTPS（遠端監考工作站 OIDC 回呼須 https；留空自動生成·SAN 自動含 localhost/127.0.0.1·`httpsSanHosts` 補伺服器 LAN IP/主機名）。僅 `urls` 含 https 時生效 |

**M3 CLIP 按圖搜圖（provider-agnostic 嵌入器·C# 暴力餘弦·**未用** sqlite-vec·見 [../docs/architecture-v0.2.md §8](../docs/architecture-v0.2.md)）**
| 鍵 | 說明 |
|---|---|
| `embedProvider` | 圖像嵌入器：留空/`off`=關 / `onnx`（**部署預設·本機 ONNX CLIP·零出網**）/ `mock`（測試）/ `openai`（OpenAI 相容 `/v1/embeddings`） |
| `embedOnnxModelPath` / `embedDim` / `embedBackstopMinutes` | ONNX CLIP 模型路徑（留空=約定名 `model.onnx` 於 `dataDir`）、維度（預設 512·僅記錄·暴力餘弦無所謂維度）、後台補掃間隔 |
| `embedBaseUrl` / `embedModel` / `embedApiKey`(→`Enc`) | 僅 `embedProvider=openai` 時用（baseUrl/key 留空則重複使用視覺 `visionBaseUrl`/key） |

**其它**
| 鍵 | 說明 |
|---|---|
| `openDashboard` | 啟動後自動在預設瀏覽器打開看板（預設 true·僅 Windows 交互式真 exe；測試宿主/服務化不彈） |
| `retentionDays` / `archiveEnabled` / `archiveScanIntervalHours` | M3 歸檔：考試結束多少天後轉 archive（預設 30）/ 後台歸檔開關 / 掃描間隔（預設 6h） |

環境變數可覆蓋設定（便於測試/部署）：`HORUS_CONFIG` `HORUS_DATADIR` `HORUS_DBPATH` `HORUS_PSK_B64` `HORUS_KSK_B64` `HORUS_ADMIN_TOKEN` `HORUS_URLS` `HORUS_VISION_PROVIDER` `HORUS_VISION_BASEURL` `HORUS_VISION_MODEL` `HORUS_VISION_KEY` `HORUS_OIDC_SECRET` `HORUS_OIDC_DASHBOARD_SECRET` `HORUS_EMBED_KEY`。

## 端點
**採集端（Agent ↔ Server）**
- `GET  /ingest/events`（WebSocket）— 握手檢核 `X-Horus-Auth`；每事件檢核 `sig` + **雜湊鏈復算**(`bad_hash` 拒收)；冪等寫入資料庫 `(agent_id,seq)`；**伺服器獨立復判 `server_risk`**，有效風險≥閾值入可疑佇列。
- `POST /ingest/images` — 檢核 `X-Horus-Sig`；pHash 去重；原圖存 `dataDir/images/<exam>/<seat>/<id>.webp`；觸發型非同步送**視覺 LLM 分析**(L2)。
- `POST /ingest/keystroke` — 判題後端旁路，**KSK 工作階段簽名**(`X-Horus-KSig`)防偽造/栽贓 + 冪等防重放；擊鍵節奏寫入資料庫 + 基礎風險初判。

**看板 / 複核（唯讀 + 寫）**
- `GET  /api/exams` · `/api/exams/{examId}/seats` · `/{examId}/suspicious?status=` · `/{examId}/events?seatId=&limit=`
- `GET  /api/images/{imageId}`（webp 位元組）· `/api/images/{imageId}/meta` — live 未命中**自動回落 archive 庫 + 冷存**（歸檔考試證據仍可取證）。
- `GET  /api/exams/{examId}/integrity` — 雜湊鏈完整性離線稽核（`sigVerified` 標註是否驗簽；psk 未配時 `ok` 僅表錨點自洽+鏈連續）。
- `GET  /api/archive/exams/{examId}` — 歸檔考試唯讀複核（彙總 + 裁決 + 關鍵事件 + 證據圖列表）。
- `POST /api/exams/{examId}/search-image` — **CLIP 按圖搜圖**（上傳一張圖，C# 暴力餘弦 top-N 相似證據圖；需 `embedProvider` 啟用）。
- `POST /api/exams`（建考試+座位）· `/api/exams/{examId}/end` · `/api/suspicious/{id}/decide`（人工裁決）
- `POST /api/exams/{examId}/logout` — **全場遠程登出**：吊銷該考試全部 OIDC 採集工作階段 + 推 `session_revoked` + 強斷連接，回傳 `{ ok, examId, revoked, notified }`。
- `POST /api/exams/{examId}/config` — 下發**設定熱更新**給該考試線上 Agent（白名單/閾值/截圖參數），回傳 `pushedTo`；新連/重連 Agent 在 hello 時補推。
- `POST /api/agents/{agentId}/capture` — 監考員點名抓圖：向線上 Agent 推 `capture_now`，回傳 `pushed`。
- `POST /api/archive/run` — 手動觸發歸檔作業（後台亦每 `archiveScanIntervalHours` 自動跑）。
- `GET  /api/authmode`（**公開·gate 豁免**）— 前端探測採集/管理鑑權模式（`psk|oidc|both` / `token|oidc`），據此切換權杖門 / OIDC 登入按鈕，`both` 灰度期高亮仍走 PSK 的座位。
- `GET  /api/preflight` — 考前預檢：鑑權設定 / 貝塔通可達（`issuer_reachable`）/ active 考試白名單覆蓋 / both→oidc 遷移進度（`migration`）。

**M4 身分層（OIDC·非 `/api/*`，不受 admin gate）**
- `POST /oidc/exchange` — Agent 用 loopback code + PKCE verifier + ECDH 公鑰換工作階段；**examId 伺服器端派發**（當前活躍考試，無則 `no_active_exam`），seatId := username。
- `GET  /oidc/active-exam`（**公開**）— Agent 待命輪詢：當前是否有活躍考試（考試開始才彈登入、啟採集）。
- `GET  /oidc/session` — 工作階段探針（頭 `X-Horus-Session`）：工作階段是否仍有效 + 所綁考試狀態（Agent 60s 備援探"被遠程登出/考試結束"）。
- `GET  /admin/login` — 監考員看板 OIDC 登入：生成 state+nonce+PKCE，302 到貝塔通根路徑 `/auth`（dashboard client，scope=`openid profile`）。
- `GET  /cb` — 貝塔通回呼：換 token → 僅按 PS256 驗 id_token subject → 從 userinfo `/me` 取姓名/使用者名 → 建管理工作階段 → 種 HttpOnly cookie → 跳看板。沒有 `horus-admin` 平台權限的帳號在貝塔通授權階段即被拒，Horus 不再檢查 `user_type`。

## 實作進度（見 architecture §15）
- **M1 已實作**：ingest 寫入資料庫 / 冪等去重 / 圖片存盤去重 / HMAC 驗簽 / 可疑佇列 / 看板 + 人工裁決 + Agent 採集/握手/續傳/斷線重連。
- **M2 已實作**：**L2 視覺 LLM 識圖**(取代 OCR+Logo·provider-agnostic·小米 MiMo-V2.5)+ 伺服器側 `server_risk` 復判 + keystroke KSK 工作階段簽名 + admin HttpOnly cookie + DB 讀寫分離。
- **M3 已實作**：**雜湊鏈完整性複驗**(ingest 復算 `bad_hash` 拒收 + 離線 `GET /api/exams/{id}/integrity` 稽核:錨點/sig/鏈連續/重新啟動邊界) + **歸檔作業** `ArchiveService`(到齡考試關鍵資料轉 archive 庫 + 清理 live + VACUUM) + **CLIP 按圖搜圖已完成實作**(provider-agnostic 嵌入器·本機 ONNX CLIP·**C# 暴力餘弦·未用 sqlite-vec**·僅嵌證據圖·`POST /api/exams/{id}/search-image` + 看板燈箱)。
- **M4 已實作**：**採集面 OIDC**(貝塔通 per-user 身分取代共享 PSK·閉合事件通道栽贓 + seq 搶佔·`authMode=psk|oidc|both`) + **管理端平台存取資格**(`horus-admin` 開關在授權階段判定·管理端 OIDC 取代靜態權杖·`adminAuthMode=token|oidc`·自簽 HTTPS) + **考前預檢** `/api/preflight`。
- **M5 已實作**：**採集端硬化**(保活三層看門狗 + 遮蔽/掛起/能力降級/異常重新啟動四類健康信號·伺服器獨立賦分入隊 + 座位健康度)——純檢測·不做核心對抗。
- **考試派發（2026-07-03）**：examId 伺服器端派發（`/oidc/exchange` 指派活躍考試）· seatId=username · Agent 待命輪詢（`/oidc/active-exam`）· 全場遠程登出（`/api/exams/{id}/logout`）。
- **待做**：擊鍵前端埋點已撤（判題走外部洛谷、無本機判題頁可埋點，核心"粘貼外部程式碼"信號由 Agent OS 級剪貼簿檢測覆蓋；server 側 KSK 旁路保留待未來自控提交頁）。啟動時按權威 `schema.sql` 建表，跳過 `vec0` 虛表（CLIP 暴力餘弦不相依 sqlite-vec 擴充）。
