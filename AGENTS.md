# AGENTS.md — Horus

> 繼承工作區 [AGENTS.md](../AGENTS.md)。

<a id="项目与权威"></a>

## 專案與權威

區域網路考試監考系統：本機IDE寫C++、網頁OJ判題；區域網路內筆記本為伺服器。**純檢測+取證、中繼資料優先、系統初篩/人工裁決**，不做網路或主機預防層。所有文件、註解、提交資訊用中文。

- [architecture-v0.2](docs/architecture-v0.2.md)是權威設計；§0鎖定決策、§5出網、§13歸檔；[api-contract-m1](docs/api-contract-m1.md)管採集端↔Server協定與資料模型。
- 改身分先讀 [BetaPass RP契約](../BetaPass/docs/rp-contract.md)和 [m4-identity-oidc](docs/m4-identity-oidc.md)開頭訂正表；採集端硬化見 [m5-agent-hardening](docs/m5-agent-hardening.md)。
- 里程碑、稽核與歷史測試計數只看 [status](docs/status.md)；不可退回的行為以本檔案硬線和權威設計為準，不把歷史全綠當本輪證據。

P147/P149：當前 issuer pass.betaoi.cc，公開靜態主頁 hr.betaoi.cc；cn 休眠。LAN/原生 loopback 與兩用途受眾保持，決定見 architecture §0 D10。

## 元件與介面

- `contracts/`（Horus.Contracts，net8.0）共用線協定/canonical/HMAC/枚舉/事件實作；改動須Agent/Server同核逐位元組簽名與雜湊鏈，不能各寫一套。
- `agentcore/`（Horus.Agent.Core，net8.0）保持平台無關，承載WS/HTTP握手、hello/ack、指數退避重連、續傳、斷網緩衝、設定、雜湊鏈，供測試直接引用。
- `agent/`（net8.0-windows）僅Windows採集：ETW/UIAutomation/WMI/抓屏；單檔案exe含requireAdministrator manifest，啟動會請求管理員權限。
- `server/`（net8.0/ASP.NET Core minimal API+WS）+Microsoft.Data.Sqlite+檔案；`server/wwwroot/`是原生單頁看板，無前端打包鏈。本機ONNX CLIP按圖搜圖用暴力餘弦，未用sqlite-vec。
- `tests/`是xUnit端到端；改contracts/agentcore/server/schema時核協定黃金格式、握手驗簽、圖片/擊鍵冪等與人工裁決。事件/圖片共用seq空間，逐條ack不能改成範圍確認；完整形狀回查api-contract。
- live/archive DDL分別在 `schema/schema.sql`、[schema-archive.sql](schema/schema-archive.sql)。

## 檢測、隱私與身分硬線

- 不控網路、不做主機防火牆、不阻斷搜題/AI；URL監控是第一防線，處理程序/截圖留證。風險分/命中只是線索，處分由人判。
- 能用OS信號判的不拍圖；圖僅可疑時刻+隨機基線（覆蓋IDE外掛），1080p WebP q75、隨機30–90s（architecture §0）。手機/第二設備/未覆蓋多屏須如實標盲區，物理監考備援。
- **唯一可選出網是伺服器視覺LLM識圖**；中繼資料/原圖/向量/看板留區域網路。送雲圖最小化、降取樣、剝EXIF/XMP/IPTC/ICC，原圖永不出網。裁剪/打碼已按owner決策移除，供應商與方案以architecture §5為準。
- 熱資料保留30天；隨後可疑/已判事件、證據圖、視覺結果（表名ocr_results）、裁決、考試中繼資料、雜湊錨轉archive；其餘乾淨基線/低危例行事件/心跳清理。不能把“關鍵資料歸檔”簡化為全刪。
- IdP是BetaPass，非WenTian；簽名允許清單只有PS256，端點在根路徑；scope為openid profile，不登記horus_profile。身分用sub/name/username，username為座位標識不是顯示欄位。
- 姓名/使用者名取userinfo `/me`，不從id_token建身分；`OidcTokenValidator`僅給OidcSubject，身分須經 `Userinfo.FetchAsync`。BetaPass無舊conformIdTokenClaims豁免。
- 看板由BetaPass `horus-admin`平台開關存取資格，Horus不本機判業務角色。採集端與看板心跳不同，按m4訂正表實作，不把瀏覽器選主套進原生Agent。

## 執行與驗收

.NET 8 SDK，無需Visual Studio；專案目標看 `Horus.sln`和各csproj。建置/測試從倉根執行，執行環境的cwd必須是server目錄：

| 何時 | 命令 | 前置 / 通過覆蓋 |
|---|---|---|
| 本機開發執行 | 在 `server/`執行 `dotnet run -c Debug` | 按 [server/README](server/README.md)設定本機範例與隔離dataDir/dbPath；會起服務/看板，不能拿現場設定當測試夾具 |
| 程式碼/協定變化 | `dotnet build Horus.sln -c Debug` → `dotnet test Horus.sln -c Debug` | Windows可建置net8.0-windows Agent；相依需已還原；核結束代碼及測試零失敗，覆蓋倉內契約與端到端 |
| 看板/瀏覽器變化 | 上述測試 + 實際監考工作站走查 | 機構管理Chrome/Edge當前及前一主版本；登入、座位刷新、複核、燈箱、考試控制逐項驗 |
| 開考前 | 現場走查及權威部署/身分預檢 | 不以倉內測試代替真實採集權限、網路、OIDC和硬體條件 |

缺SDK、現場硬體/網路/身分條件或有skip時報告未驗證；測試綠不證明已部署或可開考。執行設定/發佈說明按server/README與對應專檔，不讀取真實金鑰作為文件證據。

## Web與協作

- [baseline.config.json](baseline.config.json)：controlled-web、機構管理Chrome/Edge當前及前一主版本，不含downstream；靜態資源無轉譯，buildTarget必須not-applicable。
- `WebBaselineContractTests`守現代API登記、真實檢測/退回；關鍵監考操作不得因缺Newly能力靜默消失，保留原生路徑。
- Analytics合同：.cc自動/.cn手動、統一token、每頁最多一個beacon；本機部署的hostname門排除.cc/localhost/IP/LAN，不新增分析出網。
- 改看板入口/CSP/設定下發/loader保留Baseline與Analytics合同測試；CSP只追加既有業務來源，不被Analytics改造覆蓋。
- Tracker為本儲存庫GitHub Issues；標籤/domain/OKF入口 [docs/agents/index.md](docs/agents/index.md)。
