using Horus.Agent.Config;
using Xunit;

namespace Horus.Server.Tests;

/// Agent 零配置(2026-07-03):配置文件可选、内置默认、主机名自动身份。
public class AgentConfigTests
{
    [Fact]
    public void 缺配置文件_全内置默认_oidc模式_自动身份()
    {
        AgentConfig cfg = AgentConfig.Load(Path.Combine(Path.GetTempPath(), "horus-nonexistent-" + Guid.NewGuid().ToString("N") + ".json"));

        Assert.True(cfg.OidcMode);                                   // 默认 oidc
// ★ 默认 issuer 是**主力域**(贝塔通 P116:issuer 恒为它,备用域只是第二条入口)。
        // ⚠★★★ **这一条守得住「默认值被改动了」,守不住「默认值过期了」。**
        //   2026-08-26 实测:上游 P110(08-23)搬了 issuer,而本行与被测的默认值是**同一个
        //   陈旧的字面量**,于是这条断言在那三天里**一直是绿的** —— 自洽的抄件与正确的
        //   抄件长得一模一样。★ **要知道上游现在是什么,只能去打线上 discovery**,
        //   本仓内的判据原理上看不见它(成均那边的对应物是 `pnpm oidc:verify`)。
        Assert.Equal("https://pass.betaoi.cn", cfg.OidcIssuer);
        // 端点默认跟 issuer 走,且挂在**根路径**(贝塔通不是 /oauth/*)。★ 五条路径 2026-08-26 实测核过。
        Assert.Null(cfg.OidcEndpointBase);
        Assert.Equal("https://pass.betaoi.cn/auth", cfg.OidcAuthorizeBase);
        Assert.Equal("horus-client", cfg.OidcClientId);
        Assert.StartsWith("ws://", cfg.ServerWsBase);                // 内置默认服务器地址
        Assert.StartsWith("http://", cfg.ServerHttpBase);
        Assert.False(string.IsNullOrWhiteSpace(cfg.MachineId));      // 主机名自动推导
        Assert.Equal("ag-" + cfg.MachineId, cfg.AgentId);
        Assert.Contains("luogu.com.cn", cfg.WhitelistHosts);         // 内置洛谷白名单兜底
    }

    [Fact]
    public void 身份留空_主机名自动推导()
    {
        var cfg = new AgentConfig { AgentId = "", MachineId = "" };
        cfg.ApplyIdentityDefaults();
        Assert.Equal(Environment.MachineName, cfg.MachineId);
        Assert.Equal("ag-" + Environment.MachineName, cfg.AgentId);
    }

    [Fact]
    public void 身份显式指定_尊重配置值_不覆盖()
    {
        var cfg = new AgentConfig { AgentId = "ag-lab7", MachineId = "LAB-07" };
        cfg.ApplyIdentityDefaults();
        Assert.Equal("LAB-07", cfg.MachineId);
        Assert.Equal("ag-lab7", cfg.AgentId);
    }

    [Fact]
    public void 配置提供白名单_覆盖内置默认_不合并()
    {
        string path = Path.Combine(Path.GetTempPath(), "horus-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "whitelistHosts": ["only.this.lan"] }""");
            AgentConfig cfg = AgentConfig.Load(path);
            Assert.Equal(new[] { "only.this.lan" }, cfg.WhitelistHosts);   // 替换而非追加(不含 luogu)
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 配置覆盖服务器地址与鉴权模式()
    {
        string path = Path.Combine(Path.GetTempPath(), "horus-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "serverWsBase": "ws://10.0.0.5:9000", "authMode": "psk", "examId": "E1", "seatId": "s" }""");
            AgentConfig cfg = AgentConfig.Load(path);
            Assert.Equal("ws://10.0.0.5:9000", cfg.ServerWsBase);
            Assert.False(cfg.OidcMode);
            Assert.Equal("E1", cfg.ExamId);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void 填了端点前缀_端点整套换入口_而issuer不变()
    {
        // ★★ 贝塔通 P72:`.cc` 是**同一个 issuer 的第二条入口**,不是第二个 issuer。
        //   主域不可达时只改端点前缀 —— issuer 一个字都不动,否则所有令牌的 `iss` 校验会当场失败。
        //   这条正面锁住它,免得日后有人「顺手」把 issuer 改成 .cc 来走备用域。
        var cfg = new AgentConfig { OidcEndpointBase = "https://pass.betaoi.cc" };
        Assert.Equal("https://pass.betaoi.cn", cfg.OidcIssuer);          // 不变
        Assert.Equal("https://pass.betaoi.cc/auth", cfg.OidcAuthorizeBase);   // 整套换入口
        Assert.NotEqual(cfg.OidcIssuer, cfg.OidcEndpointBase);          // ★ 要害是两者不同主机
    }
}
