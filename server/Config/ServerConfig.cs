using System.Text.Json;
using System.Text.Json.Serialization;

namespace Horus.Server.Config;

/// 服务器配置(从 server.config.json 加载,camelCase)。record 便于用 with 施加环境变量覆盖。
public sealed record ServerConfig
{
    /// Kestrel 绑定地址(可多个,逗号分隔),如 "http://0.0.0.0:5199"。
    public string Urls { get; init; } = "http://0.0.0.0:5199";

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

    // ---- M4 身份层:cpplearn OIDC 取代共享 PSK(见 docs/m4-identity-oidc.md)----
    /// 采集面鉴权模式:"psk"(默认·共享 PSK·M1-M3 原样) | "oidc"(仅 OIDC 会话) | "both"(共存·迁移期回退网)。
    public string AuthMode { get; init; } = "psk";
    /// cpplearn OIDC issuer(如 https://betaoi.cc)。OIDC 模式必配。
    public string? OidcIssuer { get; init; }
    /// Horus 在 cpplearn 注册的 client_id(默认 horus-client)。
    public string? OidcClientId { get; init; }
    /// client_secret 明文(仅联调;生产用 OidcClientSecretEnc 或 env HORUS_OIDC_SECRET)。Server-Broker:secret 只在服务器。
    public string? OidcClientSecret { get; init; }
    /// client_secret DPAPI 密文(与视觉 key 同机制,见 SecretProtect)。
    public string? OidcClientSecretEnc { get; init; }
    /// cpplearn 的 JWKS(RSA 公钥)内联 JSON:局域网离线验 id_token 用,免运行时拉取。留空则启动时从 issuer 拉取 + 缓存。
    public string? OidcJwksJson { get; init; }
    /// OIDC 会话有效期(分钟):派发的采集凭证寿命,建议 ≥ 考试时长。默认 180。
    public int OidcSessionMinutes { get; init; } = 180;

    [JsonIgnore]
    public bool OidcEnabled => AuthMode is "oidc" or "both";
    [JsonIgnore]
    public bool PskAcceptedForIngest => AuthMode is "psk" or "both";
    /// OIDC token 端点(从 issuer 拼,去尾斜杠 + /oauth/token)。
    [JsonIgnore]
    public string? OidcTokenEndpoint => string.IsNullOrEmpty(OidcIssuer) ? null : OidcIssuer!.TrimEnd('/') + "/oauth/token";
    /// OIDC 授权端点(建监考员登录 URL 用)。
    [JsonIgnore]
    public string? OidcAuthorizeEndpoint => string.IsNullOrEmpty(OidcIssuer) ? null : OidcIssuer!.TrimEnd('/') + "/oauth/authorize";

    // ---- M4·RBAC:监考员看板 OIDC 登录(cpplearn dashboard web client·取代静态 adminToken·见 m4-identity-oidc.md §10)----
    /// 管理端鉴权模式:"token"(默认·静态 adminToken·M1-M3 原样) | "oidc"(仅 cpplearn 长老 OIDC 会话·R3 无令牌后门)。
    public string AdminAuthMode { get; init; } = "token";
    /// cpplearn dashboard web client_id(如 horus-dashboard)。AdminAuthMode=oidc 必配。
    public string? OidcDashboardClientId { get; init; }
    /// dashboard client_secret 明文(仅联调;生产用 Enc 或 env HORUS_OIDC_DASHBOARD_SECRET)。Server 持有,浏览器从不经手。
    public string? OidcDashboardClientSecret { get; init; }
    /// dashboard client_secret DPAPI 密文(与视觉/采集 secret 同机制)。
    public string? OidcDashboardClientSecretEnc { get; init; }
    /// dashboard 回调 URI(须与 cpplearn 注册的 OAUTH_HORUS_DASHBOARD_REDIRECT_URIS 一条精确一致,如 https://<服务器>/cb)。
    public string? OidcDashboardRedirectUri { get; init; }
    /// 管理会话有效期(分钟):监考员登录后凭证寿命,建议 ≥ 考试时长。默认 180。
    public int AdminSessionMinutes { get; init; } = 180;

    [JsonIgnore]
    public bool DashboardOidcEnabled => string.Equals(AdminAuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

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
    /// 补偿重扫间隔(分钟):周期性拾回 analysis_state=0 的触发型证据图(被队列丢弃 / 服务器重启丢内存队列 / 临时云失败的)。默认 5;≤0=关闭。
    public double VisionBackstopMinutes { get; init; } = 5;
    /// 单张图视觉分析的最大认领次数(含失败):临时云失败由补偿重扫重试,达此上限则放弃(防端点持续失败时死循环重扫)。默认 5。
    public int VisionMaxAttempts { get; init; } = 5;

    // ---- §5 送云前的派生处理(owner 决策 2026-07-02:不再打码/裁剪,只降采样;供应商=境内云 MiMo·PIPL 无跨境)----
    /// 送云图长边像素上限(降采样·省 token/少送无关像素·顺带剥离元数据)。默认 1600;0=不降采样直通。
    public int VisionMaxEdge { get; init; } = 1600;

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
