# AGENTS.md — Horus

> 继承工作区 [AGENTS.md](../AGENTS.md)。

## 项目与权威

局域网考试监考系统：本地IDE写C++、网页OJ判题；局域网内笔记本为服务器。**纯检测+取证、元数据优先、系统初筛/人工裁决**，不做网络或主机预防层。所有文档、注释、提交信息用中文。

- [architecture-v0.2](docs/architecture-v0.2.md)是权威设计；§0锁定决策、§5出网、§13归档；[api-contract-m1](docs/api-contract-m1.md)管采集端↔Server协议与数据模型。
- 改身份先读 [BetaPass RP契约](../BetaPass/docs/rp-contract.md)和 [m4-identity-oidc](docs/m4-identity-oidc.md)开头订正表；采集端硬化见 [m5-agent-hardening](docs/m5-agent-hardening.md)。
- 里程碑、审计与历史测试计数只看 [status](docs/status.md)；不可回退的行为以本文件硬线和权威设计为准，不把历史全绿当本轮证据。

P147/P149：当前 issuer pass.betaoi.cc，公开静态主页 hr.betaoi.cc；cn 休眠。LAN/原生 loopback 与两用途受众保持，决定见 architecture §0 D10。

## 组件与接口

- `contracts/`（Horus.Contracts，net8.0）共用线协议/canonical/HMAC/枚举/事件实现；改动须Agent/Server同核逐字节签名与哈希链，不能各写一套。
- `agentcore/`（Horus.Agent.Core，net8.0）保持平台无关，承载WS/HTTP握手、hello/ack、指数退避重连、续传、断网缓冲、配置、哈希链，供测试直接引用。
- `agent/`（net8.0-windows）仅Windows采集：ETW/UIAutomation/WMI/抓屏；单文件exe含requireAdministrator manifest，启动会请求管理员权限。
- `server/`（net8.0/ASP.NET Core minimal API+WS）+Microsoft.Data.Sqlite+文件；`server/wwwroot/`是原生单页看板，无前端打包链。本地ONNX CLIP按图搜图用暴力余弦，未用sqlite-vec。
- `tests/`是xUnit端到端；改contracts/agentcore/server/schema时核协议黄金格式、握手验签、图片/击键幂等与人工裁决。事件/图片共用seq空间，逐条ack不能改成范围确认；完整形状回查api-contract。
- live/archive DDL分别在 `schema/schema.sql`、[schema-archive.sql](schema/schema-archive.sql)。

## 检测、隐私与身份硬线

- 不控网络、不做主机防火墙、不阻断搜题/AI；URL监控是第一防线，进程/截图留证。风险分/命中只是线索，处分由人判。
- 能用OS信号判的不拍图；图仅可疑时刻+随机基线（覆盖IDE插件），1080p WebP q75、随机30–90s（architecture §0）。手机/第二设备/未覆盖多屏须如实标盲区，物理监考兜底。
- **唯一可选出网是服务器视觉LLM识图**；元数据/原图/向量/看板留局域网。送云图最小化、降采样、剥EXIF/XMP/IPTC/ICC，原图永不出网。裁剪/打码已按owner决策移除，供应商与方案以architecture §5为准。
- 热数据保留30天；随后可疑/已判事件、证据图、视觉结果（表名ocr_results）、裁决、考试元数据、哈希锚转archive；其余干净基线/低危例行事件/心跳清理。不能把“关键数据归档”简化为全删。
- IdP是BetaPass，非WenTian；签名允许清单只有PS256，端点在根路径；scope为openid profile，不登记horus_profile。身份用sub/name/username，username为座位标识不是显示字段。
- 姓名/用户名取userinfo `/me`，不从id_token建身份；`OidcTokenValidator`仅给OidcSubject，身份须经 `Userinfo.FetchAsync`。BetaPass无旧conformIdTokenClaims豁免。
- 看板由BetaPass `horus-admin`平台开关准入，Horus不本地判业务角色。采集端与看板心跳不同，按m4订正表实现，不把浏览器选主套进原生Agent。

## 运行与验收

.NET 8 SDK，无需Visual Studio；项目目标看 `Horus.sln`和各csproj。构建/测试从仓根执行，运行时的cwd必须是server目录：

| 何时 | 命令 | 前置 / 通过覆盖 |
|---|---|---|
| 本地开发运行 | 在 `server/`执行 `dotnet run -c Debug` | 按 [server/README](server/README.md)配置本地样例与隔离dataDir/dbPath；会起服务/看板，不能拿现场配置当测试夹具 |
| 代码/协议变化 | `dotnet build Horus.sln -c Debug` → `dotnet test Horus.sln -c Debug` | Windows可构建net8.0-windows Agent；依赖需已还原；核退出码及测试零失败，覆盖仓内契约与端到端 |
| 看板/浏览器变化 | 上述测试 + 实际监考工作站走查 | 机构管理Chrome/Edge当前及前一主版本；登录、座位刷新、复核、灯箱、考试控制逐项验 |
| 开考前 | 现场走查及权威部署/身份预检 | 不以仓内测试代替真实采集权限、网络、OIDC和硬件条件 |

缺SDK、现场硬件/网络/身份条件或有skip时报告未验证；测试绿不证明已部署或可开考。运行配置/发布说明按server/README与对应专档，不读取真实密钥作为文档证据。

## Web与协作

- [baseline.config.json](baseline.config.json)：controlled-web、机构管理Chrome/Edge当前及前一主版本，不含downstream；静态资源无转译，buildTarget必须not-applicable。
- `WebBaselineContractTests`守现代API登记、真实检测/回退；关键监考操作不得因缺Newly能力静默消失，保留原生路径。
- Analytics合同：.cc自动/.cn手工、统一token、每页最多一个beacon；本地部署的hostname门排除.cc/localhost/IP/LAN，不新增分析出网。
- 改看板入口/CSP/配置下发/loader保留Baseline与Analytics合同测试；CSP只追加既有业务来源，不被Analytics改造覆盖。
- Tracker为本仓GitHub Issues；标签/domain/OKF入口 [docs/agents/index.md](docs/agents/index.md)。
