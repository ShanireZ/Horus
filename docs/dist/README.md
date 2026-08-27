# docs/dist/ —— 随下载包分发的那几份用户文档

这几份是**给拿到下载包的人看的**：考场装 Agent 的老师、装监考服务器的管理员。
`hr.betaoi.cn` 上的下载包会带上它们。

| 文件 | 给谁 |
| --- | --- |
| [`部署与真机验收.md`](./部署与真机验收.md) | 完整部署 + 验收指南（先读这个） |
| [`server/快速开始.md`](./server/快速开始.md) · [`server/配置说明.md`](./server/配置说明.md) | 装监考服务器的人 |
| [`client/快速开始.md`](./client/快速开始.md) · [`client/配置说明.md`](./client/配置说明.md) | 每台考试机装 Agent 的人 |

## ★★★ 为什么它们 2026-08-27 才进 git

**在这之前它们只存在于 `dist/` 里，而 `dist/` 被 `.gitignore` 挡着。**
于是没有任何东西看着它们 —— 逐条实测出来的陈旧共 **36 处**：

| 陈旧内容 | 处数 | 现值 |
| --- | --- | --- |
| issuer 写成 `https://betaoi.cc` | 5 | `https://pass.betaoi.cn`（`AgentConfig.OidcIssuer` 为准）|
| scope 写成 `openid horus_profile` | 1 | `openid profile`（`AgentConfig.OidcScope` 为准，P81）|
| 身份提供方叫 `cpplearn` | 30 | **贝塔通**（2026-07-31 那次改名之后，身份也早已由贝塔通接管）|

> ★★★ **它与 `dist/` 里那两个 exe 是同一件事的两半**：那两个是 2026-07-03 构建的，
> 嵌着的默认 issuer 是 `https://betaoi.cc` —— **比 `f335b59` 订正的那一版还早一代**，
> 而那个域现在是 RootPage / Fulcrum，**不是贝塔通**。
> ⇒ **二进制与它的说明书一起过期，而且过期得彼此自洽** —— 照着那份文档配那个二进制，
> 一切都对得上，只是整套都指向一个不再是身份中心的域。
> ★ 这正是 [[self-consistent-stale-copy-passes-every-gate]] 那一族：
> **本仓内没有任何判据看得见它，因为分歧在仓库之外。**

## ★ 改这里之前要知道的

- **值以源码为准，不以本文档为准**：`agentcore/Config/AgentConfig.cs` 与
  `server/Config/ServerConfig.cs` 里的默认值是权威，这里只是把它们讲给人听。
  ⚠ **两边不一致时，是这里错了。**
- **发下载包之前重新生成一次**：产物与文档要同一批。
  ★ 二进制怎么建见 `agent/README.md`；★ **别拿 `dist/` 里现成的那份**，
  它是手工维护的，没有任何东西保证它是新的。

## ★★★ 那句「长老 = 监考员」已经订正（2026-08-27）

原文写着：

> 贝塔通 **长老 = 监考员**（有看板管理权限），弟子 = 考生

**它早在 2026-08-07 就被推翻了，只是这份文档没跟上。** 贝塔通 **P83** 把 Horus
拆成两个平台：`horus`（采集端 / 学生）与 **`horus-admin`**（看板 / 监考员），
★★★ **「谁能进监考看板」由贝塔通的平台开关回答，Horus 本地不判角色**。

- 起因是 **P81** 停发了 `horus_profile` 这个自定义 scope，而 `user_type` 跟着一起停 ——
  ★ 它此前是看板 RBAC 的**唯一**判据，`NormalizeUserType` 在 claim 缺失时
  fail-safe 到 `disciple` ⇒ **停发之后看板会变成谁也进不去，而且不报错**。
- ★★★ **那一行判断已从 `AdminOidcFlow.cs` 里整个删掉**，不是换了一种实现：
  没有平台权限的人**在授权阶段就换不到 code**。
- ★ 连带好处：撤权变精确 —— 可以「取消监考员资格但保留他作为考生」。

> ★★ **这一处是本目录存在理由的最好例子**：`AGENTS.md` 与 `docs/m4-identity-oidc.md`
> 当天就更新了，**而随下载包分发的这一份没有** —— 因为它躺在被 gitignore 的 `dist/` 里。
> ⇒ 拿着旧文档的人会以为准入还看境界，而实际的开关在贝塔通后台的平台授权上。

⏳ **仍待 owner 的是另一件**：`horus` / `horus-admin` 这两个平台
与 `horus-client` / `horus-dashboard` 两个客户端 **在贝塔通生产库里还一个都没登记**
（2026-08-27 实测：平台只有 `ai` / `betapass` / `cj`）。★ 登记之前，看板的 OIDC 登录跑不起来。
