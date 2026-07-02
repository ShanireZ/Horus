using System.Text.Json;
using Horus.Contracts;   // Json.Wire(线协议共享)

namespace Horus.Agent.Config;

/// Agent 配置。从 JSON 加载(camelCase);PSK 为 base64 字符串,自动转 byte[]。
public sealed class AgentConfig
{
    public required string ExamId { get; init; }
    public required string SeatId { get; init; }
    public required string AgentId { get; init; }
    public required string MachineId { get; init; }

    public required string ServerWsBase { get; init; }     // ws://host:port
    public required string ServerHttpBase { get; init; }   // http://host:port
    public byte[]? Psk { get; init; }                     // 预共享 HMAC 密钥(base64)。OIDC 模式可省。

    // ---- M4 身份层:OIDC 登录(取代共享 PSK)----
    /// 采集鉴权:"psk"(默认) | "oidc"(经 cpplearn 登录换会话)。both 由服务器侧决定共存;Agent 只需二选一。
    public string AuthMode { get; init; } = "psk";
    /// cpplearn OIDC issuer(如 https://betaoi.cc)。oidc 模式必配。
    public string? OidcIssuer { get; init; }
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

    // 白名单
    public List<string> WhitelistHosts { get; init; } = new();   // 判题站域名,放行
    public List<string> WhitelistProcs { get; init; } = new();   // 允许进程名(无 .exe)

    public static AgentConfig Load(string path)
        => JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json.Wire)
           ?? throw new InvalidOperationException("配置解析失败: " + path);
}
