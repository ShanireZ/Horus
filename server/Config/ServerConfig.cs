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

    /// 事件风险分 ≥ 此值 → 入可疑队列。默认 50(见 architecture §16)。
    public int RiskThreshold { get; init; } = 50;

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
    public bool AdminAuthEnabled => !string.IsNullOrEmpty(AdminToken);

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
