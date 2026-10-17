---
type: reference
title: M4 身分層 —— OIDC 接入 · 取代共享 PSK（設計與任務計畫）
---

# M4 身分層 —— OIDC 接入 · 取代共享 PSK（設計與任務計畫）

> **2026-10-07 P147/P149 當前入口**：issuer 為 pass.betaoi.cc，hr.betaoi.cc 僅公開靜態下載頁，cn 休眠；LAN 與動態 loopback、P101 和下表的用途邊界保持。具體決定見 architecture §0 D10，狀態見 status。以下日期段保留歷史工作階段模型及訂正依據。
>
> ## ★★★ 2026-08-11 工作階段模型訂正 —— 這一段最新，先讀它
>
> 貝塔通**取消了 SSO**、工作階段判定從單一超時改成**三道門**、撤權重投改成**永不放棄**
> （其 P84–P99）。本儲存庫已按新口徑實作完畢，**345 測試全綠**。
> 跨專案通知原文：[`2026-08-10-跨项目-BetaPass会话模型重设计.md`](2026-08-10-跨项目-BetaPass会话模型重设计.md)；
> 權威在對側 `BetaPass/docs/rp-contract.md`。
>
> ### ★★ 兩個用戶端**不是同一套**，別照抄
>
> 下文（以及 §3.1 的時序圖）把 `horus-client` 與 `horus-dashboard` 當同一套 OIDC 接入描述。
> **在協定那一層它們確實一樣；在心跳這一層它們完全不同。** 混寫下去一定有人照抄錯。
>
> | | `horus-client`（採集端 Agent） | `horus-dashboard`（監考看板） |
> |---|---|---|
> | 形態 | **原生桌面 exe**（控制檯程式） | 網頁 |
> | 平台（貝塔通 P83） | `horus` | `horus-admin` |
> | 心跳通道 | ★ **重複使用既有 WS 事件上行**，不另開端點 | `POST /api/heartbeat` |
> | 心跳間隔 | 30 秒（沿用既有心跳） | 5 分鐘 |
> | `active` 從哪來 | ★★ **機器使用者活動**（Win32 `GetLastInputInfo`，`agent/Signals/UserActivity.cs`） | `visibilityState` + 最近 5 分鐘內的 `mousemove`/`keydown`/`click`/`scroll` |
> | 跨標籤選主 | **不適用**（沒有 `BroadcastChannel`） | `BroadcastChannel`，leader 廣播超時 15 秒 |
> | 「當前登入 + 一鍵退出」 | 開考橫幅 + `Ctrl+C`（控制檯沒有能放按鈕的介面） | 頂欄膠囊 + 退出按鈕 |
>
> ★★ **照抄瀏覽器那套到 Agent 上的後果**：一段永遠走不到的死程式碼，或者一個**恆為 `false` 的
> `active`** —— 後者的表現是**每個學生考到 30 分鐘就被 idle 踢掉**，而且看起來像網路問題。
>
> ### 三道門（P88–P92）
>
> | 門 | 欄位 | 本儲存庫預設 | 擋什麼 |
> |---|---|---|---|
> | 心跳 | `last_heartbeat_at` | 15 分鐘（`sessionHeartbeatMinutes`） | 關頁面 / 關瀏覽器走人 |
> | idle | `last_seen_at` | 30 分鐘（`sessionIdleMinutes`） | **頁面開著但人走了** |
> | absolute | `expires_at` | **6 小時**（`oidcSessionMinutes` / `adminSessionMinutes`，原 3 小時） | 前兩道被繞過時的最後一道；★ **任何活動都推不動它** |
>
> - 判定順序 **revoked → absolute → idle → heartbeat**；`revoked` 不在枚舉裡，因為撤權是**刪行**。
> - **被動判定**：過期發生在下一次請求進來時，**沒有定時任務、沒有輪詢**。
> - ★★ **`last_seen_at` 的寫入口只有心跳。任何業務請求都不得續 idle。**
>   看板每 5 秒輪詢一次，輪詢若算作活動，idle 那道門**永遠不會到點** ——
>   監考機上開著看板就等於永不登出。落法：admin gate **唯讀不寫**。
> - ★ 伺服器端節流 150 秒，**判據按欄位分開**（心跳夠舊 **或**（本次 active **且** idle 時間戳也夠舊））。
>   整個落在 SQL 的 WHERE + 兩個獨立 CASE 上，**故意不另寫 C# 副本** —— 那是同一判據兩個來源。
>
> ### 新增端點
>
> | 端點 | 幹什麼 |
> |---|---|
> | `GET /internal/health` | 貝塔通每 5 分鐘探活（其 P94）。**必須驗簽**（與 `/internal/revoke` 同一套）。★★ **「不實作 = 撤權通知永遠送不到」那條舊理由已不成立**（對側 `5bb16b6` 之後任何 HTTP 應答都算線上，包括沒實作時框架回的 404）；仍要實作的三條理由見 `BetapassHealthEndpoint` 類註解 |
> | `POST /api/heartbeat` | 看板心跳，收 `{ active }` 回 204 |
> | `GET /api/me` | 「當前登入：XXX」用 |
> | `GET /admin/logout` | RP-Initiated Logout：★ **先清本機，再跳**貝塔通的退出範圍二選一頁 |
> | `GET /logout/done` | 回跳落點。★★ **在這裡再清一次本機工作階段** —— 「只退出本站」上游只撤 grant，碰不到你的本機工作階段 |
>
> ### ★★★ P101：本機**有意不接**貝塔通的撤權廣播（owner 2026-08-11 拍板）
>
> **不給 `horus-client` / `horus-dashboard` 登記 `revoke_callback_url`**（對側 `d7b35b5`，其 P101 + 風險 R13）。
> 連帶：探活位址按撤權回呼**同源推導**，沒登記就**不在探活名單裡** ——
> `/internal/revoke` 與 `/internal/health` **目前一次都不會被呼叫**。
>
> - ★★ **判據不是「懶得開隧道」**：Horus Server 是監考電腦上的**區域網路筆記本**（自簽 HTTPS、無公用網路網域），
>   而那兩個端點都要求**貝塔通主動撥進來**。★ **出站 ≠ 可入站** —— 換 token / 取 userinfo / 拉 JWKS
>   全是出站、一直好用；這兩條是入站，方向反了。**這是架構選擇，不是填個表。**
> - ★★ **殘餘是有界的，而這個界是三道門削出來的**：撤權後最長 **≤ absolute（6 小時）**、
>   **且再也登不進來**（授權階段就被拒）、且 Server 只在考試期線上。
>   ★★★ 順帶帶走一條通用教訓：**這個風險從契約寫下時的 7 天縮到 6 小時是 P88 乾的，而沒人回頭重算過它** ——
>   **口徑變過之後，掛在舊口徑上的風險評估要回頭重算**，否則會為一個已經縮小十倍的風險付舊價錢。
> - ★ **觸發條件**：真需要**分鐘級**撤權時**上拉取模型**（Horus 主動去取），**不是開隧道** ——
>   拉取順著本機出站-only 的拓撲，隧道方向是逆的。⏳ 那個拉取端點目前不存在。
> - ★ **實作一個字都不刪**：兩個端點、兩道防線（驗簽 + 「權杖 `sub` == 訊息 `sub`」）與它們的迴歸
>   **全部保留**，走拉取模型那天要用同一套。兩個端點的類註解裡都標了「目前不會被呼叫」。
> - **設定**：`betapassRevokePushExpected`（預設 `false`）。它**只改預檢怎麼說話**，不關任何實作。
>
> ★★ **預檢那一項因此變成四態**，而口徑由部署聲明、**結論仍由行為決定**：
>
> | 實際發生了什麼 | `Expected=false`（P101，預設） | `Expected=true` |
> |---|---|---|
> | 從未探過 | **ok**「有意不接」+ 界限 + 觸發條件 | **warn**「多半是後台沒登記回呼」 |
> | 探活通過 | ★ **warn**「居然到達了 —— 有人登記了回呼，與聲明不符」 | ok |
> | 探活被拒 | fail | fail |
>
> ★ 第二行是這次最要緊的一格：**不能因為設定說不接就不看** —— 那就退回成設定讀數了。
>
> ### ★★ 探活權杖的專屬 `aud`（owner 2026-08-11 拍板）
>
> `/internal/revoke` 與 `/internal/health` 的權杖此前 **`aud` 完全相同**（都是本機 client_id），
> 於是契約裡「多個入站端點唯一的區分是 `aud`」對這一對**不成立**。
> owner 拍板：由貝塔通給探活權杖一個**專屬 `aud` = `<client_id>#health`**，把那句話恢復成**真不變數**。
>
> ★ **為什麼值得改，儘管已實作的三家 RP 恰好都安全**（問天錄 / betai / Horus 的 `sub` 都從權杖取）：
> 漏做「權杖 `sub` == 訊息 `sub`」是**零症狀**的，把安全性押在「每一家都讀懂並正確實作了契約」上
> 不划算 —— 成均與將來的 RP 還沒寫。
>
> **Horus 側的落法**：
>
> - ★★★ **撤權與探活的候選 `aud` 集拆開**（`BetapassRevokeVerifier._candidates` /
>   `_probeCandidates`）。**這是全部要點** —— 共用一份的話，給探活加一個可接受的 `aud`
>   會**連帶放寬 `/internal/revoke`**，方向正好反了。迴歸 `探活专属aud的令牌打撤权端点_拒` 釘住。
> - ⚠ **對側已寫好、但 2026-08-11 核即時**（`git log -S"healthProbeAudience"` 為空）**尚未提交**：
>   貝塔通的 `healthProbeAudience()`（只發 `<client_id>#health`）、P100、契約改寫，以及問天錄的跟進，
>   **都還在各自的工作樹裡等 owner**。★ 決策是 owner 拍的、程式碼也寫好了，但**「線上跑的是哪一版」
>   是另一回事** —— 讀工作樹就當「已完成實作」正是本儲存庫一再吃虧的那種假狀態。
>   本儲存庫因此**預設只認專屬值**（`oidcHealthAudienceAcceptLegacy` 預設 `false`；字面值走 `oidcHealthAudienceSuffix`），
>   把那個開關當**逃生口**：只在「Horus 已部署新版、而線上貝塔通還是舊版」那段視窗裡置 `true`。
>   ★ 兩個方向的代價都很小 —— 不匹配時探活恆 401，而 401 **算線上**，只在對側後台顯示成
>   「線上但 HTTP 401」，看得見、不靜默。`GET /api/preflight` 的 `health_audience` 會在逃生口開著時一直 warn。
> - ★ `/internal/revoke` 那條「權杖 `sub` == 訊息 `sub`」**不撤**：它降為雙保險，
>   但**本身自洽、不相依對側保持任何欄位** —— 而這次改動恰恰相依對側。兩道都留著。
>
> ### ★★ 一處與跨專案通知**不一致**的地方（已拍板，別再改回去）
>
> 那份通知的第三節寫「收到 revoke 時**兩套工作階段都要按 `sub` 清**」。**本儲存庫不這麼做**，
> 維持 `BetapassRevokeEndpoint` 既有的**按權杖 `aud` 分別處置**。
>
> - **判據**：核過對側 `src/identity/revocation/repo.ts` 的 `enqueueForCredentialChange` ——
>   它是**按 client 逐條入隊**的，所以同時持有兩個平台權限的監考員會收到**兩發**通知
>   （`aud` 各一），按 `aud` 分派本身就能清乾淨。
> - **兩套一起清的代價**：撤銷某人的監考員資格會**連帶掐斷他正在考試的採集工作階段** ——
>   而「撤監考臺不動採集面」正是 §10 裡記著的「拆平台換來的連帶好處」。
> - ⏳ **前提**：兩個用戶端**都要**在貝塔通後台登記回呼位址，少登一個就半邊失效。
>   `GET /api/preflight` 的 `post_logout` 一項只能核到本機那一半，**「對側登記了沒有」本機查不到**。
> - owner 2026-08-11 拍板保持 aud 分派。
>
> ### 取消 SSO 的連帶（P84 / P98）
>
> - **每次授權都要重新認證**：監考員進看板、學生啟動採集端，**每次都要完整輸一遍密碼**
>   （過 MFA 的還要再過一次）。loopback 回呼流程一個字沒變，但**用時變長了** ——
>   `agentcore/Identity/OidcLoginFlow.cs` 因此**不設等回呼的超時**（學生輸密碼可能要好幾分鐘）。
> - ★ **無權限頁不放「重新登入」**：再點一次要再白輸一遍密碼才被同樣拒掉。
>   但「協定/網路出錯」那一屏**仍然留著**重試連結 —— 那種情形重試確實可能成功。
> - ★ §3.1 時序圖第 4 步那句「瀏覽器已登 wentian → 免帳密」**已不成立**，
>   下文保留原文只為記住當時的判據。
>
> ### 落點（讀程式碼從這幾處進）
>
> - `server/Identity/SessionGates.cs` —— 三道門的判定核心（純函式，無後台作業）
> - `server/Identity/SessionStore.cs` / `AdminSessionStore.cs` —— `Heartbeat()` 與 `GetWithGate()`
> - `server/Identity/BetapassHealthEndpoint.cs` —— 探活接收端
> - `server/Identity/RevocationNotices.cs` —— 「你為什麼被踢」的留痕與那句人話
> - `server/Identity/AdminOidcEndpoints.cs` —— `/admin/logout` 與 `/logout/done`
> - `agent/Signals/UserActivity.cs` —— ★ 採集端自己定義的 `active`
> - `tests/{SessionGateTests,BetapassHealthTests,AdminLogoutTests}.cs` —— 迴歸
>
> ---

> ## ★★ 2026-08-07 現狀訂正 —— 上面那段之後再讀這一段
>
> **本文其餘部分停在 2026-07-03，寫的是「接入 wentian（問天錄）」那一版。**
> 問天錄已降級為普通業務系統、不再是身分提供方，Horus 現在接的是**貝塔通 BetaPass**。
> 下文的時序圖、claims、演算法、端點、RBAC 全都**不再是當前契約**。
>
> **保留原文不改寫**，因為它記著當時的判據與踩過的坑（尤其 §3.2 的憑證機制、
> §11 的灰度 runbook 仍然有效）。★ **但不要照它排工作，也不要照它寫程式碼。**
>
> ### 與下文相比，實際已經變成這樣
>
> | 項 | 本文原寫 | 當前（P147/P149，2026-10-07） | 依據 |
> |---|---|---|---|
> | IdP | 問天錄 `betaoi.cc` | **貝塔通**，issuer `https://pass.betaoi.cc` | 貝塔通 **P147/P149** |
> | 簽名演算法 | RS256（RSA-PKCS1） | **PS256（RSA-PSS）**，允許清單**只有一項** | 貝塔通 P58 |
> | 協定端點 | `/oauth/authorize`、`/oauth/token`、`/.well-known/jwks.json` | **根路徑**：`/auth`、`/token`、`/jwks`、`/session/end` | 貝塔通 `docs/oidc-mounting.md` |
> | 備用域 | 無 | `OidcEndpointBase` 保留獨立設定能力；cn 當前休眠，無活動備用入口 | 貝塔通 **P147/P149** |
>
> ⚠ ★★★ **2026-08-26 的教訓（這張表自己就是那個形態）**：上面那行 issuer 從 2026-08-23
> 起就是錯的，**而沒有任何東西會紅** —— 貝塔通 **P110** 把 issuer 從根域搬到 `pass.` 子域，
> Horus 與成均兩家 RP 的設定一處都沒跟上。★★ **失效形態不是 404**：`betaoi.cn` 當時解析到
> Fulcrum 那臺機器，`/.well-known/openid-configuration` 回一個 **`200 text/plain`**。
> ⇒ ★ 契約（`../BetaPass/docs/rp-contract.md`「雙域（P116）」）明寫
> 「★★★ **別把它抄進任何程式碼或寫死進設定的字面值裡**」——
> ★★ **本表與 `AgentConfig.OidcIssuer` 的預設值都是那句警告的反面**，
> 留著是因為考場現場要少配一項，★ **代價是它會單向腐爛 ⇒ 整合測試前必須拿線上 discovery 核一次**。
> ★ 協定端點那一行（根路徑五條）2026-08-26 逐條實測核過，**Horus 這側是對的**。
> | scope | `openid horus_profile` | **`openid profile`**（`horus_profile` 永不登記） | 貝塔通 P81 |
> | 身分欄位 | 9 項富畫像 | **3 項**：`sub` / `name` / `username` | 貝塔通 P81 |
> | 身分欄位**從哪取** | id_token（wentian 為此設了 `conformIdTokenClaims: false`） | ★★ **userinfo（`/me`）** —— 貝塔通的 id_token 裡**只有 `sub`** | 貝塔通 `docs/rp-contract.md` |
> | 看板存取資格 | `user_type == 'elder'`（本機判） | **貝塔通 `horus-admin` 平台開關**（本機不判） | 貝塔通 P83 |
> | 撤權傳播 | 無 | **`/internal/revoke`**（按 `jti` 冪等 · 按 `aud` 分別處置） | 貝塔通 P44 / P69 |
>
> ### 三條最容易照舊文做錯的
>
> 1. ★★ **`username` 不是顯示欄位，是座位標識**（`ExamDispatch.SeatFrom`）。D4 把它列在
>    「富展示資料」裡，照那個理解會連它一起刪掉，於是全場座位號退化成 `sub`。
>    它現在來自標準 `profile` 的 `preferred_username`，而貝塔通對**未設使用者名的帳號直接省略
>    這個 claim**（不發空串），所以退回 `sub` 的分支必須留著。
> 2. ★★ **D4 那套富畫像不是「暫時沒接」而是「永遠不接」**。貝塔通 §1.1 只管
>    「帳號 × 平台 → 能否訪問」，道號/境界/戰力屬問天錄的業務欄位。
>    §10 整章的 `user_type` RBAC 更是被明確點名為**要清除的反面案例**。
> 3. ★★ **D5「只認 ID Token」的演算法那一半仍成立**：RS256 與 PS256 用**同一把 RSA 公鑰**
>    都驗得通 —— 所以「順手放寬成兩種都收」不是相容性而是**安全問題**，
>    貝塔通根本簽不出 RS256，多出來那條路只服務於攻擊者。
> 4. ★★ **但 D5 的「身分從 ID Token 取」那一半已經不成立**（2026-08-07 修，見下）。
>    wentian 是**專門為本專案**設了 `conformIdTokenClaims: false`（其 `server/oidc/provider.js`，
>    註解寫著「Horus Server 純區域網路靠離線驗 id_token 拿富畫像」）；**貝塔通沒有這條豁免** ——
>    它留在上游預設值 `true`，授權碼流下 id_token 只帶 `sub` 與協定 claims。
>    照舊從 id_token 取的表現是：`Name` / `Username` **恆為空串** → `SeatFrom` 對每一個學生
>    靜默退回成 `sub`，看板與取證也沒有姓名。**不回報錯誤、不拋異常、測試全綠**
>    （測試的 id_token 是自己簽的，手動塞了那兩個 claim）。
>    ★ 現在 `OidcTokenValidator.Validate` 只給得出 `OidcSubject`（一個 `sub`），
>    姓名與使用者名**只能**經 `Userinfo.FetchAsync` 拿 —— 把它變成類型上做不到的事，而不是靠註解提醒。
>    ★ **多一次線上往返不破壞 R5 拓撲**：換 token 本來就是線上動作，離線的是**驗簽**不是取身分。
>
> ### 當前實作落點（讀程式碼從這幾處進）
>
> - `server/Identity/OidcTokenValidator.cs` —— PS256 離線驗簽 + `SigningAlg` 常數；★ 產物只有 `OidcSubject`
> - `server/Identity/Userinfo.cs` —— ★ **身分 claims 的唯一來源**（含 OIDC Core 5.3.2 的 `sub` 比對）
> - `server/Identity/AdminOidcFlow.cs` —— 監考臺登入；**存取資格判據已整個移除**，見其行內說明
> - `server/Identity/BetapassRevokeEndpoint.cs` + `BetapassRevokeVerifier.cs` —— 撤權接收端
> - `server/Config/ServerConfig.cs` —— 端點推導與 `OidcEndpointBase`（P72 雙域）
> - `tests/{OidcTests,BetapassRevokeTests,SchemaDriftTests}.cs` —— 契約迴歸
> - 對側權威：`BetaPass/docs/rp-contract.md`（**通用 RP 契約，以它為準**）


- 專案：**Horus** 區域網路考試監考系統 · 里程碑：**M4 身分層（穩健性/信任模型）**
- 日期：2026-07-02（RBAC 增補 + §11 灰度驗收清單 2026-07-03）· ★ **2026-08-07 轉接貝塔通，見文首訂正塊**
- 狀態：**採集面 OIDC + both→oidc 灰度清單/工具均已實作**；★ 接入物件已從問天錄換成**貝塔通**（PS256 / 根路徑端點 / 雙域 / 身分三項 / 平台開關存取資格 / `/internal/revoke`），**298 測試全綠**。~~wentian 兩批改動待 owner commit~~ —— 隨問天錄 OP 退役消失
- 關聯：[architecture-v0.2.md §10.1](architecture-v0.2.md)（事件通道跨身分栽贓殘留）· [api-contract-m1.md](api-contract-m1.md)
- 依據：對 `WenTian`（OIDC provider）與 `Round1`（OIDC 用戶端樣板）的唯讀調研（見文末《調研證據》）

---

## 0. 目標（要閉合什麼）

M4 用 **wentian OIDC 的 per-user 身分**取代 Horus 現有的**全場共享 PSK**，根治第三輪審查識別的兩條結構性殘留（architecture §10.1）：

- **A1 事件通道跨身分栽贓**：持共享 PSK 的學員機可對事件體填**他人 seatId/agentId** 並自洽簽名，把偽造證據栽贓到別人頭上，離線稽核用同一把共享 PSK 無法追溯真兇。
- **A2 seq 搶佔壓制**：用他人 agentId 搶先佔據其未來 seq，受害者真證據到達時撞 `(agent_id,seq)` 唯一約束被靜默丟棄。

**根因**：身分鑑權用**全場同一把 PSK**，任何持 PSK 者能冒充任意身分。**閉合手段**：每個 Agent 工作階段綁定到**一個經 wentian 認證的真實使用者**（需其 wentian 帳密，攻擊者無從冒名），且伺服器強制**事件體身分 == 工作階段認證身分**。

> Horus **不建自己的帳號體系**（與 Round1 不同——Round1 是"外部登入 + 本機帳號 + external_identities 連結"；Horus 直接**以 wentian 身分為身分**，無 complete-profile、無本機註冊）。

---

## 1. 已鎖定決策（owner 2026-07-02）

> ★ **D1 / D4 / D5 已被上面那張表取代**：IdP 換成貝塔通、富畫像整套不再散佈、演算法改 PS256。
> **D2（Server-Broker，secret 只在伺服器）與 D3（以 `sub` 為權威身分）仍然成立且更硬了。**

| # | 決策點 | 選定 | 理由 |
|---|---|---|---|
| D1 | 考場網路前提 | **能觸達公用網路 wentian**（issuer `https://betaoi.cc`） | 網頁判題本就用 wentian 生態，學員機/Server 考試期可達 |
| D2 | 用戶端拓撲 | **拓撲 A：Server-Broker** | secret 只在可信 Server、永不進學員機 exe；wentian 改動最小（不必放開 public client） |
| D3 | 身分映射 | **直接用 wentian 身分**（無座位/學號） | 考場無座位號；`sub`(UUID) 為權威身分，`seatId` 欄位改承載 wentian 帳戶名/物理機標籤 |
| D4 | 富展示資料 | **首版即富資料**：使用者名 + 暱稱 + 道號 + 境界 + 戰力 + 頭像 | 看板直接呈現真實學員畫像；需 wentian 端加自訂 claim |
| D5 | Token 使用 | **只認 ID Token（RS256 JWT，離線驗簽）** | wentian access_token 是 opaque 無 introspection；ID token 是唯一可離線驗的身分錨（Round1 亦如此） |

---

## 2. ~~wentian OIDC 現狀~~（硬約束 · 調研結論）

> ★ **整節已作廢**：問天錄不再是身分提供方，其 `oidc_clients` / 授權端點都在 M7 下線名單裡。
> 貝塔通的對應事實見 `BetaPass/docs/oidc-mounting.md` 與 `docs/rp-contract.md`。
> 原文保留只為記住當時的調研方法。

| 維度 | 現狀 | 出處 |
|---|---|---|
| 庫 | `oidc-provider` (node-oidc-provider) ^9.8.2 | `WenTian/server/oidc/provider.js:143` |
| Issuer | 公用網路 HTTPS `https://betaoi.cc`（env `OAUTH_ISSUER`） | `config/oauth.js:45`·`.env.production:31` |
| 流程 | **僅** `authorization_code` + **強制 PKCE(S256)**；無 device_code/ROPC/client_credentials/refresh | `config/oauth.js:4,52-53`·`provider.js:161-165` |
| client | **僅 confidential**（`client_secret_post`/`basic`），**不支援 public(`none`)**；寫死單 `round1` | `config/oauth.js:6,54`·`provider.js:99` |
| redirect | 精確 HTTPS 網域白名單，**無 loopback** | `config/oauth.js:8-19`·`.env.production:71` |
| ID Token | **RS256 JWT，可離線驗簽**；JWKS 端點 `/.well-known/jwks.json` | `provider.js:72-96,169` |
| access_token | **opaque**（無 introspection，僅 `/oauth/userinfo`） | `provider.js:147-151`（features 只開 revocation/userinfo） |
| claims | 僅 `sub`(隨機 UUID) / `name`(nickname‖username) / `email`(空未實作) | `server/oidc/claims.js`·`account.js:11-33` |
| 缺口 | **無 role/道號/境界/戰力/頭像 claim**；`users` 表無這些的 claim 映射 | `server/oidc/claims.js:1-19` |

**樣板（Round1）**：`openid-client` ^6.8 → `client.discovery()` 自動發現 → 授權碼+PKCE+state+nonce → `authorizationCodeGrant()` 內部離線驗 ID token 簽名 → `tokens.claims()` 取 `sub` → 存 `external_identities`。見 `Round1/server/services/auth/oidcService.ts`、`server/routes/auth.ts`。

---

## 3. 目標架構（拓撲 A：Server-Broker）

**角色**：Agent = 學員機桌面 exe（**不可信**，無長期機密）；Horus Server = 區域網路筆記本（**可信**，持 client_secret + 預置 wentian 公鑰）；wentian = 公用網路 OIDC provider。

**為什麼回呼落 Agent loopback**：Server 是純 HTTP 區域網路（無 HTTPS/網域），OIDC 規定非 localhost 回呼必須 HTTPS → 回呼只能落 Agent 的 `http://127.0.0.1:<port>/cb`（loopback 是 OIDC 對原生應用的 HTTP 例外）。code 落 loopback、secret 留 Server，兩全。

### 3.1 登入時序

```
学员机 Agent(exe)              系统浏览器            Horus Server(可信)          wentian(公网HTTPS)
  │ 1. 生成:临时密钥对(ephemeral) + PKCE code_verifier + state + nonce
  │ 2. 起 loopback 监听 127.0.0.1:PORT/cb
  │ 3. 开系统浏览器 → wentian /authorize
  │      ?client_id=horus&redirect_uri=http://127.0.0.1:PORT/cb
  │      &response_type=code&scope=openid horus_profile
  │      &code_challenge=S256(verifier)&state&nonce ───────────────────────────────────────►│
  │                            │◄── 4. 浏览器已登 wentian(判题会话)→ 免账密 → (首次)同意 ──►│
  │◄── 5. 302 → 127.0.0.1:PORT/cb?code&state ─┤                                               │
  │ 6. 校验 state;POST /oidc/exchange
  │      { code, code_verifier, agentPubKey, agentId, machineId } ─────►│                     │
  │      (不再上送 examId/seatId:examId 服务端派发=当前活跃考试,      │                     │
  │       seatId 由 OIDC 身份派生=username;无活跃考试 → no_active_exam)│                     │
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
  │◄── 11. { sessionId, examId(派发), seatId(=username), profile:{…} } ─┤                    │
  │ 12. ingest(事件/图片/击键) 用 sessionId + **私钥签名**;Server 验签+强制身份==会话身份     │
```

### 3.2 憑證機制（閉合 A1/A2 的關鍵）

- **不再有可冒名的共享 PSK**。登入時 **Agent 本機生成臨時金鑰對**，把**公鑰**隨 `/oidc/exchange` 上報（該請求由一次性 `code` 保護），Server 把公鑰綁定到**認證出的 wentian `sub`**。
- ingest 的握手與每事件簽名由 **Agent 私鑰**產生，Server 用**登記的公鑰**驗證。**私鑰永不出學員機、不過網**——LAN 嗅探者只看到公鑰與簽名，**無法偽造他人事件**（需要對應私鑰，而私鑰綁定到經 wentian 認證的工作階段）。
- Server 寫入資料庫/入隊時**強制**事件體身分（examId/該工作階段的 wentian 身分）== 工作階段身分，不符即拒 → **A1 栽贓閉合**。seq 空間歸屬該認證工作階段 → **A2 搶佔閉合**。
- **實作子決策（待定）**：每事件簽名用 **Ed25519**（簡單直白，~50k/s 足夠）或 **ECDH 派生 per-session HMAC 金鑰**（Agent/Server 各出臨時公鑰、各自 ECDH 得同一 K_sess，不傳金鑰，保留 HMAC 的低開銷）。二者都滿足"無長期機密過網"；首版建議 **Ed25519**（無需改雜湊鏈的 HMAC 結構之外的金鑰協商，最少概念）。

> **wentian ID token 只在登入時消費一次**;此後長期憑證是 **Horus 工作階段**（Server 控其有效期 = 考試時長）。故 wentian 無 refresh token **不影響**——登入後不再相依 wentian token。

---

## 4. ~~wentian 端適配清單~~（✅ 已實作 · WenTian 倉 · OIDC 測試全綠）

> ★ **整節已作廢**：那些改動隨問天錄 OP 退役一併消失（`round1` client 已於 2026-08-07 摘除）。
> 現在需要的是**在貝塔通後台登記兩個平台 + 兩個用戶端** —— 清單在
> `BetaPass/handoff/26-8-6-first.md`「第 4 步：stg 上要登記的兩家」。

**核心安全保證**：全部改動在 `OAUTH_HORUS_*` env 缺失時**完全 no-op** —— 生產（betaoi）與 Round1 集成零影響。唯一非門控項 `conformIdTokenClaims:false` 對 Round1 **純加性**（把它已請求的 name/email 補進其 id_token，Round1 本就從 id_token 讀）。

| # | 改動 | 位置 | 最終做法 |
|---|---|---|---|
| C1 | **多 client 支援 + conform 修正** | `server/oidc/provider.js` | `buildClients` 改為 `Object.entries(clients).map(...)` 遍歷所有 client 並透傳 `application_type`;provider 設定加 **`conformIdTokenClaims:false`**（否則授權碼流下 scope claims 只進 userinfo 不進 id_token，Horus 區域網路離線讀不到） |
| C2 | **註冊 `horus` client** | `config/oauth.js` + env `OAUTH_HORUS_CLIENT_ID/SECRET/REDIRECT_URIS` | `buildHorusClientEntry()` **readEnv 門控**（無 clientId 則 spread `{}` 不註冊）;grant=authorization_code·PKCE·`client_secret_post`·refresh off |
| C3 | **loopback 回呼** | `config/oauth.js` horus client | **`application_type:'native'`** + `redirectUris=["http://127.0.0.1/cb"]`;native 下 oidc-provider 做**連接埠無關**匹配（client.js:437），任意 `http://127.0.0.1:<port>/cb` 放行。native+`client_secret_post` 共存合法（schema 不禁，native 只改回呼檢核） |
| C4 | **富資料自訂 claim（D4）** | `server/oidc/claims.js` + `account.js` | scope `horus_profile` → claims `[username,nickname,dao_name,avatar,realm,realm_level,combat_power]`;`account.claims(use,scope)` **僅當 scope 含 horus_profile** 才查 `users` 行 + `calcCombatPowerForUser().total`（Round1 登入不白算戰力）;經 C1 的 conform=false 進 **id_token** |
| C5 | client DB 鏡像同步 | `server/db.js:775` | 加 `syncConfiguredOidcClient(db, oauthConfig?.clients?.horus)`（對 undefined 已 no-op） |
| env | 範本 | `.env.example` | 加 `OAUTH_HORUS_CLIENT_ID/SECRET/REDIRECT_URIS`（不填真值·全空=不啟用） |
| 測試 | 迴歸 | `server/__tests__/integration/oauthConfig.test.js` | 更新 scopes 斷言（+horus_profile）+ 新增"未配則不註冊 / 配了則 native+PKCE+confidential"兩案例 |

> C6（使用者表補 `student_id`）：owner 定"考場無學號" → **不做**（直接用 wentian 身分）。
> 若日後改走**拓撲 B（Agent public client）**才需額外放開 `token_endpoint_auth_method=none`（改 `provider.js` 硬邏輯）——**拓撲 A 不需要**。
> **部署**：wentian 側填 `OAUTH_HORUS_*` env + 重新啟動即啟用 horus client;`OAUTH_HORUS_REDIRECT_URIS` 預設 `http://127.0.0.1/cb`。

**✅ 單測**：wentian 全量 `test:server` 全綠（**75 unit + 65 integration = 585 測試**·外部倉）+ Horus **142 測試**（S1–S7 階段·含 A1/A2 端到端閉合·累計 **225 全綠**）。
**✅ 本機 live smoke 已通過（2026-07-02·真元件端到端）**：本機起真 wentian（`OAUTH_ISSUER=http://127.0.0.1:3939` 全 HTTP 免證書·載入 C1–C5）+ 真 Horus Server（authMode=oidc·啟動即從 wentian 拉 JWKS）+ **真 Horus.Agent.exe**。實測連線路徑全綠：
> - wentian 真 oidc-provider 簽發的 **id_token 內含 `horus_profile` 富 claim**（`username/nickname/dao_name/realm/combat_power` 等）——**`conformIdTokenClaims:false` 執行環境確認**（最大未知項已消除）；`aud=horus-client`、`iss`、`nonce` 均正確。
> - Agent（native·loopback 動態連接埠 56510）授權碼 + PKCE → 回呼收 code → Horus `/oidc/exchange`：**真 token 交換 + RS256 驗簽一枚真 wentian id_token + ECDH 派生 K_sess + 建工作階段**。
> - Agent 以 K_sess 握手連 WS、上報 115 事件(工作階段驗簽)；看板 `/api/exams/{id}/seats` 呈現 **`identity{sub,username,combatPower:86,realm,…}`** + `online:true`。
> 唯一 harness 折衷：wentian dev node 直連 :3939 只發佔位頁(登入/同意 SPA 由 Caddy 託管)，故 login+consent 用指令碼按 Round1 既有交互 API 走(login-interaction finalize→consent confirm→resume)——**該交互機制是 Round1 生產既有、非 M4 改動**;loopback 收 code→exchange→session 全由**真 Agent** 完成。生產走真瀏覽器+SPA 無此折衷。

---

## 5. Horus 端任務（✅ S1–S7 + A1–A3 已實作 · 142 測試全綠含 12 項 M4 新增·累計 225 全綠）

**已實作總覽**：`server/Identity/`(OidcTokenValidator·SessionStore·OidcExchange·OidcEndpoints·IngestAuth·OidcSecret/OidcJwks) + `contracts/SessionCrypto.cs`(ECDH) + ingest 改造(EventIngest/ImageIngest 工作階段驗簽 + 身分強制) + 看板富畫像;`agentcore/Identity/OidcLoginFlow.cs`(loopback 登入) + UplinkClient 工作階段簽名 + Agent Program 登入串接。**金鑰選型最終定案**:①id_token 驗簽 = **純 BCL RSA-PKCS1-SHA256**(無 JWT 相依·預置 JWKS 離線驗)②工作階段金鑰 = **ECDH-P256(BCL)→ SHA256 KDF → K_sess**,重複使用既有 HMAC 雜湊鏈(只換金鑰源·私鑰不過網)。**A1/A2 閉合已端到端鎖定**(OidcIngestAuthTests:本人事件接受 / 拿自己工作階段給他人栽贓拒 / 改 agentId 搶 seq 拒 / both 模式 PSK 共存)。

### 5.1 Server（`Horus.Server`）

| # | 任務 | 要點 |
|---|---|---|
| S1 | **OIDC 用戶端模組** | 換 token = 向 wentian `/token` POST（client_secret_post）;ID token 驗簽用 `Microsoft.IdentityModel.Tokens`（RS256）+ **預置 wentian JWKS 公鑰**（啟動時可從公用網路 wentian 拉 JWKS 快取 + kid 檢核;離線備援用設定內嵌公鑰）。檢核 iss/aud(=horus client_id)/exp/nonce |
| S2 | **工作階段儲存** | `sessionId → {sub, profile, agentPubKey, examId, machineId, issuedAt, expiresAt}`;有效期 = 考試時長上限;寫入資料庫（重新啟動存活）或記憶體+持久化。**考試派發(2026-07-03)**:新增 `RevokeByExam`（全場遠程登出即 DELETE 工作階段） |
| S3 | **`/oidc/exchange` 端點** | 收 `{code, code_verifier, agentPubKey, agentId, machineId}` → 換 token → 驗簽 → 建工作階段 → 回 `{sessionId, examId, seatId, profile}`。一次性 code 防重放。**考試派發(2026-07-03)**:examId 伺服器端指派（當前活躍考試,無則 `no_active_exam` 拒,且先於 token 交換,不白耗授權碼）;seatId := username（不安全/空退回 sub,`ExamDispatch.SeatFrom`）;配套 `GET /oidc/active-exam`（待命輪詢·公開）與 `GET /oidc/session`（工作階段探針·sessionId 即憑證） |
| S4 | **ingest 鑑權改造** | 握手/事件/圖片/擊鍵改驗**工作階段公鑰簽名**（Ed25519）而非 PSK;**強制事件體身分 == 工作階段 sub**;seq 歸屬工作階段 → 閉合 A1/A2 |
| S5 | **authMode 門** | 設定 `authMode: psk \| oidc \| both`（遷移用，見 §6）;`both` 相容舊 PSK 連接與新 OIDC 工作階段 |
| S6 | **座位/身分模型** | `seats`/`events.seat_id` 語義改為 wentian 帳戶標識;`seats` 存 `sub` + 富 profile 快照;看板展示使用者名/暱稱/道號/境界/戰力/頭像 |
| S7 | **看板/契約更新** | `/api/exams/{id}/seats` 回傳富 profile;新增 `/oidc/*` 契約文件 |

### 5.2 Agent（`Horus.Agent` / `Horus.Agent.Core`）

| # | 任務 | 要點 |
|---|---|---|
| A1 | **登入流** | 生成臨時金鑰對 + PKCE + state + nonce;起 loopback 監聽;開系統瀏覽器到 authorize URL（Server 下發或 Agent 按 config 建構）;捕獲 code;POST 給 Server `/oidc/exchange` |
| A2 | **工作階段憑證使用** | 收 `sessionId`;ingest 全通道改用**私鑰簽名** + 攜 sessionId;`UplinkClient`/`HashChain` 的簽名金鑰從 PSK 切到工作階段私鑰 |
| A3 | **工作階段過期/重登** | Horus 工作階段由 Server 定長(=考試時長);過期或未認證時重新走登入流;登入前不採集或緩衝待認證 |
| A4 | **無瀏覽器降級** | 學員機無預設瀏覽器/被限制時的降級提示（考前流程備援） |

### 5.3 測試

- **Mock OIDC provider**（仿 wentian `providerShim`）:簽發可控 ID token（RS256 測試金鑰）。
- 案例：登入流端到端;ID token 驗簽（有效/過期/錯 aud/錯 iss/壞簽名 → 拒）;工作階段綁定;**ingest 強制身分==工作階段（栽贓事件被拒 → 鎖定 A1 閉合）**;**seq 搶佔被拒（A2 閉合）**;authMode both 共存;富 claim 透傳到看板。

---

## 6. PSK → OIDC 遷移路徑（分階段·可退回）

| 階段 | authMode | 行為 | 門檻 |
|---|---|---|---|
| **M4.0（現狀）** | `psk` | 全場共享 PSK（A1/A2 殘留） | — |
| **M4.1 共存** | `both` | Server 同時接受**舊 PSK 連接**與**新 OIDC 工作階段**;新部署走 OIDC，老部署仍 PSK;可灰度 | wentian 加 horus client(C1-C5) + Horus S1-S5/A1-A3 |
| **M4.2 OIDC 強制** | `oidc` | 拒絕 PSK;身分綁工作階段，**A1/A2 閉合** | 考場穩定可達 wentian(D1) + 現場驗收 |

- **`both` 是安全網**：OIDC 基建考試當天故障時，可臨時退回 `psk` 保監考不中斷（記響亮告警）。
- **fail-closed 取捨**：`oidc` 模式下若 wentian 不可達 → Agent 無法認證 → 無法監考。緩解:考前連通性預檢 + `both` 退回 + 富 profile 快照容忍 wentian 短暫抖動（登入後不相依 wentian）。

---

## 7. 風險與殘留（誠實標註）

1. **網路相依**：考試期 wentian 必須可達（授權環節）。owner 確認公用網路可達;仍需**考前連通性預檢**;`both` 退回備援。
2. **JWKS 輪換**：Server 預置/快取的 wentian 公鑰須隨 wentian RS256 金鑰輪換更新;kid 不符時告警 + 啟動時拉新 JWKS。
3. **富 profile 快照陳舊**：境界/戰力是登入時快照，考中變化不反映（監考展示可接受;需即時則登入後調 userinfo 復取）。
4. **loopback 可用性**：學員機本機防火牆/無瀏覽器的極端環境;A4 降級 + 考前環境檢核。
5. **跨專案協同**：wentian provider 改動需 wentian 團隊配合 + 重新啟動;C1-C5 與本儲存庫解耦，可並行。
6. **仍屬檢測非預防**：OIDC 閉合的是"冒名栽贓/seq 搶佔"，**不改變** Horus 純檢測取證本質;學員用自己帳號真作弊仍靠截圖/視覺/人工裁決（架構鐵律不變）。

---

## 8. 里程碑內建議順序

1. **wentian 側**（可先行，獨立）：C1→C2→C3→C4→C5，起測試 client。
2. **Horus Server**：S1（驗簽）→S2/S3（工作階段）→S6（身分模型）→S4/S5（ingest 改造 + authMode both）→S7。
3. **Horus Agent**：A1（登入流）→A2（憑證）→A3。
4. **測試**：Mock provider + 端到端 + A1/A2 鎖定案例。
5. **灰度**：`both` 上線一場 → 驗收 → `oidc` 強制。

---

## 9. 待細化子決策（實作期定）

- **S4 簽名演算法**：Ed25519（推薦·最少概念）vs ECDH→HMAC（保留 HMAC 低開銷）。
- **C3 loopback 連接埠**：動態連接埠通配 vs 固定連接埠（取決於 wentian `parseRedirectUris` 放寬方式）。
- **C4 富資料載體**：ID token 自訂 claim（離線·快照·推薦）vs userinfo 復取（即時·需線上）。
- **工作階段有效期與續期**：定長=考試時長 vs 心跳滑動續期。

---

## 10. ~~M4·RBAC —— 角色→權限映射~~（owner 2026-07-02 拍板 + 實作進度）

> ★★ **整章的判據已被貝塔通 P83 取代，且 `user_type` 那套被明確點名為要清除的反面案例。**
> 「誰能進監考看板」現在由**貝塔通的 `horus-admin` 平台開關**回答：看板用戶端歸屬該平台、
> 採集端歸屬 `horus`，沒有該平台權限的人**在授權階段就被拒、換不到 code**，走不到 Horus 的程式碼裡。
> `AdminOidcFlow` 裡那行 `user_type == 'elder'` 已**整個刪除**，`AdminSession` 不再有任何角色欄位
> （`SchemaDriftTests` 釘著這一點）。
>
> ★ **為什麼不是「換一種本機實作」**：判據放在本機就會與身分中心分家，變成同一個問題兩個來源 ——
> 本儲存庫與貝塔通都反覆吃過這個虧。判據完全上移，正是把身分抽離出去的本意。
>
> ★ **連帶好處**：撤權變精確了 —— 可以「取消某人的監考員資格但保留他作為考生」，
> 而此前撤 `horus` 是採集端與看板一起斷。
>
> **下文保留原文**，記著當時的時序與踩過的坑（§10.3 的 R5 拓撲仍然有效且重要：
> 監考伺服器跑在監考電腦上、自簽 HTTPS、`redirect_uri` 每臺一條）。

M4 的 OIDC 只把身分用在**採集面防栽贓**（A1/A2）；身分的**授權語義**（誰是考生、誰是監考員）此前為零——看板/管理端仍靠**獨立的靜態 `adminToken`**，與 wentian 身分無任何關聯。M4·RBAC 補上這層。

### 10.1 已鎖定決策（owner 2026-07-02）

| # | 決策點 | 選定 | 說明 |
|---|---|---|---|
| R1 | 角色語義 | **弟子=參考學員(考生)·長老=監考員(有 Horus 管理端權限)** | wentian `users.role='admin'` → 監考員；`'user'` → 考生 |
| R2 | 監考員判定粒度 | **任何長老(role=admin)即監考員** | 不新增 wentian 權限鍵；`user_type='elder'` 即可 |
| R3 | 看板/管理端鑑權 | **OIDC 完全取代靜態 `adminToken`（無後門）** | 認證長老才能進管理端；弟子即使登入也拒管理端 |
| R4 | 角色載體 | **wentian `horus_profile` 新增 `user_type` claim** | `'elder'‖'disciple'`，隨 id_token 離線可讀（重複使用 `resolveIdentityUserType`） |

> **fail-closed 取捨（誠實標註·owner 已知選此）**：R3 下若考試當天 wentian 不可達 → 無法簽發/驗證長老身分 → **管理端無權杖備援、進不去**。緩解：**考前監考員先登入**（工作階段有效期 = 考試時長，登入後不再相依 wentian）+ 考前連通性預檢。若日後需應急後門，另行拍板（當前明確不留）。

### 10.2 已實作·第一批（角色載體·加性·0 警告）

**wentian 側（`WenTian` 倉·未 commit·65 套/266 案例全綠·生產/Round1 零影響）**：
- `server/oidc/claims.js`：`HORUS_PROFILE_CLAIMS` 加 `user_type`；`buildHorusProfileClaims` 輸出 `user_type`（空/非法回落 `disciple`）。
- `server/oidc/account.js`：`loadHorusProfileClaims` 查 `role` 列，重複使用 `resolveIdentityUserType` 算 `user_type`。
- `server/oidc/identityCore.js`：匯出既有 `resolveIdentityUserType`（此前未匯出）。
- 門控不變：`user_type` 僅在請求 `horus_profile` scope 時出現，Round1 的 `name/email/sub` 未動。

**Horus 側（已 build+test 綠）**：
- `OidcTokenValidator`：提取 `user_type` → `OidcClaims.UserType`；`NormalizeUserType` **僅 `"elder"` 認監考員，其餘(含缺失)一律 `disciple`**（fail-safe，絕不誤授）。
- `SessionStore`/`HorusSession`：工作階段持久化 `user_type`（schema `oidc_sessions` 加列 + `AddColumnIfMissing` 冪等遷移·舊行 NULL→`disciple`）；新增 `HorusSession.IsElder`。
- `/oidc/exchange` profile 與 `/api/exams/{id}/seats` 的 `identity` 均帶 `userType`（看板可據此標註/篩選）。
- 新增迴歸：`user_type` 提取 / 預設考生 / 未知值歸一化為考生。

### 10.3 已實作·第二批 S8/S9（監考員 OIDC 登入 + 管理端授權·**151 測試全綠（S8/S9 階段·累計 225 全綠）·真機 https smoke 通過**）

**R5 拓撲（owner 拍板：本機 + 遠端 LAN 監考工作站都要支援）**：因 wentian horus client 是 `native+loopback`（僅 `127.0.0.1` 回呼）、遠端工作站瀏覽器的 loopback 回不到伺服器，故**監考伺服器啟用自簽名 HTTPS** + wentian **新增第二個 `horus-dashboard` web client**（confidential·`redirect=https://<服务器>/cb`·authorization_code+PKCE·scope `openid horus_profile`）。遠端/本機瀏覽器均經 https 直達伺服器 `/cb`，走**標準伺服器端授權碼流**，無需每臺裝助手。**Agent 仍用原 native+loopback client 不變**。

- **✅ S8 監考員看板 OIDC 登入 + 管理端授權（OIDC 取代靜態權杖·R3）**：
  - **遷移模式** `AdminAuthMode`（`token` 預設·M1-M3 原樣 / `oidc` 長老工作階段）——與採集面 `AuthMode` 同型；**既有 admin 權杖案例零改（token 模式）**，`oidc` 模式滿足 R3 無權杖後門。
  - **端點**（`server/Identity/AdminOidcFlow.cs` + `AdminOidcEndpoints.cs`）：`GET /admin/login`（生成 state+nonce+PKCE·存記憶體 pending·302 到 wentian `/authorize`）；`GET /cb`（檢核 state 單次使用 → 換 token（dashboard secret）→ 驗 id_token（aud=horus-dashboard·nonce）→ **要求 `user_type='elder'`** → 建管理工作階段 → 種 HttpOnly cookie `horus_admin`=工作階段 id → 302 跳看板；**非長老 → 403 頁**）。
  - **管理工作階段**（`AdminSessionStore` + `admin_sessions` 表）：sessionId→{sub,userType,富畫像,issuedAt,expiresAt}·壽命 = `AdminSessionMinutes`（預設 180）；`Program.cs` admin gate 在 `oidc` 模式**檢核 admin_sessions（須 `IsElder`·未過期）**，無靜態權杖後門；`token` 模式仍走 `FixedTimeEquals`。`/api/login`（token）在 oidc 模式回傳 `use_oidc_login`；`/api/logout` 一併銷燬工作階段。
  - **HTTPS**（`server/Config/HttpsCert.cs`）：Kestrel 自簽證書（啟動 `LoadOrCreate`·SAN 含 localhost/127.0.0.1/::1/機器名 + `HttpsSanHosts`）；僅 Urls 含 https 時生效（測試 TestServer 不觸發）。
  - **公開端點** `GET /api/authmode`（gate 豁免）：前端據此切換權杖門 / OIDC 登入按鈕。
  - **測試**（`AdminOidcTests` 7 項）：長老工作階段放行 / 弟子拒 / 無工作階段拒 / 過期拒 / 靜態權杖登入退役 / `/admin/login` 302 到 wentian（PKCE+state+dashboard client）/ `/cb` 未知 state 拒。**真機 https smoke**：自簽證書生成 + Kestrel https 綁定 + `/api/authmode`=oidc + `/admin/login` 302 參數正確 + `/api/exams` 401 均驗證通過。
  - **wentian 適配（✅ C7）**：`horus-dashboard` web client（env `OAUTH_HORUS_DASHBOARD_*` 門控·未 commit·OIDC 套件 18 綠·生產/Round1/Agent client 零影響）。
- **✅ S9 看板前端接入身分畫像 + 登入改造**（`wwwroot/`）：登入門據 `/api/authmode` 切換——`oidc` 模式隱藏權杖輸入、顯示"用 wentian 帳號登入（監考員）"按鈕跳 `/admin/login`；座位抽屜繪製 `identity{userType,username,昵称,道号,境界,战力}` + **監考員/參考學員標註**（此前死程式碼，現已繪製）。

> **Horus 端部署設定**（對應 wentian dashboard client）：`adminAuthMode=oidc` · `oidcIssuer`（同採集面）· `oidcDashboardClientId=horus-dashboard` · `oidcDashboardClientSecret`(或 Enc/env `HORUS_OIDC_DASHBOARD_SECRET`) · `oidcDashboardRedirectUri=https://<服务器>/cb`（須與 wentian `OAUTH_HORUS_DASHBOARD_REDIRECT_URIS` 一條精確一致）· Urls 含 https · 可選 `httpsSanHosts=<服务器LAN IP/主机名>`。wentian 側填 `OAUTH_HORUS_DASHBOARD_CLIENT_ID/SECRET/REDIRECT_URIS` + 重新啟動。

### 10.4 M4·RBAC 殘留（誠實標註）

> ★ 這兩條的現狀：**自簽證書信任仍然成立**；**fail-closed 也仍然成立**，
> 且貝塔通停服切換當天要特別注意 —— 那段視窗內**沒有任何監考機能新登入管理端**。
> 「wentian 兩批改動未 commit」那條已隨問天錄退役消失。
- **fail-closed**：`oidc` 模式下 wentian 不可達 → 監考員無法登入管理端（無權杖後門）。緩解=考前先登入（工作階段 = 考試時長，登入後不相依 wentian）+ 連通性預檢。
- **自簽證書信任**：監考機首用需點過瀏覽器警告或預裝證書（受管機建議預裝）。
- **wentian 兩批改動未 commit**（owner 自提）：`user_type` claim（claims.js/account.js/identityCore.js）+ `horus-dashboard` web client（oauth.js/db.js/.env.example/測試）。

---

## 11. both → oidc 現場灰度驗收清單（採集面遷移 runbook）

> **適用範圍**：本清單是**採集面** `AuthMode`（psk → both → oidc）的現場遷移驗收，閉合 A1 跨身分栽贓 + A2 seq 搶佔。**與管理端 `AdminAuthMode`（token→oidc·§10 RBAC）是兩條獨立軸**，互不阻塞——可各自灰度。

### 11.1 前置條件（考前一次性）
- [ ] wentian 已部署 horus **native** client（`OAUTH_HORUS_*` env·§4）+ 重新啟動,`/.well-known/jwks.json` 公用網路可達。
- [ ] Horus config：`oidcIssuer` / `oidcClientId=horus-client` / `oidcClientSecret`(或 Enc/env) / `oidcSessionMinutes ≥ 考试时长`；建議內聯 `oidcJwksJson` 或確保啟動可拉 JWKS。
- [ ] 學員機有預設瀏覽器（OIDC 授權走系統瀏覽器·A4 無瀏覽器降級備援）。
- [ ] **考前連通性預檢**：看板「預檢」按鈕 / `GET /api/preflight` 全綠——重點看 `collect_auth`、`issuer_reachable`（fail-closed 關鍵）、`migration` 三項。

### 11.2 階段與判據

| 階段 | `authMode` | 行為 | 進入判據 |
|---|---|---|---|
| M4.0 | `psk` | 全場共享 PSK（**A1/A2 殘留**） | 現狀 |
| M4.1 灰度 | `both` | Server **同時**接受舊 PSK 連接與新 OIDC 工作階段·可逐座位遷移·隨時退回 | 前置條件全綠 |
| M4.2 強制 | `oidc` | **拒 PSK**·身分綁工作階段·**A1/A2 閉合** | 11.4 go 判據全滿足 |

### 11.3 灰度執行（現場·both 模式）
1. Horus config `authMode=both` + 重新啟動（舊 PSK 座位不受影響,照常上報）。
2. 試點 1 臺學員機走 **OIDC 登入**（Agent `oidcMode`）→ 看板該座位抽屜「採集鑑權=OIDC（已遷移）」、`identity` 畫像正確、事件/圖片/擊鍵正常。
3. 分批把學員機切到 OIDC 登入。**看板灰度可見性**：`both` 模式下**仍走 PSK 的線上座位顯示藍色「PSK」角標**（`authMode=psk`）——據此逐個盯遷移進度。
4. 全量 both：所有線上座位無「PSK」角標（都已 OIDC）。預檢 `migration` 項轉 **ok（全部 N 線上座位已走 OIDC）**。

### 11.4 切 oidc 強制（go/no-go）
**go 判據（全滿足才切）**：
- [ ] 預檢 `migration` = **ok**：`onlineOidc == onlineTotal`（`GET /api/preflight` 的 `migration.{onlineTotal,onlineOidc}` 相等·看板無「PSK」角標）。
- [ ] both 模式下**已穩定跑完至少一場**真實考試（OIDC 登入/續傳/斷線重連/看板畫像均無異常）。
- [ ] `issuer_reachable` = ok（考試期 wentian 穩定可達）。
- [ ] 現場有**物理監考**在場（fail-closed 備援：見 11.6）。

**操作**：`authMode=oidc` + 重新啟動。
**驗收**：
- [ ] 新 Agent OIDC 登入 → 採集正常；
- [ ] **PSK 連接被拒**（舊 PSK 握手 → 401 `session_required`）——A1/A2 閉合的直接證據；
- [ ] 抽查一座位：看板 `identity` 畫像正確、事件時間線連續。

### 11.5 A1/A2 閉合的現場驗證（可選·驗收增強）
- **A1（栽贓）**：拿 A 的工作階段給 B 的座位發事件（改事件體 seatId）→ 伺服器 `identity_mismatch` 拒（`OidcIngestAuthTests` 已端到端鎖定）。
- **A2（seq 搶佔）**：用他人 agentId 搶 seq → 工作階段身分不符被拒。
- 兩者在 `oidc` 模式由「事件體身分 == 工作階段身分」強制關閉；`both` 模式下**PSK 座位仍留 A1/A2 殘留**（故 go 判據要求全部遷 OIDC 才切）。

### 11.6 退回預案（fail-closed 備援）
- **考試當天 wentian 不可達 / OIDC 基建故障**：`oidc` 模式下 Agent 無法認證 → 無法採集。**臨時退回 `authMode=both`**（放開 PSK）保監考不中斷,**記響亮告警 + 事後覆盤**；極端情況回 `psk`。
- 緩解:考前連通性預檢 + 工作階段有效期 = 考試時長（登入後不相依 wentian）+ 物理監考在場。

### 11.7 工具支撐（本儲存庫已實作）
- **預檢** `GET /api/preflight`（看板「預檢」按鈕）：`migration` 項給灰度 go/no-go（both 期線上座位 OIDC 覆蓋）+ `issuer_reachable`/`collect_auth`。
- **看板灰度可見性**：`both` 模式線上 PSK 座位藍色「PSK」角標 + 座位抽屜「採集鑑權」行（OIDC 已遷移 / PSK 未遷移 / 離線）。
- **`GET /api/authmode`** 回傳 `collectAuthMode`（前端據此決定是否高亮）。

### 11.8 常見故障排查
| 現象 | 可能原因 | 處置 |
|---|---|---|
| Agent OIDC 登入失敗 | wentian 不可達 / horus client 未部署 / redirect 連接埠被佔 | 預檢 `issuer_reachable`；查 wentian `OAUTH_HORUS_*`；A4 降級 |
| 啟動即 `kid 未命中 JWKS` | wentian RS256 金鑰輪換 | 同步新 JWKS（內聯或重拉）+ 重新啟動 |
| 切 oidc 後大量座位掉線 | 還有座位沒遷就切了 | 立即回 `both`；看板補遷「PSK」角標座位後再切 |
| 座位一直顯示「PSK」角標 | 該機仍走 PSK 登入 | 切該 Agent 到 `oidcMode` 重登 |

---

## 調研證據（唯讀核驗，絕對路徑）
- wentian provider：`WenTian/server/oidc/provider.js`（init/features/PKCE/JWKS/RS256 強制）
- wentian 設定：`WenTian/config/oauth.js`（issuer/grant/scope/單 round1 client/auth_method）
- wentian claims/account：`WenTian/server/oidc/{claims.js, account.js, identityCore.js}`（sub=UUID、claims 僅 sub/name/空 email）
- wentian 生產 env：`WenTian/.env.production`（issuer=betaoi.cc、kid、round1 回呼）
- Round1 用戶端樣板：`Round1/server/services/auth/oidcService.ts`（openid-client 授權碼+PKCE、`tokens.claims()` 取 ID token）、`Round1/server/routes/auth.ts`（start/callback）
