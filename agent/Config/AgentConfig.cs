using System.Text.Json;
using Honus.Contracts;   // Json.Wire(线协议共享)

namespace Honus.Agent.Config;

/// Agent 配置。从 JSON 加载(camelCase);PSK 为 base64 字符串,自动转 byte[]。
public sealed class AgentConfig
{
    public required string ExamId { get; init; }
    public required string SeatId { get; init; }
    public required string AgentId { get; init; }
    public required string MachineId { get; init; }

    public required string ServerWsBase { get; init; }     // ws://host:port
    public required string ServerHttpBase { get; init; }   // http://host:port
    public required byte[] Psk { get; init; }              // 预共享 HMAC 密钥(base64)

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
