# Horus

**本地局域网考试监考系统** —— 防止学员在编程 / OJ 考试中用 AI 做题或联网搜题。

> 设计哲学:**纯检测 + 取证**(不做网络/主机层阻断)、**元数据优先、图像为辅**、**系统只初筛、人工裁决**。
> 全部数据在本地局域网内流转,唯一对外出网的是服务器侧的**视觉 LLM 识图**(最小化上传:送云前降采样 + 剥离元数据·**原图永不出网**)。

## 这是什么 / 不是什么
- **是**:面向"本地 IDE 写 C++ + 网页判题"场景的监考工具——采集 OS 元数据信号(前台窗口 / 进程 / 浏览器 URL / 剪贴板 / USB)、事件触发 + 随机基线截图,在服务器侧分析、留证,供监考员人工复核。
- **不是**:不阻断网络、不强制锁屏;手机 / 第二设备等"机外"作弊靠现场物理监考兜底(见架构"威胁覆盖矩阵")。

## 组件
| 组件 | 说明 | 技术 |
|---|---|---|
| 采集端 Agent | 考试机各一;采信号 + 截图,哈希链 + HMAC 上报 | C#/.NET 8(`agent/`) |
| 监考服务器 | 笔记本;接收 + 分析(L1 元数据 / L2 视觉 LLM 识图) + 落库 + 看板 | SQLite + 文件 + sqlite-vec |
| 监考端 | 实时看板 + 可疑队列复核 + 按图搜图 | (规划中) |

## 仓库结构
```
agents.md                  项目准则(给 AI 协作者)
docs/architecture-v0.2.md  权威架构设计
docs/api-contract-m1.md    M1 接口契约(WS / HTTP 协议 + 数据模型)
schema/schema.sql          SQLite live DB DDL
schema/schema-archive.sql  SQLite archive DB DDL(30 天后关键数据归档)
agent/                     C#/.NET 采集端骨架(Horus.Agent)
```

## 状态
**M1–M5 已实现并端到端验证(180 项测试全绿)**:M1 采集/握手/续传/断线重连 + ingest 落库/幂等/看板/人工裁决;M2 视觉 LLM 识图(取代 OCR+Logo·小米 MiMo-V2.5·基线图确定性 1/N 抽样控云成本)+ 服务器侧 `server_risk` 复判 + 击键 KSK 会话签名 + admin HttpOnly cookie + DB 读写分离;M3 哈希链完整性复验(ingest `bad_hash` 拒收 + 离线审计)+ 归档/清理作业;**M4 身份层 cpplearn OIDC 取代共享 PSK**(Agent 经 cpplearn 授权码+PKCE 登录 → ECDH 会话密钥签名 → 闭合 A1 跨身份栽赃 + A2 seq 抢占)+ **RBAC 角色映射**(`user_type='elder'`=监考员/看板管理端权限,`'disciple'`=参考学员(考生);监考员用 cpplearn OIDC 登录看板取代静态令牌·远端工作站经自签 https·真机 smoke 通过·见 [docs/m4-identity-oidc.md](docs/m4-identity-oidc.md) §10)+ **考前预检**(`/api/preflight` + 看板「预检」按钮:鉴权配置 / cpplearn 可达 / active 考试白名单覆盖一键自查);**M5 采集端硬化**(保活/防挂起/防遮蔽/防降权限——统一为"检测+上报+看板健康告警"+三层保活看门狗·纯检测不做内核对抗·见 [docs/m5-agent-hardening.md](docs/m5-agent-hardening.md))。**规模**:单场 30~60 人×2h 单服务器吞吐/存储绰绰有余(余量 2~3 数量级),多服务器=冗余非扩展。**both→oidc 灰度**:采集面迁移验收清单 + 工具(预检 `migration` 覆盖 + 看板 both 期「PSK」座位高亮 + 座位「采集鉴权」行)见 [docs/m4-identity-oidc.md](docs/m4-identity-oidc.md) §11。**待做**:CLIP 按图搜图(需 sqlite-vec + CLIP ONNX)、击键前端埋点(判题网页·本仓外)、M5 Windows 专属项真机验收。里程碑见 [docs/architecture-v0.2.md](docs/architecture-v0.2.md) §15。

## 关键设计取舍
- **预防层为零**:不控考场网络、不做主机防火墙 → 浏览器 URL 监控是第一防线,只能事后取证、不可阻断。
- **留存 30 天 → 归档**:热数据保留 30 天,到期关键数据(可疑 / 已判事件 + 证据图 + 裁决 + 哈希锚)转入 archive,其余清理。
- **向量化只作检索**:CLIP embedding 用于"按图搜图",**单向不可逆、不能还原原图**;证据永远是压缩后的真实截图。

## 合规与正当使用
本系统用于**获得授权的考试监考**。部署前须:告知被监考者采集范围与留存期、取得知情同意、遵守所在地隐私 / 数据保护法规;截图经外部**视觉 LLM** 处理须与供应商签数据处理协议。**请勿用于未授权的监控。**

## 许可 / License
**GNU GPL-3.0**(见 [LICENSE](LICENSE))。Copyright © 2026 ShanireZ。
