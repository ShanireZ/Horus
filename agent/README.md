# Horus.Agent — 采集端（C#/.NET）

考试机上的信号采集端。采 OS 元数据信号 + 事件触发/随机基线截图，盖哈希链后经 WebSocket（事件）/ HTTP（图片）上报服务器。

**已实现（M1–M5 + 身份层）**：核心信号采集 + 传输的握手鉴权 / `hello`+`ack` / 接收循环（`config_update`/`capture_now`/`exam_ended`/`session_revoked`）/ 断线指数退避重连 + `LocalBuffer` 续传去重、每事件签名（会话私钥·见下）、**OIDC 登录流**（loopback 授权码 + PKCE + ECDH 会话密钥）、**待命循环**（无活跃考试不采集·`/oidc/active-exam` 轮询 + 60s 会话探针）、**采集端硬化**（遮蔽/挂起/能力降级/异常重启四类健康自检 + 三层保活看门狗）。跨平台可测逻辑在 **`Horus.Agent.Core`（agentcore）**，Windows 专属（抓屏 / WMI / UIA / 服务 / watchdog）在本工程。

## 运行环境
- .NET 8 SDK，目标 `net8.0-windows`（Windows-only：UIAutomation / WMI / System.Drawing 抓屏）。
- **需管理员权限**运行：WMI 取 `CommandLine`、ETW、全局剪贴板监听都依赖提权。

## 构建 / 运行
```powershell
cd D:\Workspace\horus\agent
dotnet restore
dotnet build -c Release

# 单文件自包含发布(分发到考试机)
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 运行(传配置路径,缺省 agent.config.json)
cp agent.config.sample.json agent.config.json   # 改好里面的 server 地址/白名单;鉴权模式见下
.\bin\Release\net8.0-windows\win-x64\publish\Horus.Agent.exe agent.config.json
```

**鉴权模式（`authMode`，默认 `oidc`）**：
- `oidc`（默认·推荐）：**per-user 身份**，登录时经系统浏览器走贝塔通授权码 + PKCE（issuer `https://pass.betaoi.cn`、PS256、`openid profile`），Agent 本地生成临时密钥对（**私钥永不出学员机 / 不过网**），会话密钥 ECDH 派生。无共享 PSK，闭合事件通道栽赃 + seq 抢占（见 [../docs/m4-identity-oidc.md](../docs/m4-identity-oidc.md)）。
- `psk`（迁移期兼容）：全场共享 PSK（base64 的 32 字节）。生成：
  ```powershell
  [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
  ```
  服务器须同为 `authMode=psk` 或 `both`。

## 结构

工程拆分为**平台无关可测核 `Horus.Agent.Core`（agentcore/）** 与 **Windows 专属采集端（agent/）**。

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

## 设计要点（与 architecture / api-contract 对齐）
- **URL 监控是第一防线**：判题站走白名单，浏览器出现任何非白名单 URL → risk 80 + 抓图。读不到 URL（隐身/冷门浏览器）→ 降级 risk 40 + 强制抓图。
- **随机基线截图不去重**：专为抓 IDE AI 插件（补全不触发进程/网络事件，只能靠定期看屏）。触发型截图才做 pHash 去重（汉明 ≤3）。
- **隐私**：剪贴板只记长度/行数，**不上传明文**。截图原图发服务器（局域网内），**视觉 LLM 识图**（已取代 OCR）由服务器侧按收口策略（降采样 + 剥元数据·原图永不出网）处理，Agent 不直连云。
- **防篡改**：每条事件入哈希链 + 签名（`psk` 模式=HMAC-PSK / `oidc` 模式=会话密钥·私钥不过网）；`Handle` 用锁串行化盖章，保证链序与 seq 一致。

## 已知残留 / 后续（诚实标注）
- **多显示器抓图**（当前只抓主屏——见威胁矩阵"第二显示器"盲区）。
- 切窗爆发源（`alt_tab_burst`）、ETW 替代 WMI 降延迟。
- ClipboardWatcher 干净关停消息泵。
- **Windows 专属保活/硬化**（服务看门狗 / SessionLauncher / 真实 suspend·遮蔽·降权）已实现并编译通过，**待 owner 真机验收**（见 [../docs/m5-agent-hardening.md](../docs/m5-agent-hardening.md)）。

## 构建排错
- 若 `System.Windows.Automation` 找不到：确认 `<UseWPF>true</UseWPF>`，必要时保留 `UIAutomationClient`/`UIAutomationTypes` 的 `<Reference>`。
- 若剪贴板无回调：确认以**桌面会话**（非服务 Session 0）运行；`ClipboardWatcher` 的 STA 线程消息泵需桌面。
