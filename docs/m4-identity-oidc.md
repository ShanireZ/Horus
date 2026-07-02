# M4 身份层 —— cpplearn OIDC 接入 · 取代共享 PSK（设计与任务计划）

- 项目：**Horus** 局域网考试监考系统 · 里程碑：**M4 身份层（健壮性/信任模型）**
- 日期：2026-07-02 · 状态：**设计已定案（owner 拍板），待实现**
- 关联：[architecture-v0.2.md §10.1](architecture-v0.2.md)（事件通道跨身份栽赃残留）· [api-contract-m1.md](api-contract-m1.md)
- 依据：对 `Cpplearn`（OIDC provider）与 `Round1`（OIDC 客户端样板）的只读调研（见文末《调研证据》）

---

## 0. 目标（要闭合什么）

M4 用 **cpplearn OIDC 的 per-user 身份**取代 Horus 现有的**全场共享 PSK**，根治第三轮审查识别的两条结构性残留（architecture §10.1）：

- **A1 事件通道跨身份栽赃**：持共享 PSK 的学员机可对事件体填**他人 seatId/agentId** 并自洽签名，把伪造证据栽赃到别人头上，离线审计用同一把共享 PSK 无法追溯真凶。
- **A2 seq 抢占压制**：用他人 agentId 抢先占据其未来 seq，受害者真证据到达时撞 `(agent_id,seq)` 唯一约束被静默丢弃。

**根因**：身份鉴权用**全场同一把 PSK**，任何持 PSK 者能冒充任意身份。**闭合手段**：每个 Agent 会话绑定到**一个经 cpplearn 认证的真实用户**（需其 cpplearn 账密，攻击者无从冒名），且服务器强制**事件体身份 == 会话认证身份**。

> Horus **不建自己的账号体系**（与 Round1 不同——Round1 是"外部登录 + 本地账号 + external_identities 链接"；Horus 直接**以 cpplearn 身份为身份**，无 complete-profile、无本地注册）。

---

## 1. 已锁定决策（owner 2026-07-02）

| # | 决策点 | 选定 | 理由 |
|---|---|---|---|
| D1 | 考场网络前提 | **能触达公网 cpplearn**（issuer `https://betaoi.cc`） | 网页判题本就用 cpplearn 生态，学员机/Server 考试期可达 |
| D2 | 客户端拓扑 | **拓扑 A：Server-Broker** | secret 只在可信 Server、永不进学员机 exe；cpplearn 改动最小（不必放开 public client） |
| D3 | 身份映射 | **直接用 cpplearn 身份**（无座位/学号） | 考场无座位号；`sub`(UUID) 为权威身份，`seatId` 字段改承载 cpplearn 账户名/物理机标签 |
| D4 | 富展示数据 | **首版即富数据**：用户名 + 昵称 + 道号 + 境界 + 战力 + 头像 | 看板直接呈现真实学员画像；需 cpplearn 端加自定义 claim |
| D5 | Token 使用 | **只认 ID Token（RS256 JWT，离线验签）** | cpplearn access_token 是 opaque 无 introspection；ID token 是唯一可离线验的身份锚（Round1 亦如此） |

---

## 2. cpplearn OIDC 现状（硬约束 · 调研结论）

| 维度 | 现状 | 出处 |
|---|---|---|
| 库 | `oidc-provider` (node-oidc-provider) ^9.8.2 | `Cpplearn/server/oidc/provider.js:143` |
| Issuer | 公网 HTTPS `https://betaoi.cc`（env `OAUTH_ISSUER`） | `config/oauth.js:45`·`.env.production:31` |
| 流程 | **仅** `authorization_code` + **强制 PKCE(S256)**；无 device_code/ROPC/client_credentials/refresh | `config/oauth.js:4,52-53`·`provider.js:161-165` |
| client | **仅 confidential**（`client_secret_post`/`basic`），**不支持 public(`none`)**；写死单 `round1` | `config/oauth.js:6,54`·`provider.js:99` |
| redirect | 精确 HTTPS 域名白名单，**无 loopback** | `config/oauth.js:8-19`·`.env.production:71` |
| ID Token | **RS256 JWT，可离线验签**；JWKS 端点 `/.well-known/jwks.json` | `provider.js:72-96,169` |
| access_token | **opaque**（无 introspection，仅 `/oauth/userinfo`） | `provider.js:147-151`（features 只开 revocation/userinfo） |
| claims | 仅 `sub`(随机 UUID) / `name`(nickname‖username) / `email`(空未实现) | `server/oidc/claims.js`·`account.js:11-33` |
| 缺口 | **无 role/道号/境界/战力/头像 claim**；`users` 表无这些的 claim 映射 | `server/oidc/claims.js:1-19` |

**样板（Round1）**：`openid-client` ^6.8 → `client.discovery()` 自动发现 → 授权码+PKCE+state+nonce → `authorizationCodeGrant()` 内部离线验 ID token 签名 → `tokens.claims()` 取 `sub` → 存 `external_identities`。见 `Round1/server/services/auth/oidcService.ts`、`server/routes/auth.ts`。

---

## 3. 目标架构（拓扑 A：Server-Broker）

**角色**：Agent = 学员机桌面 exe（**不可信**，无长期机密）；Horus Server = 局域网笔记本（**可信**，持 client_secret + 预置 cpplearn 公钥）；cpplearn = 公网 OIDC provider。

**为什么回调落 Agent loopback**：Server 是纯 HTTP 局域网（无 HTTPS/域名），OIDC 规定非 localhost 回调必须 HTTPS → 回调只能落 Agent 的 `http://127.0.0.1:<port>/cb`（loopback 是 OIDC 对原生应用的 HTTP 例外）。code 落 loopback、secret 留 Server，两全。

### 3.1 登录时序

```
学员机 Agent(exe)              系统浏览器            Horus Server(可信)          cpplearn(公网HTTPS)
  │ 1. 生成:临时密钥对(ephemeral) + PKCE code_verifier + state + nonce
  │ 2. 起 loopback 监听 127.0.0.1:PORT/cb
  │ 3. 开系统浏览器 → cpplearn /authorize
  │      ?client_id=horus&redirect_uri=http://127.0.0.1:PORT/cb
  │      &response_type=code&scope=openid horus_profile
  │      &code_challenge=S256(verifier)&state&nonce ───────────────────────────────────────►│
  │                            │◄── 4. 浏览器已登 cpplearn(判题会话)→ 免账密 → (首次)同意 ──►│
  │◄── 5. 302 → 127.0.0.1:PORT/cb?code&state ─┤                                               │
  │ 6. 校验 state;POST /oidc/exchange
  │      { code, code_verifier, agentPubKey, examId, machineId } ──────►│                     │
  │                                                                     │ 7. 持 client_secret │
  │                                                                     │  换 token ──────────►│
  │                                                                     │◄─ 8. id_token(JWT) ─┤
  │                                                                     │ 9. 预置 JWKS 离线验签│
  │                                                                     │  (iss/aud/exp/nonce)→│
  │                                                                     │  sub + 富 claims     │
  │                                                                     │ 10. 建会话:sessionId │
  │                                                                     │  ↔ {sub,profile,     │
  │                                                                     │  agentPubKey,exam,   │
  │                                                                     │  machineId, 有效期}  │
  │◄── 11. { sessionId, profile:{username,nickname,daoName,realm,combat,avatar} } ─┤          │
  │ 12. ingest(事件/图片/击键) 用 sessionId + **私钥签名**;Server 验签+强制身份==会话身份     │
```

### 3.2 凭证机制（闭合 A1/A2 的关键）

- **不再有可冒名的共享 PSK**。登录时 **Agent 本地生成临时密钥对**，把**公钥**随 `/oidc/exchange` 上报（该请求由一次性 `code` 保护），Server 把公钥绑定到**认证出的 cpplearn `sub`**。
- ingest 的握手与每事件签名由 **Agent 私钥**产生，Server 用**登记的公钥**验证。**私钥永不出学员机、不过网**——LAN 嗅探者只看到公钥与签名，**无法伪造他人事件**（需要对应私钥，而私钥绑定到经 cpplearn 认证的会话）。
- Server 落库/入队时**强制**事件体身份（examId/该会话的 cpplearn 身份）== 会话身份，不符即拒 → **A1 栽赃闭合**。seq 空间归属该认证会话 → **A2 抢占闭合**。
- **实现子决策（待定）**：每事件签名用 **Ed25519**（简单直白，~50k/s 足够）或 **ECDH 派生 per-session HMAC 密钥**（Agent/Server 各出临时公钥、各自 ECDH 得同一 K_sess，不传密钥，保留 HMAC 的低开销）。二者都满足"无长期机密过网"；首版建议 **Ed25519**（无需改哈希链的 HMAC 结构之外的密钥协商，最少概念）。

> **cpplearn ID token 只在登录时消费一次**;此后长期凭证是 **Horus 会话**（Server 控其有效期 = 考试时长）。故 cpplearn 无 refresh token **不影响**——登录后不再依赖 cpplearn token。

---

## 4. cpplearn 端适配清单（✅ 已实现 · Cpplearn 仓 · OIDC 测试全绿）

**核心安全保证**：全部改动在 `OAUTH_HORUS_*` env 缺失时**完全 no-op** —— 生产（betaoi）与 Round1 集成零影响。唯一非门控项 `conformIdTokenClaims:false` 对 Round1 **纯加性**（把它已请求的 name/email 补进其 id_token，Round1 本就从 id_token 读）。

| # | 改动 | 位置 | 最终做法 |
|---|---|---|---|
| C1 | **多 client 支持 + conform 修正** | `server/oidc/provider.js` | `buildClients` 改为 `Object.entries(clients).map(...)` 遍历所有 client 并透传 `application_type`;provider 配置加 **`conformIdTokenClaims:false`**（否则授权码流下 scope claims 只进 userinfo 不进 id_token，Horus 局域网离线读不到） |
| C2 | **注册 `horus` client** | `config/oauth.js` + env `OAUTH_HORUS_CLIENT_ID/SECRET/REDIRECT_URIS` | `buildHorusClientEntry()` **readEnv 门控**（无 clientId 则 spread `{}` 不注册）;grant=authorization_code·PKCE·`client_secret_post`·refresh off |
| C3 | **loopback 回调** | `config/oauth.js` horus client | **`application_type:'native'`** + `redirectUris=["http://127.0.0.1/cb"]`;native 下 oidc-provider 做**端口无关**匹配（client.js:437），任意 `http://127.0.0.1:<port>/cb` 放行。native+`client_secret_post` 共存合法（schema 不禁，native 只改回调校验） |
| C4 | **富数据自定义 claim（D4）** | `server/oidc/claims.js` + `account.js` | scope `horus_profile` → claims `[username,nickname,dao_name,avatar,realm,realm_level,combat_power]`;`account.claims(use,scope)` **仅当 scope 含 horus_profile** 才查 `users` 行 + `calcCombatPowerForUser().total`（Round1 登录不白算战力）;经 C1 的 conform=false 进 **id_token** |
| C5 | client DB 镜像同步 | `server/db.js:775` | 加 `syncConfiguredOidcClient(db, oauthConfig?.clients?.horus)`（对 undefined 已 no-op） |
| env | 模板 | `.env.example` | 加 `OAUTH_HORUS_CLIENT_ID/SECRET/REDIRECT_URIS`（不填真值·全空=不启用） |
| 测试 | 回归 | `server/__tests__/integration/oauthConfig.test.js` | 更新 scopes 断言（+horus_profile）+ 新增"未配则不注册 / 配了则 native+PKCE+confidential"两用例 |

> C6（用户表补 `student_id`）：owner 定"考场无学号" → **不做**（直接用 cpplearn 身份）。
> 若日后改走**拓扑 B（Agent public client）**才需额外放开 `token_endpoint_auth_method=none`（改 `provider.js` 硬逻辑）——**拓扑 A 不需要**。
> **部署**：cpplearn 侧填 `OAUTH_HORUS_*` env + 重启即启用 horus client;`OAUTH_HORUS_REDIRECT_URIS` 默认 `http://127.0.0.1/cb`。

**✅ 验证**：cpplearn 全量 `test:server` 全绿（**75 unit + 65 integration = 585 测试**，含新增 OIDC 用例）；改动模块冒烟加载无循环依赖。
**⚠️ 上线前 live smoke（建议）**：cpplearn 集成测试走 `providerShim`（测试替身），**未**用真 oidc-provider 跑「horus native client 授权码 → token → 解 id_token 验富 claim」的完整链。上线前应用**真 RSA 密钥 + 真 provider**做一次 live smoke，确认 `conformIdTokenClaims:false` 下 `horus_profile` claims 确实落入 id_token、且 native loopback 动态端口回调放行。

---

## 5. Horus 端任务（✅ S1–S7 + A1–A3 已实现 · 142 测试全绿含 12 项 M4 新增）

**已实现总览**：`server/Identity/`(OidcTokenValidator·SessionStore·OidcExchange·OidcEndpoints·IngestAuth·OidcSecret/OidcJwks) + `contracts/SessionCrypto.cs`(ECDH) + ingest 改造(EventIngest/ImageIngest 会话验签 + 身份强制) + 看板富画像;`agentcore/Identity/OidcLoginFlow.cs`(loopback 登录) + UplinkClient 会话签名 + Agent Program 登录接线。**密钥选型最终定案**:①id_token 验签 = **纯 BCL RSA-PKCS1-SHA256**(无 JWT 依赖·预置 JWKS 离线验)②会话密钥 = **ECDH-P256(BCL)→ SHA256 KDF → K_sess**,复用既有 HMAC 哈希链(只换密钥源·私钥不过网)。**A1/A2 闭合已端到端锁定**(OidcIngestAuthTests:本人事件接受 / 拿自己会话给他人栽赃拒 / 改 agentId 抢 seq 拒 / both 模式 PSK 共存)。

### 5.1 Server（`Horus.Server`）

| # | 任务 | 要点 |
|---|---|---|
| S1 | **OIDC 客户端模块** | 换 token = 向 cpplearn `/token` POST（client_secret_post）;ID token 验签用 `Microsoft.IdentityModel.Tokens`（RS256）+ **预置 cpplearn JWKS 公钥**（启动时可从公网 cpplearn 拉 JWKS 缓存 + kid 校验;离线兜底用配置内嵌公钥）。校验 iss/aud(=horus client_id)/exp/nonce |
| S2 | **会话存储** | `sessionId → {sub, profile, agentPubKey, examId, machineId, issuedAt, expiresAt}`;有效期 = 考试时长上限;落库（重启存活）或内存+持久化 |
| S3 | **`/oidc/exchange` 端点** | 收 `{code, code_verifier, agentPubKey, examId, machineId}` → 换 token → 验签 → 建会话 → 回 `{sessionId, profile}`。一次性 code 防重放 |
| S4 | **ingest 鉴权改造** | 握手/事件/图片/击键改验**会话公钥签名**（Ed25519）而非 PSK;**强制事件体身份 == 会话 sub**;seq 归属会话 → 闭合 A1/A2 |
| S5 | **authMode 门** | 配置 `authMode: psk \| oidc \| both`（迁移用，见 §6）;`both` 兼容旧 PSK 连接与新 OIDC 会话 |
| S6 | **座位/身份模型** | `seats`/`events.seat_id` 语义改为 cpplearn 账户标识;`seats` 存 `sub` + 富 profile 快照;看板展示用户名/昵称/道号/境界/战力/头像 |
| S7 | **看板/契约更新** | `/api/exams/{id}/seats` 返回富 profile;新增 `/oidc/*` 契约文档 |

### 5.2 Agent（`Horus.Agent` / `Horus.Agent.Core`）

| # | 任务 | 要点 |
|---|---|---|
| A1 | **登录流** | 生成临时密钥对 + PKCE + state + nonce;起 loopback 监听;开系统浏览器到 authorize URL（Server 下发或 Agent 按 config 构造）;捕获 code;POST 给 Server `/oidc/exchange` |
| A2 | **会话凭证使用** | 收 `sessionId`;ingest 全通道改用**私钥签名** + 携 sessionId;`UplinkClient`/`HashChain` 的签名密钥从 PSK 切到会话私钥 |
| A3 | **会话过期/重登** | Horus 会话由 Server 定长(=考试时长);过期或未认证时重新走登录流;登录前不采集或缓冲待认证 |
| A4 | **无浏览器降级** | 学员机无默认浏览器/被限制时的降级提示（考前流程兜底） |

### 5.3 测试

- **Mock OIDC provider**（仿 cpplearn `providerShim`）:签发可控 ID token（RS256 测试密钥）。
- 用例：登录流端到端;ID token 验签（有效/过期/错 aud/错 iss/坏签名 → 拒）;会话绑定;**ingest 强制身份==会话（栽赃事件被拒 → 锁定 A1 闭合）**;**seq 抢占被拒（A2 闭合）**;authMode both 共存;富 claim 透传到看板。

---

## 6. PSK → OIDC 迁移路径（分阶段·可回退）

| 阶段 | authMode | 行为 | 门槛 |
|---|---|---|---|
| **M4.0（现状）** | `psk` | 全场共享 PSK（A1/A2 残留） | — |
| **M4.1 共存** | `both` | Server 同时接受**旧 PSK 连接**与**新 OIDC 会话**;新部署走 OIDC，老部署仍 PSK;可灰度 | cpplearn 加 horus client(C1-C5) + Horus S1-S5/A1-A3 |
| **M4.2 OIDC 强制** | `oidc` | 拒绝 PSK;身份绑会话，**A1/A2 闭合** | 考场稳定可达 cpplearn(D1) + 现场验收 |

- **`both` 是安全网**：OIDC 基建考试当天故障时，可临时回退 `psk` 保监考不中断（记响亮告警）。
- **fail-closed 取舍**：`oidc` 模式下若 cpplearn 不可达 → Agent 无法认证 → 无法监考。缓解:考前连通性预检 + `both` 回退 + 富 profile 快照容忍 cpplearn 短暂抖动（登录后不依赖 cpplearn）。

---

## 7. 风险与残留（诚实标注）

1. **网络依赖**：考试期 cpplearn 必须可达（授权环节）。owner 确认公网可达;仍需**考前连通性预检**;`both` 回退兜底。
2. **JWKS 轮换**：Server 预置/缓存的 cpplearn 公钥须随 cpplearn RS256 密钥轮换更新;kid 不符时告警 + 启动时拉新 JWKS。
3. **富 profile 快照陈旧**：境界/战力是登录时快照，考中变化不反映（监考展示可接受;需实时则登录后调 userinfo 复取）。
4. **loopback 可用性**：学员机本地防火墙/无浏览器的极端环境;A4 降级 + 考前环境校验。
5. **跨项目协同**：cpplearn provider 改动需 cpplearn 团队配合 + 重启;C1-C5 与本仓解耦，可并行。
6. **仍属检测非预防**：OIDC 闭合的是"冒名栽赃/seq 抢占"，**不改变** Horus 纯检测取证本质;学员用自己账号真作弊仍靠截图/视觉/人工裁决（架构铁律不变）。

---

## 8. 里程碑内建议顺序

1. **cpplearn 侧**（可先行，独立）：C1→C2→C3→C4→C5，起测试 client。
2. **Horus Server**：S1（验签）→S2/S3（会话）→S6（身份模型）→S4/S5（ingest 改造 + authMode both）→S7。
3. **Horus Agent**：A1（登录流）→A2（凭证）→A3。
4. **测试**：Mock provider + 端到端 + A1/A2 锁定用例。
5. **灰度**：`both` 上线一场 → 验收 → `oidc` 强制。

---

## 9. 待细化子决策（实现期定）

- **S4 签名算法**：Ed25519（推荐·最少概念）vs ECDH→HMAC（保留 HMAC 低开销）。
- **C3 loopback 端口**：动态端口通配 vs 固定端口（取决于 cpplearn `parseRedirectUris` 放宽方式）。
- **C4 富数据载体**：ID token 自定义 claim（离线·快照·推荐）vs userinfo 复取（实时·需在线）。
- **会话有效期与续期**：定长=考试时长 vs 心跳滑动续期。

---

## 调研证据（只读核验，绝对路径）
- cpplearn provider：`Cpplearn/server/oidc/provider.js`（init/features/PKCE/JWKS/RS256 强制）
- cpplearn 配置：`Cpplearn/config/oauth.js`（issuer/grant/scope/单 round1 client/auth_method）
- cpplearn claims/account：`Cpplearn/server/oidc/{claims.js, account.js, identityCore.js}`（sub=UUID、claims 仅 sub/name/空 email）
- cpplearn 生产 env：`Cpplearn/.env.production`（issuer=betaoi.cc、kid、round1 回调）
- Round1 客户端样板：`Round1/server/services/auth/oidcService.ts`（openid-client 授权码+PKCE、`tokens.claims()` 取 ID token）、`Round1/server/routes/auth.ts`（start/callback）
