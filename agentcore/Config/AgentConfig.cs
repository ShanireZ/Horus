using System.Text.Json;
using Horus.Contracts;   // Json.Wire(线协议共享)

namespace Horus.Agent.Config;

/// Agent 配置。从 JSON 加载(camelCase);PSK 为 base64 字符串,自动转 byte[]。
/// **零配置部署(2026-07-03)**:所有字段都有内置默认,配置文件**整个可选** —— 缺文件即全默认。
///   身份 agentId/machineId 留空 → 由**主机名自动推导**(machineId=主机名·agentId="ag-"+主机名),逐台免配。
///   examId/seatId/psk 在 oidc 模式无需配置(examId 服务端派发·seatId 由 OIDC 身份派生·psk 不用)。
///   唯一常需按现场核对的是 serverWsBase/serverHttpBase(内置默认见下,IP 不符才覆盖)。
public sealed class AgentConfig
{
    /// 考试与座位:**OIDC 模式不在配置里出现** —— examId 由服务端派发(当前活跃考试)、seatId 由 OIDC 身份派生
    /// (username),登录成功后由 Program 填入;psk(legacy)模式仍须在配置中提供。
    public string ExamId { get; set; } = "";
    public string SeatId { get; set; } = "";
    /// 本机身份:留空则 Load 后由主机名自动推导(agentId="ag-"+主机名·machineId=主机名),逐台免配。
    public string AgentId { get; set; } = "";
    public string MachineId { get; set; } = "";

    // 服务器地址:内置默认 = 考场服务器(IP 不符才在配置覆盖)。唯一常需现场核对的字段。
    public string ServerWsBase { get; init; } = "ws://192.168.32.145:8080";     // ws://host:port
    public string ServerHttpBase { get; init; } = "http://192.168.32.145:8080"; // http://host:port
    public byte[]? Psk { get; init; }                     // 预共享 HMAC 密钥(base64)。OIDC 模式可省。

    // ---- M4 身份层:OIDC 登录(取代共享 PSK)----
    /// 采集鉴权:"oidc"(默认·经 cpplearn 登录换会话) | "psk"(legacy·须配 psk+examId+seatId)。both 由服务器侧决定。
    public string AuthMode { get; init; } = "oidc";
    /// cpplearn OIDC issuer。内置默认 = 生产站(自建部署才需覆盖)。
    public string? OidcIssuer { get; init; } = "https://betaoi.cc";
    /// Horus 在 cpplearn 注册的 client_id(默认 horus-client)。
    public string OidcClientId { get; init; } = "horus-client";
    /// 请求的 scope(默认 openid + horus_profile 富画像)。
    public string OidcScope { get; init; } = "openid horus_profile";

    public bool OidcMode => string.Equals(AuthMode, "oidc", StringComparison.OrdinalIgnoreCase);

    // 截图
    public int TargetHeight { get; init; } = 1080;
    public int WebpQuality { get; init; } = 75;
    public int BaselineMinSeconds { get; init; } = 30;
    public int BaselineMaxSeconds { get; init; } = 90;

    // 阈值
    public int LargePasteThreshold { get; init; } = 200;

    // 白名单:内置默认 = 洛谷判题站 + 常见 IDE/编译器(兜底);服务器下发的白名单会在运行时覆盖(以下发为准)。
    public List<string> WhitelistHosts { get; init; } = new() { "luogu.com.cn", "www.luogu.com.cn" };
    public List<string> WhitelistProcs { get; init; } = new()
    {
        "code", "devenv", "cl", "g++", "gcc", "edge", "explorer", "horus.agent", "dev-c++", "redpanda",
    };

    // 配置加载专用选项:camelCase + **允许 // 注释与尾逗号**(便于部署模板自文档化)。
    // 不复用 Json.Wire(那是线协议/canonical 序列化器,须逐字节稳定,不能加宽松解析)。
    private static readonly JsonSerializerOptions ConfigOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// 从路径加载;**文件不存在则用全部内置默认**(零配置部署)。加载后补齐主机名自动身份。
    public static AgentConfig Load(string path)
    {
        AgentConfig cfg = File.Exists(path)
            ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), ConfigOpt)
              ?? throw new InvalidOperationException("配置解析失败: " + path)
            : new AgentConfig();   // 无配置文件 → 全内置默认
        cfg.ApplyIdentityDefaults();
        return cfg;
    }

    /// 身份缺省补齐:agentId/machineId 留空 → 由主机名自动推导(machineId=主机名·agentId="ag-"+主机名)。
    /// 已在配置显式指定则尊重原值。主机名跨重启稳定 → seq 空间/哈希链不受影响。
    public void ApplyIdentityDefaults()
    {
        string host = SafeHostName();
        if (string.IsNullOrWhiteSpace(MachineId)) MachineId = host;
        if (string.IsNullOrWhiteSpace(AgentId)) AgentId = "ag-" + host;
    }

    private static string SafeHostName()
    {
        try { string h = Environment.MachineName; return string.IsNullOrWhiteSpace(h) ? "unknown-host" : h; }
        catch { return "unknown-host"; }
    }
}
