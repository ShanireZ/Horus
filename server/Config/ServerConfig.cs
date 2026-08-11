using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Server.Config;

/// 服务器配置(从 server.config.json 加载,camelCase)。record 便于用 with 施加环境变量覆盖。
public sealed record ServerConfig
{
    /// Kestrel 绑定地址(可多个,逗号分隔),如 "http://0.0.0.0:8080"。默认 8080 与 Agent 内置默认 / dist 成品 / 部署文档一致。
    public string Urls { get; init; } = "http://0.0.0.0:8080";

    /// 数据根目录:SQLite 文件与截图原图都在此下。
    public string DataDir { get; init; } = "./data";

    /// SQLite 文件路径;":memory:" 走内存(测试用)。相对路径相对 DataDir。
    public string DbPath { get; init; } = "horus.db";

    /// 预共享 HMAC 密钥(base64)。与 Agent 同一把。留空则**关闭验签**(仅本地联调,生产必须配)。
    public string? PskBase64 { get; init; }

    /// 管理/看板令牌。所有 /api/* 请求需带 X-Horus-Admin 头(图片字节端点可用 ?t= 查询)。
    /// 留空则关闭管理鉴权(仅本地联调)。防止学员机调 /api/exams/{id}/config 关掉全场检测。
    public string? AdminToken { get; init; }

    /// 击键旁路密钥(base64)。判题后端持它对 /ingest/keystroke 提交体签名(X-Horus-KSig)。
    /// 留空则**关闭击键鉴权**(仅本地联调)。防同网学员机伪造/栽赃他人击键样本。与采集 PSK / 管理令牌相互独立。
    public string? KeystrokeSecretBase64 { get; init; }

    /// 允许在非 loopback 绑定下缺 PSK / 管理令牌启动(裸奔)。默认 false = fail-closed。仅联调开。
    public bool AllowInsecure { get; init; }

    /// 启动成功后自动在默认浏览器打开管理端看板(仅 Windows 交互式运行的真 exe;测试宿主/输出重定向/服务化不弹)。
    public bool OpenDashboard { get; init; } = true;

    /// Cloudflare Web Analytics 公开 site token。只在 betaoi.cn 正式主机手动加载；LAN/IP 下不出网。
    public string? CloudflareWebAnalyticsToken { get; init; } = "c113fb69d7e84d38a645c5160f6f1bda";

    // ---- M4 身份层:wentian OIDC 取代共享 PSK(见 docs/m4-identity-oidc.md)----
    /// 采集面鉴权模式:"psk"(默认·共享 PSK·M1-M3 原样) | "oidc"(仅 OIDC 会话) | "both"(共存·迁移期回退网)。
    public string AuthMode { get; init; } = "psk";
    /// 贝塔通 OIDC issuer(生产为 `https://betaoi.cn`)。OIDC 模式必配。
    /// ★★ **issuer 是标识符不是地址**(贝塔通 P72):令牌里的 `iss` 恒为它,`iss` 校验也永远对它 ——
    ///   即便走备用域 `.cc` 进来也不变。**不要**为了走备用域去改这一项,那会让所有令牌验不过。
    ///   要换入口改 <see cref="OidcEndpointBase"/>。
    public string? OidcIssuer { get; init; }
    /// 协议端点的**取回地址**前缀。留空 = 用 issuer。
    /// ★ 贝塔通 P72:`.cc` 是**同一个 issuer 的第二条入口** —— 端点整套走 `.cc`,而 `iss` 仍是 `.cn`。
    ///   主域不可达时把这一项填成 `https://betaoi.cc` 即可,issuer 一个字都不用动。
    /// ★ **不要走 discovery 自动发现**:从 `.cc` 拉回的文档里 `issuer` 与取回地址不一致,
    ///   合规的 OIDC 库会当场拒绝;本类是手写端点配置,正合这条口径。
    public string? OidcEndpointBase { get; init; }
    /// Horus 在 wentian 注册的 client_id(默认 horus-client)。
    public string? OidcClientId { get; init; }
    /// client_secret 明文(仅联调;生产用 OidcClientSecretEnc 或 env HORUS_OIDC_SECRET)。Server-Broker:secret 只在服务器。
    public string? OidcClientSecret { get; init; }
    /// client_secret DPAPI 密文(与视觉 key 同机制,见 SecretProtect)。
    public string? OidcClientSecretEnc { get; init; }
    /// wentian 的 JWKS(RSA 公钥)内联 JSON:局域网离线验 id_token 用,免运行时拉取。留空则启动时从 issuer 拉取 + 缓存。
    public string? OidcJwksJson { get; init; }
    /// 采集会话的 **absolute** 门(分钟):建会话时写死的到期时刻。★ **任何活动都推不动它**。
    /// 默认 **360(6 小时)**,与贝塔通给的默认值一致(其 P88)。
    ///
    /// ★★ **它必须覆盖得住一整场考试**:考试 2–3 小时,而学员往往在开考前就登录了。
    ///   不够长的表现是**考到一半被踢**,而重登要完整走一遍浏览器授权(取消 SSO 后还要输密码)。
    ///   开考预检(其 P91)因此改为 **Horus 自己算自己的剩余** —— 原设计靠 IdP 下发一个 claim
    ///   告诉 RP「你的会话还剩多久」,那条随取消 SSO 一并作废。
    /// ★ 各平台**完全自由调这三个值、不登记、贝塔通不拦**(其 P90 已取消原来那条
    ///   「RP 的 absolute 不得超过 IdP 的」)。兜底改由永不放弃的撤权重投承担。
    public int OidcSessionMinutes { get; init; } = 360;

    // ---- 三道门的另外两道(贝塔通 P88–P92·两条链路共用) ----
    // ★ 采集端与看板**共用这两个值**:三道门的数值口径对两者相同,不同的只是「谁来发心跳、
    //   `active` 怎么算」(看板用 visibilityState + 输入事件;采集端用机器用户活动)。
    /// **idle** 门(分钟):距最后一次「人还在」多久算失效。默认 30。挡的是**页面开着但人走了**。
    /// ★ `last_seen_at` **只由带 `active: true` 的心跳更新**,任何业务请求都不续它(其 P92)。
    public int SessionIdleMinutes { get; init; } = 30;
    /// **心跳**门(分钟):距最后一次收到心跳多久算失效。默认 15(= 5 分钟间隔 × 容忍 3 次)。
    /// 挡的是**关页面 / 关浏览器走人**;与 idle 挡的是两件事,**不要合并**。
    public int SessionHeartbeatMinutes { get; init; } = 15;

    /// **开考预检**用的预计考试时长(分钟),默认 180。★ 它**不参与任何鉴权判定**,
    /// 只用来回答一个问题:「你这次登录的剩余寿命够不够撑完一场考试」(贝塔通 P91)。
    ///
    /// ★★ 原设计是**靠 IdP 下发一个 claim** 告诉 RP「你的会话还剩多久」,
    ///   **取消 SSO(其 P84)之后那条已作废** —— 改为 Horus 按自己的 absolute 自己算。
    /// ★ 两个客户端上它的意义不同,见 `docs/m4-identity-oidc.md`:
    ///   采集端每场考试都是**新登录**,所以剩余恒为满,这个检查真正能逮到的是
    ///   **`oidcSessionMinutes` 配短了**;监考看板那边监考员可能几小时前就登录了,
    ///   剩余不足是会真实发生的。
    public int ExpectedExamMinutes { get; init; } = 180;

    [JsonIgnore]
    public bool OidcEnabled => AuthMode is "oidc" or "both";
    [JsonIgnore]
    public bool PskAcceptedForIngest => AuthMode is "psk" or "both";
    /// 端点取回地址的前缀:优先 <see cref="OidcEndpointBase"/>,留空则回落 issuer(P72)。
    [JsonIgnore]
    private string? EndpointBase =>
        (string.IsNullOrEmpty(OidcEndpointBase) ? OidcIssuer : OidcEndpointBase)?.TrimEnd('/');

    // ★★ 贝塔通的协议端点**挂在根路径**(其 docs/oidc-mounting.md:`/auth` `/token` `/me` `/jwks`
    //    `/session/end` `/request` 已占用根命名空间),**不是** 旧 wentian 的 `/oauth/*`。
    //    照旧路径拼会得到 404,而表现是「token 端点非 200」—— 读起来像 IdP 挂了,不像路径拼错。
    /// OIDC token 端点。
    [JsonIgnore]
    public string? OidcTokenEndpoint => EndpointBase is null ? null : EndpointBase + "/token";
    /// OIDC 授权端点(建监考员登录 URL 用)。
    [JsonIgnore]
    public string? OidcAuthorizeEndpoint => EndpointBase is null ? null : EndpointBase + "/auth";
    /// JWKS 端点(未内联 OidcJwksJson 时启动拉取用)。
    [JsonIgnore]
    public string? OidcJwksEndpoint => EndpointBase is null ? null : EndpointBase + "/jwks";
    /// userinfo 端点。★ **身份 claims 只在这里拿得到**,id_token 里没有(见 <see cref="Identity.Userinfo"/>)。
    [JsonIgnore]
    public string? OidcUserinfoEndpoint => EndpointBase is null ? null : EndpointBase + "/me";
    /// RP-Initiated Logout 端点(贝塔通 P24:RP 退出连带退掉 IdP 会话)。
    [JsonIgnore]
    public string? OidcEndSessionEndpoint => EndpointBase is null ? null : EndpointBase + "/session/end";

    // ---- M4·RBAC:监考员看板 OIDC 登录(wentian dashboard web client·取代静态 adminToken·见 m4-identity-oidc.md §10)----
    /// 管理端鉴权模式:"token"(默认·静态 adminToken·M1-M3 原样) | "oidc"(仅 wentian 长老 OIDC 会话·R3 无令牌后门)。
    public string AdminAuthMode { get; init; } = "token";
    /// wentian dashboard web client_id(如 horus-dashboard)。AdminAuthMode=oidc 必配。
    public string? OidcDashboardClientId { get; init; }
    /// dashboard client_secret 明文(仅联调;生产用 Enc 或 env HORUS_OIDC_DASHBOARD_SECRET)。Server 持有,浏览器从不经手。
    public string? OidcDashboardClientSecret { get; init; }
    /// dashboard client_secret DPAPI 密文(与视觉/采集 secret 同机制)。
    public string? OidcDashboardClientSecretEnc { get; init; }
    /// dashboard 回调 URI(须与 wentian 注册的 OAUTH_HORUS_DASHBOARD_REDIRECT_URIS 一条精确一致,如 https://<服务器>/cb)。
    public string? OidcDashboardRedirectUri { get; init; }
    /// 登出回跳地址(RP-Initiated Logout 的 `post_logout_redirect_uri`)。
    /// 留空则按 <see cref="OidcDashboardRedirectUri"/> **同源**推导 `/logout/done`。
    ///
    /// ★★ **必须在贝塔通后台的「登出回跳地址」一栏登记**,且与这里**精确一致** ——
    ///   没登记时上游会忽略它,用户会停在贝塔通自己的「已退出」页,回不到 Horus。
    ///   ★ 那**不算故障**:本地会话在跳走**之前**就已经清掉了(见 AdminOidcEndpoints 的
    ///   `/admin/logout`),用户确实已经退出,只是少了一次回跳。
    public string? OidcPostLogoutRedirectUri { get; init; }
    /// 管理会话的 **absolute** 门(分钟):监考员登录后凭证寿命。默认 **360(6 小时)**,同采集面。
    /// idle 与心跳两道门走共用的 <see cref="SessionIdleMinutes"/> / <see cref="SessionHeartbeatMinutes"/>。
    public int AdminSessionMinutes { get; init; } = 360;

    [JsonIgnore]
    public bool DashboardOidcEnabled => string.Equals(AdminAuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

    /// 实际使用的登出回跳地址:显式配置优先,否则按 dashboard 回调**同源**推导 `/logout/done`。
    /// 两者都取不到时返回 null —— 那时登出仍然照走,只是不带 `post_logout_redirect_uri`。
    [JsonIgnore]
    public string? PostLogoutRedirectUriEffective
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OidcPostLogoutRedirectUri)) return OidcPostLogoutRedirectUri;
            if (string.IsNullOrWhiteSpace(OidcDashboardRedirectUri)) return null;
            return Uri.TryCreate(OidcDashboardRedirectUri, UriKind.Absolute, out Uri? u)
                ? new Uri(u, "/logout/done").ToString() : null;
        }
    }

    // ---- HTTPS(远端监考工作站 OIDC 回调须 https;自签证书启动生成/加载)----
    /// 自签证书 pfx 路径(相对 DataDir)。留空则在 DataDir 下自动生成 horus-https.pfx。仅当 Urls 含 https 时生效。
    public string? HttpsCertPath { get; init; }
    /// 自签证书 pfx 密码(留空=无密码)。
    public string? HttpsCertPassword { get; init; }
    /// 自签证书额外 SAN 主机/IP(逗号分隔),如服务器 LAN IP / 主机名(localhost/127.0.0.1 自动含)。
    public string? HttpsSanHosts { get; init; }

    // ---- 视觉分析(L2:视觉 LLM 取代 OCR + L3 Logo,合并单一视觉级)----
    /// 视觉分析器:留空/"off" = 关(默认) | "mock"(确定性·测试联调) | "openai"(OpenAI 兼容端点)。
    public string? VisionProvider { get; init; }
    /// OpenAI 兼容视觉端点基址(DeepSeek-V4 / MiMo-V2.5 / Qwen-VL / GLM-4V 通用)。provider="openai" 时用。
    public string? VisionBaseUrl { get; init; }
    /// 视觉模型名(如 deepseek-v4-pro / MiMo-V2.5)。
    public string? VisionModel { get; init; }
    /// 视觉端点 API key **明文**(仅联调;生产请用 visionApiKeyEnc 加密存储)。env HORUS_VISION_KEY 覆盖。
    public string? VisionApiKey { get; init; }
    /// 视觉端点 API key **DPAPI 密文**(base64·配置文件不存明文)。在部署机上跑 `protect-secret` 生成。见 SecretProtect。
    public string? VisionApiKeyEnc { get; init; }
    /// 视觉判定入可疑队列的置信度阈值(默认 60)。
    public int VisionConfidenceThreshold { get; init; } = 60;
    /// 是否也分析随机基线图(默认 false = 只分析触发型,§5 最小化上传/成本)。
    public bool VisionAnalyzeBaseline { get; init; }
    /// 基线分析抽样率 N:`VisionAnalyzeBaseline=true` 时只分析 **1/N** 的随机基线图(确定性按 imageId 抽样·控云成本)。
    /// 默认 1 = 全分析(保持既有语义);设 >1 抽样(如 10 ≈ 分析 10%),其余基线标 analysis_state=1 终结、补偿重扫不再拾回。
    public int VisionBaselineSampleRate { get; init; } = 1;
    /// 补偿重扫间隔(分钟):周期性拾回 analysis_state=0 的触发型证据图(被队列丢弃 / 服务器重启丢内存队列 / 临时云失败的)。默认 5;≤0=关闭。
    public double VisionBackstopMinutes { get; init; } = 5;
    /// 单张图视觉分析的最大认领次数(含失败):临时云失败由补偿重扫重试,达此上限则放弃(防端点持续失败时死循环重扫)。默认 5。
    public int VisionMaxAttempts { get; init; } = 5;
    /// 视觉分析并发度(单 reader 之外:一次批量拉取 N 张并行送视觉端点,提速单场大批量)。默认 1 = 串行、不压端点;上限 8。
    public int VisionConcurrency { get; init; } = 1;

    // ---- §5 送云前的派生处理(owner 决策 2026-07-02:不再打码/裁剪,只降采样;供应商=境内云 MiMo·PIPL 无跨境)----
    /// 送云图长边像素上限(降采样·省 token/少送无关像素·顺带剥离元数据)。默认 1600;0=不降采样直通。
    public int VisionMaxEdge { get; init; } = 1600;

    // ---- M3 CLIP 按图搜图(provider-agnostic 嵌入器·C# 暴力余弦·无需 sqlite-vec·仅嵌证据/可疑图)----
    /// 图像嵌入器:留空/"off"=关(默认) | "mock"(确定性·测试联调) | "openai"(OpenAI 兼容 /v1/embeddings)。
    public string? EmbedProvider { get; init; }
    /// 嵌入端点基址;**留空则复用 `VisionBaseUrl`**(KEY一致·同 provider)。/embeddings 从此拼。
    public string? EmbedBaseUrl { get; init; }
    /// 嵌入模型名(供应商的图像/多模态 embedding 模型)。provider=openai 必配。
    public string? EmbedModel { get; init; }
    /// 嵌入 API key 明文;**留空则复用视觉 key**(KEY一致)。env HORUS_EMBED_KEY 覆盖。
    public string? EmbedApiKey { get; init; }
    /// 嵌入 API key DPAPI 密文(同视觉机制)。
    public string? EmbedApiKeyEnc { get; init; }
    /// 嵌入维度(暴力余弦无所谓维度·仅记录/校验)。默认 512(CLIP ViT-B/32)。
    public int EmbedDim { get; init; } = 512;
    /// 后台嵌入补扫间隔(分钟):拾回证据/可疑图里尚无 embedding 的。默认 5;≤0=关闭。
    public double EmbedBackstopMinutes { get; init; } = 5;
    /// 按图搜图余弦下限:低于此分的帧视为无关(CLIP 余弦对不相关图常 ≈0),过滤掉避免噪声(B1)。
    /// 默认 0.2(CLIP 经验值);设为 ≤0 则不过滤(退回旧行为)。
    public double EmbedCosineFloor { get; init; } = 0.2;
    /// 本地 ONNX CLIP 图像编码器模型路径。**留空 = 约定名 `model.onnx`(在 DataDir 下)**——与 HF 仓
    /// Qdrant/clip-ViT-B-32-vision 原始文件名一致,下载后免改名;也可显式指定相对(按 DataDir)或绝对路径。
    public string? EmbedOnnxModelPath { get; init; }
    /// ONNX 输入张量名(留空=模型第一个输入·CLIP 常为 pixel_values)。
    public string? EmbedOnnxInput { get; init; }
    /// ONNX 输出张量名(留空=模型第一个输出·常为 image_embeds / output)。
    public string? EmbedOnnxOutput { get; init; }

    [JsonIgnore]
    public bool EmbedEnabled => !string.IsNullOrWhiteSpace(EmbedProvider)
                                && !string.Equals(EmbedProvider, "off", StringComparison.OrdinalIgnoreCase);
    /// 嵌入端点基址(留空复用视觉)。
    [JsonIgnore]
    public string? EmbedBaseUrlEffective => string.IsNullOrWhiteSpace(EmbedBaseUrl) ? VisionBaseUrl : EmbedBaseUrl;
    /// OpenAI 兼容 embeddings 端点(基址去尾斜杠 + /embeddings)。
    [JsonIgnore]
    public string? EmbedEmbeddingsEndpoint => string.IsNullOrEmpty(EmbedBaseUrlEffective) ? null : EmbedBaseUrlEffective!.TrimEnd('/') + "/embeddings";

    /// 事件风险分 ≥ 此值 → 入可疑队列。默认 50(见 architecture §16)。
    public int RiskThreshold { get; init; } = 50;

    // ---- M3 归档 / 清理(architecture §13/§15)----
    /// 是否启用后台归档作业(默认 true)。:memory: 或无到龄考试时自然 no-op;测试可关闭后台、手动触发 RunOnce。
    public bool ArchiveEnabled { get; init; } = true;
    /// 考试**结束**多少天后转 archive 并清理 live。默认 30(§13/§16)。
    public int RetentionDays { get; init; } = 30;
    /// 归档"关键数据"判据:事件有效风险 ≥ 此值,或被 suspicious_queue 引用。默认 50(§16)。
    public int ArchiveCriticalRisk { get; init; } = 50;
    /// 归档库 SQLite 文件路径(相对 DataDir)。默认 horus-archive.db。
    public string ArchiveDbPath { get; init; } = "horus-archive.db";
    /// 后台归档扫描间隔(小时)。默认 6;≤0 = 关闭后台自动扫描(仍可手动 / 测试触发)。
    public double ArchiveScanIntervalHours { get; init; } = 6;

    /// 服务器侧 pHash 近重复判定:同座位相同 phash 视为重复,不另存原图。M1 用精确相等。
    public bool DedupImagesByPhash { get; init; } = true;

    /// 心跳在线判定窗口(秒):最近一次心跳在此窗口内则座位在线。
    public int OnlineWindowSeconds { get; init; } = 90;

    /// 只读连接池大小(SQLite WAL 并发读:看板轮询 / 完整性审计 / 归档 copy 互不串行)。默认 4。:memory: 模式忽略(回退写连接)。
    public int ReadPoolSize { get; init; } = 4;

    /// "最近风险"统计窗口(秒):座位热力取此窗口内事件的最大 risk。
    public int RecentRiskWindowSeconds { get; init; } = 300;

    [JsonIgnore]
    public byte[]? Psk => string.IsNullOrWhiteSpace(PskBase64) ? null : Convert.FromBase64String(PskBase64);

    [JsonIgnore]
    public byte[]? Ksk => string.IsNullOrWhiteSpace(KeystrokeSecretBase64) ? null : Convert.FromBase64String(KeystrokeSecretBase64);

    [JsonIgnore]
    public bool AuthEnabled => Psk is not null;

    [JsonIgnore]
    public bool KeystrokeAuthEnabled => Ksk is not null;

    [JsonIgnore]
    public bool AdminAuthEnabled => !string.IsNullOrEmpty(AdminToken) || DashboardOidcEnabled;

    [JsonIgnore]
    public bool VisionEnabled => !string.IsNullOrWhiteSpace(VisionProvider)
                                 && !string.Equals(VisionProvider, "off", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path)) return new ServerConfig();
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Opt) ?? new ServerConfig();
    }
}
