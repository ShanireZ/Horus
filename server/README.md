# Horus.Server — 监考服务器（M1–M5 + 考试派发）

接收采集端上报（WebSocket 事件 + HTTP 图片/击键）→ 落库（SQLite + 文件系统）→ 简易 Web 看板。
与 Agent 同为 .NET 8，经 [`Horus.Contracts`](../contracts/) 共享线协议 / HMAC canonical，**哈希签名两端逐字节一致**。

> 权威设计见 [../docs/architecture-v0.2.md](../docs/architecture-v0.2.md)，接口契约见 [../docs/api-contract-m1.md](../docs/api-contract-m1.md)。

## 前置
- .NET 8 SDK（无需 Visual Studio；`dotnet build/run/test` 即可）。

## 构建 / 测试
```bash
dotnet build ../Horus.sln -c Debug     # 全量(含 Agent，走 net8.0-windows)
dotnet test  ../Horus.sln -c Debug     # 端到端测试(持续全绿·见 CI 输出)
```

## 运行
```bash
cp server.config.sample.json server.config.json   # 按现场改;生产务必配置 pskBase64
dotnet run -c Debug                                # 或运行已发布 exe
```
看板：浏览器打开 `http://<服务器IP>:8080/`。

### 配置（`server.config.json`，见 sample）
| 键 | 说明 |
|---|---|
| `urls` | Kestrel 绑定，如 `http://0.0.0.0:8080`（默认 8080，与 Agent 内置默认 / dist 成品 / 部署文档一致） |
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
| `visionMaxEdge` | 送云前降采样长边像素上限（默认 1600·压 token + 顺带剥离 EXIF/XMP/IPTC/ICC 元数据；`≤0`=直通原字节）。**原图永不出网**（§5，打码/裁剪已于 2026-07-02 移除） |

**M4 采集面 OIDC（取代共享 PSK·见 [../docs/m4-identity-oidc.md](../docs/m4-identity-oidc.md)）**
| 键 | 说明 |
|---|---|
| `authMode` | 采集面鉴权模式：`psk`（默认·M1-M3 原样）/ `oidc`（仅 OIDC 会话）/ `both`（共存·迁移期回退网） |
| `oidcIssuer` / `oidcClientId` / `oidcClientSecret`(→`Enc`) / `oidcJwksJson` / `oidcSessionMinutes` | wentian OIDC 接入：issuer、`horus-client`、client_secret（明文自动 DPAPI 加密为 `Enc`；env `HORUS_OIDC_SECRET`）、JWKS 内联（留空则启动拉取缓存）、会话有效期（默认 180min·建议 ≥ 考试时长） |

**M4·RBAC 管理端 OIDC（监考员登录·取代静态令牌·§10）**
| 键 | 说明 |
|---|---|
| `adminAuthMode` | 管理端鉴权：`token`（默认·静态 `adminToken`）/ `oidc`（仅 wentian 长老会话·无令牌后门） |
| `oidcDashboardClientId` / `oidcDashboardClientSecret`(→`Enc`) / `oidcDashboardRedirectUri` / `adminSessionMinutes` | dashboard web client（`horus-dashboard`·env `HORUS_OIDC_DASHBOARD_SECRET`）、回调（须与 wentian 注册一条精确一致·如 `https://<服务器>/cb`）、管理会话有效期 |
| `httpsCertPath` / `httpsCertPassword` / `httpsSanHosts` | 自签 HTTPS（远端监考工作站 OIDC 回调须 https；留空自动生成·SAN 自动含 localhost/127.0.0.1·`httpsSanHosts` 补服务器 LAN IP/主机名）。仅 `urls` 含 https 时生效 |

**M3 CLIP 按图搜图（provider-agnostic 嵌入器·C# 暴力余弦·**未用** sqlite-vec·见 [../docs/architecture-v0.2.md §8](../docs/architecture-v0.2.md)）**
| 键 | 说明 |
|---|---|
| `embedProvider` | 图像嵌入器：留空/`off`=关 / `onnx`（**部署默认·本地 ONNX CLIP·零出网**）/ `mock`（测试）/ `openai`（OpenAI 兼容 `/v1/embeddings`） |
| `embedOnnxModelPath` / `embedDim` / `embedBackstopMinutes` | ONNX CLIP 模型路径（留空=约定名 `model.onnx` 于 `dataDir`）、维度（默认 512·仅记录·暴力余弦无所谓维度）、后台补扫间隔 |
| `embedBaseUrl` / `embedModel` / `embedApiKey`(→`Enc`) | 仅 `embedProvider=openai` 时用（baseUrl/key 留空则复用视觉 `visionBaseUrl`/key） |

**其它**
| 键 | 说明 |
|---|---|
| `openDashboard` | 启动后自动在默认浏览器打开看板（默认 true·仅 Windows 交互式真 exe；测试宿主/服务化不弹） |
| `retentionDays` / `archiveEnabled` / `archiveScanIntervalHours` | M3 归档：考试结束多少天后转 archive（默认 30）/ 后台归档开关 / 扫描间隔（默认 6h） |

环境变量可覆盖配置（便于测试/部署）：`HORUS_CONFIG` `HORUS_DATADIR` `HORUS_DBPATH` `HORUS_PSK_B64` `HORUS_KSK_B64` `HORUS_ADMIN_TOKEN` `HORUS_URLS` `HORUS_VISION_PROVIDER` `HORUS_VISION_BASEURL` `HORUS_VISION_MODEL` `HORUS_VISION_KEY` `HORUS_OIDC_SECRET` `HORUS_OIDC_DASHBOARD_SECRET` `HORUS_EMBED_KEY`。

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
- `POST /api/exams/{examId}/search-image` — **CLIP 按图搜图**（上传一张图，C# 暴力余弦 top-N 相似证据图；需 `embedProvider` 启用）。
- `POST /api/exams`（建考试+座位）· `/api/exams/{examId}/end` · `/api/suspicious/{id}/decide`（人工裁决）
- `POST /api/exams/{examId}/logout` — **全场远程登出**：吊销该考试全部 OIDC 采集会话 + 推 `session_revoked` + 强断连接，返回 `{ ok, examId, revoked, notified }`。
- `POST /api/exams/{examId}/config` — 下发**配置热更新**给该考试在线 Agent（白名单/阈值/截图参数），返回 `pushedTo`；新连/重连 Agent 在 hello 时补推。
- `POST /api/agents/{agentId}/capture` — 监考员点名抓图：向在线 Agent 推 `capture_now`，返回 `pushed`。
- `POST /api/archive/run` — 手动触发归档作业（后台亦每 `archiveScanIntervalHours` 自动跑）。
- `GET  /api/authmode`（**公开·gate 豁免**）— 前端探测采集/管理鉴权模式（`psk|oidc|both` / `token|oidc`），据此切换令牌门 / OIDC 登录按钮，`both` 灰度期高亮仍走 PSK 的座位。
- `GET  /api/preflight` — 考前预检：鉴权配置 / wentian 可达（`issuer_reachable`）/ active 考试白名单覆盖 / both→oidc 迁移进度（`migration`）。

**M4 身份层（OIDC·非 `/api/*`，不受 admin gate）**
- `POST /oidc/exchange` — Agent 用 loopback code + PKCE verifier + ECDH 公钥换会话；**examId 服务端派发**（当前活跃考试，无则 `no_active_exam`），seatId := username。
- `GET  /oidc/active-exam`（**公开**）— Agent 待命轮询：当前是否有活跃考试（考试开始才弹登录、启采集）。
- `GET  /oidc/session` — 会话探针（头 `X-Horus-Session`）：会话是否仍有效 + 所绑考试状态（Agent 60s 兜底探"被远程登出/考试结束"）。
- `GET  /admin/login` — 监考员看板 OIDC 登录：生成 state+nonce+PKCE，302 到 wentian 授权页（dashboard client）。
- `GET  /cb` — wentian 回调：换 token → 验 id_token → **须长老**（`user_type='elder'`）→ 建管理会话 → 种 HttpOnly cookie → 跳看板；非长老 → 403。

## 实现进度（见 architecture §15）
- **M1 已实现**：ingest 落库 / 幂等去重 / 图片存盘去重 / HMAC 验签 / 可疑队列 / 看板 + 人工裁决 + Agent 采集/握手/续传/断线重连。
- **M2 已实现**：**L2 视觉 LLM 识图**(取代 OCR+Logo·provider-agnostic·小米 MiMo-V2.5)+ 服务器侧 `server_risk` 复判 + keystroke KSK 会话签名 + admin HttpOnly cookie + DB 读写分离。
- **M3 已实现**：**哈希链完整性复验**(ingest 复算 `bad_hash` 拒收 + 离线 `GET /api/exams/{id}/integrity` 审计:锚点/sig/链连续/重启边界) + **归档作业** `ArchiveService`(到龄考试关键数据转 archive 库 + 清理 live + VACUUM) + **CLIP 按图搜图已落地**(provider-agnostic 嵌入器·本地 ONNX CLIP·**C# 暴力余弦·未用 sqlite-vec**·仅嵌证据图·`POST /api/exams/{id}/search-image` + 看板灯箱)。
- **M4 已实现**：**采集面 OIDC**(wentian per-user 身份取代共享 PSK·闭合事件通道栽赃 + seq 抢占·`authMode=psk|oidc|both`) + **RBAC**(弟子=考生 / 长老=监考员·管理端 OIDC 取代静态令牌·`adminAuthMode=token|oidc`·自签 HTTPS) + **考前预检** `/api/preflight`。
- **M5 已实现**：**采集端硬化**(保活三层看门狗 + 遮蔽/挂起/能力降级/异常重启四类健康信号·服务器独立赋分入队 + 座位健康度)——纯检测·不做内核对抗。
- **考试派发（2026-07-03）**：examId 服务端派发（`/oidc/exchange` 指派活跃考试）· seatId=username · Agent 待命轮询（`/oidc/active-exam`）· 全场远程登出（`/api/exams/{id}/logout`）。
- **待做**：击键前端埋点已撤（判题走外部洛谷、无本地判题页可埋点，核心"粘贴外部代码"信号由 Agent OS 级剪贴板检测覆盖；server 侧 KSK 旁路保留待未来自控提交页）。启动时按权威 `schema.sql` 建表，跳过 `vec0` 虚表（CLIP 暴力余弦不依赖 sqlite-vec 扩展）。
