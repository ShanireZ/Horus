<a id="docsdist--随下载包分发的那几份用户文档"></a>

# docs/dist/ —— 隨下載包散布的那幾份使用者文件

這幾份是**給拿到下載包的人看的**：考場裝 Agent 的老師、裝監考伺服器的管理員。
`hr.betaoi.cn` 上的下載包會帶上它們。

| 文件 | 給誰 |
| --- | --- |
| [`部署與真機驗收.md`](./部署與真機驗收.md) | 完整部署 + 驗收指南（先讀這個） |
| [`server/快速开始.md`](./server/快速开始.md) · [`server/配置说明.md`](./server/配置说明.md) | 裝監考伺服器的人 |
| [`client/快速开始.md`](./client/快速开始.md) · [`client/配置说明.md`](./client/配置说明.md) | 每臺考試機裝 Agent 的人 |

## 生成合同

- [`../部署與真機驗收.md`](../部署與真機驗收.md) 是完整指南的唯一源；本目錄同名文件是派生副本，禁止手動修改。
- 從儲存庫根執行 `pwsh -File scripts/generate-dist-docs.ps1` 重新生成；CI 或發佈前用 `pwsh -File scripts/generate-dist-docs.ps1 -Check` 驗證沒有漂移。指令碼含中文路徑，要求按 UTF-8 讀取指令碼的 PowerShell 7，不使用 Windows PowerShell 5.1。
- 生成器去掉源文件的 OKF frontmatter，並把架構、M4/M5 與伺服器範例連結按 `docs/dist/` 的目錄深度重寫。
- 設定預設值仍以 `agentcore/Config/AgentConfig.cs`、`server/Config/ServerConfig.cs` 和 sample JSON 為準；身分線級契約以工作區 BetaPass 專案的 `docs/rp-contract.md` 為準。
