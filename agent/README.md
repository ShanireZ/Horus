# Horus.Agent — 採集端（C#/.NET）

考試機上的信號採集端。採 OS 中繼資料信號 + 事件觸發/隨機基線截圖，蓋雜湊鏈後經 WebSocket（事件）/ HTTP（圖片）上報伺服器。

**已實作（M1–M5 + 身分層）**：核心信號採集 + 傳輸的握手鑑權 / `hello`+`ack` / 接收循環（`config_update`/`capture_now`/`exam_ended`/`session_revoked`）/ 斷線指數退避重連 + `LocalBuffer` 續傳去重、每事件簽名（工作階段私鑰·見下）、**OIDC 登入流**（loopback 授權碼 + PKCE + ECDH 工作階段金鑰）、**待命循環**（無活躍考試不採集·`/oidc/active-exam` 輪詢 + 60s 工作階段探針）、**採集端硬化**（遮蔽/掛起/能力降級/異常重新啟動四類健康自檢 + 三層保活看門狗）。跨平台可測邏輯在 **`Horus.Agent.Core`（agentcore）**，Windows 專屬（抓屏 / WMI / UIA / 服務 / watchdog）在本工程。

## 執行環境
- .NET 8 SDK，目標 `net8.0-windows`（Windows-only：UIAutomation / WMI / System.Drawing 抓屏）。
- **需管理員權限**執行：WMI 取 `CommandLine`、ETW、全域剪貼板監聽都相依提權。

## 建置 / 執行
```powershell
cd D:\Workspace\horus\agent
dotnet restore
dotnet build -c Release

# 單檔案自包含發佈(散布到考試機)
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 執行(傳設定路徑,預設 agent.config.json)
cp agent.config.sample.json agent.config.json   # 改好里面的 server 地址/白名单;鉴权模式见下
.\bin\Release\net8.0-windows\win-x64\publish\Horus.Agent.exe agent.config.json
```

**鑑權模式（`authMode`，預設 `oidc`）**：
- `oidc`（預設·推薦）：**per-user 身分**，登入時經系統瀏覽器走貝塔通授權碼 + PKCE（issuer `https://pass.betaoi.cc`、PS256、`openid profile`），Agent 本機生成臨時金鑰對（**私鑰永不出學員機 / 不過網**），工作階段金鑰 ECDH 派生。無共享 PSK，閉合事件通道栽贓 + seq 搶佔（見 [../docs/m4-identity-oidc.md](../docs/m4-identity-oidc.md)）。
- `psk`（遷移期相容）：全場共享 PSK（base64 的 32 位元組）。生成：
  ```powershell
  [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
  ```
  伺服器須同為 `authMode=psk` 或 `both`。

## 結構

工程拆分為**平台無關可測核 `Horus.Agent.Core`（agentcore/）** 與 **Windows 專屬採集端（agent/）**。

```
── agentcore/（平台无关·可单测）─────────────────────────
Model/RawSignal.cs         事件/RawSignal 模型 + SignalType
Config/AgentConfig.cs      配置(从 json 加载·authMode/serverWsBase 等)
Config/LiveConfig.cs       config_update 原子热替换
Integrity/HashChain.cs     哈希链 + 签名(HMAC / 会话密钥)
Transport/UplinkClient.cs  WS 事件 + HTTP 图片 + 握手/ack/续传/重连
Transport/TriggerMap.cs    触发原因映射
Buffer/LocalBuffer.cs      断网缓冲(文件落地)
Identity/OidcLoginFlow.cs  OIDC loopback 登录(授权码+PKCE+ECDH 会话密钥)
Hardening/ScreenQuality.cs 遮蔽分类(纯色/低熵)·SuspendMonitor 等纯逻辑核
── agent/（Windows 专属·真机验收）───────────────────────
Signals/ForegroundWindowSource.cs  前台窗口标题+进程(轮询)
Signals/BrowserUrlSource.cs        浏览器地址栏 URL(UIAutomation)★第一防线
Signals/ProcessWatcher.cs          进程启停(WMI)
Signals/ClipboardWatcher.cs        大段粘贴(剪贴板,自带 STA 线程)
Signals/UsbWatcher.cs              USB 到达(WMI)
Capture/ScreenshotCapturer.cs      抓屏→缩放→WebP→pHash→上传(+每帧 luma 喂遮蔽分类器)
Capture/PerceptualHash.cs          dHash + 汉明距离
Hardening/AgentHardening.cs        管理员自检 / 重启标记 / 挂起接线
Hardening/Watchdog.cs              保活层2:supervisor + 兄弟 watchdog 互拉
Hardening/WindowsService.cs        保活层1:LocalSystem 服务看门狗(sc.exe)
Hardening/SessionLauncher.cs       CreateProcessAsUser 把采集拉进交互会话
Program.cs                         装配 + 管线 + 待命/基线/心跳循环
```

## 設計要點（與 architecture / api-contract 對齊）
- **URL 監控是第一防線**：判題站走白名單，瀏覽器出現任何非白名單 URL → risk 80 + 抓圖。讀不到 URL（隱身/冷門瀏覽器）→ 降級 risk 40 + 強制抓圖。
- **隨機基線截圖不去重**：專為抓 IDE AI 外掛（補全不觸發處理程序/網路事件，只能靠定期看屏）。觸發型截圖才做 pHash 去重（漢明 ≤3）。
- **隱私**：剪貼板只記長度/行數，**不上傳明文**。截圖原圖發伺服器（區域網路內），**視覺 LLM 識圖**（已取代 OCR）由伺服器側按收口策略（降取樣 + 剝中繼資料·原圖永不出網）處理，Agent 不直連雲。
- **防篡改**：每條事件入雜湊鏈 + 簽名（`psk` 模式=HMAC-PSK / `oidc` 模式=工作階段金鑰·私鑰不過網）；`Handle` 用鎖串行化蓋章，保證鏈序與 seq 一致。

## 已知殘留 / 後續（誠實標註）
- **多顯示器抓圖**（當前只抓主屏——見威脅矩陣"第二顯示器"盲區）。
- 切窗爆發源（`alt_tab_burst`）、ETW 替代 WMI 降延遲。
- ClipboardWatcher 乾淨關停訊息泵。
- **Windows 專屬保活/硬化**（服務看門狗 / SessionLauncher / 真實 suspend·遮蔽·降權）已實作並編譯通過，**待 owner 真機驗收**（見 [../docs/m5-agent-hardening.md](../docs/m5-agent-hardening.md)）。

## 建置排錯
- 若 `System.Windows.Automation` 找不到：確認 `<UseWPF>true</UseWPF>`，必要時保留 `UIAutomationClient`/`UIAutomationTypes` 的 `<Reference>`。
- 若剪貼板無回呼：確認以**桌面工作階段**（非服務 Session 0）執行；`ClipboardWatcher` 的 STA 執行緒訊息泵需桌面。
