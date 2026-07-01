# Horus.Server — 监考服务器（M1）

接收采集端上报（WebSocket 事件 + HTTP 图片/击键）→ 落库（SQLite + 文件系统）→ 简易 Web 看板。
与 Agent 同为 .NET 8，经 [`Horus.Contracts`](../contracts/) 共享线协议 / HMAC canonical，**哈希签名两端逐字节一致**。

> 权威设计见 [../docs/architecture-v0.2.md](../docs/architecture-v0.2.md)，接口契约见 [../docs/api-contract-m1.md](../docs/api-contract-m1.md)。

## 前置
- .NET 8 SDK（无需 Visual Studio；`dotnet build/run/test` 即可）。

## 构建 / 测试
```bash
dotnet build ../Horus.sln -c Debug     # 全量(含 Agent，走 net8.0-windows)
dotnet test  ../Horus.sln -c Debug     # 端到端测试(10 项)
```

## 运行
```bash
cp server.config.sample.json server.config.json   # 按现场改;生产务必配置 pskBase64
dotnet run -c Debug                                # 或运行已发布 exe
```
看板：浏览器打开 `http://<服务器IP>:5199/`。

### 配置（`server.config.json`，见 sample）
| 键 | 说明 |
|---|---|
| `urls` | Kestrel 绑定，如 `http://0.0.0.0:5199` |
| `dataDir` | 数据根目录（SQLite 文件 + 截图原图都在此下） |
| `dbPath` | SQLite 文件；`:memory:` 走内存（测试） |
| `pskBase64` | 采集面预共享 HMAC 密钥（base64），**与各 Agent 一致**。留空=关闭验签（仅联调） |
| `keystrokeSecretBase64` | 击键旁路密钥（base64），判题后端签 `X-Horus-KSig`。留空=关闭击键鉴权（仅联调）。防同网学员机伪造/栽赃击键 |
| `adminToken` | 管理/看板令牌。浏览器经 `POST /api/login` 换 **HttpOnly cookie**；脚本客户端用 `X-Horus-Admin` 头（图片字节兼容 `?t=`）。留空=关闭管理鉴权（仅联调）。**生产必配**，防学员机下发配置关检测/拉证据图/抹裁决 |
| `riskThreshold` | **有效风险** ≥ 此值入可疑队列（默认 50）。有效风险 = max(Agent 自报 risk, 服务器独立复判 server_risk) |
| `onlineWindowSeconds` / `recentRiskWindowSeconds` | 座位在线判定 / 热力风险统计窗口 |

环境变量可覆盖配置（便于测试/部署）：`HORUS_CONFIG` `HORUS_DATADIR` `HORUS_DBPATH` `HORUS_PSK_B64` `HORUS_KSK_B64` `HORUS_ADMIN_TOKEN` `HORUS_URLS`。

## 端点
**采集端（Agent ↔ Server）**
- `GET  /ingest/events`（WebSocket）— 握手校验 `X-Horus-Auth`；每事件校验 `sig`；幂等落库 `(agent_id,seq,type)`；risk≥阈值入可疑队列。
- `POST /ingest/images` — 校验 `X-Horus-Sig`；pHash 去重；原图存 `dataDir/images/<exam>/<seat>/<id>.webp`。
- `POST /ingest/keystroke` — 判题前端旁路，击键节奏落库 + 基础风险初判。

**看板 / 复核（只读 + 写）**
- `GET  /api/exams` · `/api/exams/{examId}/seats` · `/{examId}/suspicious?status=` · `/{examId}/events?seatId=&limit=`
- `GET  /api/images/{imageId}`（webp 字节）· `/api/images/{imageId}/meta`
- `POST /api/exams`（建考试+座位）· `/api/exams/{examId}/end` · `/api/suspicious/{id}/decide`（人工裁决）
- `POST /api/exams/{examId}/config` — 下发**配置热更新**给该考试在线 Agent（白名单/阈值/截图参数），返回 `pushedTo`；新连/重连 Agent 在 hello 时补推。

## M1 边界（见 architecture §15）
- **已实现**：ingest 落库 / 幂等去重 / 图片存盘去重 / HMAC 验签 / 可疑队列 / 看板 + 人工裁决。
- **未实现（M2/M3）**：云 OCR（L2）、Logo 模板（L3）、CLIP 向量检索、完整哈希链复验、归档作业。启动时按权威 `schema.sql` 建表，M1 跳过 `vec0` 虚表（需 sqlite-vec 扩展，属 M3）。
