# Horus.Server — 监考服务器（M1）

接收采集端上报（WebSocket 事件 + HTTP 图片/击键）→ 落库（SQLite + 文件系统）→ 简易 Web 看板。
与 Agent 同为 .NET 8，经 [`Horus.Contracts`](../contracts/) 共享线协议 / HMAC canonical，**哈希签名两端逐字节一致**。

> 权威设计见 [../docs/architecture-v0.2.md](../docs/architecture-v0.2.md)，接口契约见 [../docs/api-contract-m1.md](../docs/api-contract-m1.md)。

## 前置
- .NET 8 SDK（无需 Visual Studio；`dotnet build/run/test` 即可）。

## 构建 / 测试
```bash
dotnet build ../Horus.sln -c Debug     # 全量(含 Agent，走 net8.0-windows)
dotnet test  ../Horus.sln -c Debug     # 端到端测试(130 项)
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
| `visionProvider` | 视觉分析（L2·识图取代 OCR）:留空/`off`=关 · `mock` · `openai`（OpenAI 兼容·换 `visionBaseUrl`+`visionModel` 即换供应商）。**已选定并真机验证 = 小米 MiMo-V2.5 托管 API**（`token-plan-cn.xiaomimimo.com/v1`·`visionModel=mimo-v2.5` 小写） |
| `visionApiKey` → `visionApiKeyEnc` | **直接把明文 key 填进 `visionApiKey`,启动即自动 DPAPI 加密为 `visionApiKeyEnc` 并清空明文（密文回写文件·保留注释）**。也可 `Horus.Server protect-secret <key>` 预生成密文填 `visionApiKeyEnc`。联调可用 `HORUS_VISION_KEY` env 注入明文（优先级最高·不落盘） |
| `visionConfidenceThreshold` / `visionAnalyzeBaseline` | 视觉命中入队置信度阈值（默认 60）/ 是否也分析随机基线图（默认否·§5 最小化） |

环境变量可覆盖配置（便于测试/部署）：`HORUS_CONFIG` `HORUS_DATADIR` `HORUS_DBPATH` `HORUS_PSK_B64` `HORUS_KSK_B64` `HORUS_ADMIN_TOKEN` `HORUS_URLS` `HORUS_VISION_PROVIDER` `HORUS_VISION_BASEURL` `HORUS_VISION_MODEL` `HORUS_VISION_KEY`。

## 端点
**采集端（Agent ↔ Server）**
- `GET  /ingest/events`（WebSocket）— 握手校验 `X-Horus-Auth`；每事件校验 `sig` + **哈希链复算**(`bad_hash` 拒收)；幂等落库 `(agent_id,seq)`；**服务器独立复判 `server_risk`**，有效风险≥阈值入可疑队列。
- `POST /ingest/images` — 校验 `X-Horus-Sig`；pHash 去重；原图存 `dataDir/images/<exam>/<seat>/<id>.webp`；触发型异步送**视觉 LLM 分析**(L2)。
- `POST /ingest/keystroke` — 判题后端旁路，**KSK 会话签名**(`X-Horus-KSig`)防伪造/栽赃 + 幂等防重放；击键节奏落库 + 基础风险初判。

**看板 / 复核（只读 + 写）**
- `GET  /api/exams` · `/api/exams/{examId}/seats` · `/{examId}/suspicious?status=` · `/{examId}/events?seatId=&limit=`
- `GET  /api/images/{imageId}`（webp 字节）· `/api/images/{imageId}/meta` — live 未命中**自动回落 archive 库 + 冷存**（归档考试证据仍可取证）。
- `GET  /api/exams/{examId}/integrity` — 哈希链完整性离线审计（`sigVerified` 标注是否验签；psk 未配时 `ok` 仅表锚点自洽+链连续）。
- `GET  /api/archive/exams/{examId}` — 归档考试只读复核（汇总 + 裁决 + 关键事件 + 证据图列表）。
- `POST /api/exams`（建考试+座位）· `/api/exams/{examId}/end` · `/api/suspicious/{id}/decide`（人工裁决）
- `POST /api/exams/{examId}/config` — 下发**配置热更新**给该考试在线 Agent（白名单/阈值/截图参数），返回 `pushedTo`；新连/重连 Agent 在 hello 时补推。
- `POST /api/agents/{agentId}/capture` — 监考员点名抓图：向在线 Agent 推 `capture_now`，返回 `pushed`。
- `POST /api/archive/run` — 手动触发归档作业（后台亦每 `archiveScanIntervalHours` 自动跑）。

## 实现进度（见 architecture §15）
- **M1 已实现**：ingest 落库 / 幂等去重 / 图片存盘去重 / HMAC 验签 / 可疑队列 / 看板 + 人工裁决 + Agent 采集/握手/续传/断线重连。
- **M2 已实现**：**L2 视觉 LLM 识图**(取代 OCR+Logo·provider-agnostic·小米 MiMo-V2.5)+ 服务器侧 `server_risk` 复判 + keystroke KSK 会话签名 + admin HttpOnly cookie + DB 读写分离。
- **M3 已实现**：**哈希链完整性复验**(ingest 复算 `bad_hash` 拒收 + 离线 `GET /api/exams/{id}/integrity` 审计:锚点/sig/链连续/重启边界) + **归档作业** `ArchiveService`(到龄考试关键数据转 archive 库 + 清理 live + VACUUM)。
- **待做**：CLIP 按图搜图(需 sqlite-vec + CLIP ONNX,`vec_images` 虚表预留但 M1 起跳过) + 击键前端埋点(判题网页,本仓外)。启动时按权威 `schema.sql` 建表，跳过 `vec0` 虚表（需 sqlite-vec 扩展）。
