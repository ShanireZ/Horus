# docs/dist/ —— 随下载包分发的那几份用户文档

这几份是**给拿到下载包的人看的**：考场装 Agent 的老师、装监考服务器的管理员。
`hr.betaoi.cn` 上的下载包会带上它们。

| 文件 | 给谁 |
| --- | --- |
| [`部署与真机验收.md`](./部署与真机验收.md) | 完整部署 + 验收指南（先读这个） |
| [`server/快速开始.md`](./server/快速开始.md) · [`server/配置说明.md`](./server/配置说明.md) | 装监考服务器的人 |
| [`client/快速开始.md`](./client/快速开始.md) · [`client/配置说明.md`](./client/配置说明.md) | 每台考试机装 Agent 的人 |

## 生成合同

- [`../部署与真机验收.md`](../部署与真机验收.md) 是完整指南的唯一源；本目录同名文件是派生副本，禁止手工修改。
- 从仓库根运行 `pwsh -File scripts/generate-dist-docs.ps1` 重新生成；CI 或发布前用 `pwsh -File scripts/generate-dist-docs.ps1 -Check` 验证没有漂移。脚本含中文路径，要求按 UTF-8 读取脚本的 PowerShell 7，不使用 Windows PowerShell 5.1。
- 生成器去掉源文档的 OKF frontmatter，并把架构、M4/M5 与服务器样例链接按 `docs/dist/` 的目录深度重写。
- 配置默认值仍以 `agentcore/Config/AgentConfig.cs`、`server/Config/ServerConfig.cs` 和 sample JSON 为准；身份线级契约以工作区 BetaPass 项目的 `docs/rp-contract.md` 为准。
