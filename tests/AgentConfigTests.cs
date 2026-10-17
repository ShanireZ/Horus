using Horus.Agent.Config;
using Xunit;

namespace Horus.Server.Tests;

/// Agent 零設定(2026-07-03):設定檔案可選、內建預設、主機名自動身分。
public class AgentConfigTests
{
    [Fact]
    public void 缺配置文件_全内置默认_oidc模式_自动身份()
    {
        AgentConfig cfg = AgentConfig.Load(Path.Combine(Path.GetTempPath(), "horus-nonexistent-" + Guid.NewGuid().ToString("N") + ".json"));

        Assert.True(cfg.OidcMode);                                   // 預設 oidc
// ★ 預設 issuer 是**主力域**(貝塔通 P116:issuer 恆為它,備用域只是第二條入口)。
        // ⚠★★★ **這一條守得住「預設值被改動了」,守不住「預設值過期了」。**
        //   2026-08-26 實測:上游 P110(08-23)搬了 issuer,而本行與被測的預設值是**同一個
        //   陳舊的字面值**,於是這條斷言在那三天裡**一直是綠的** —— 自洽的抄件與正確的
        //   抄件長得一模一樣。★ **要知道上游現在是什麼,只能去打線上 discovery**,
        //   本儲存庫內的判據原理上看不見它(成均那邊的對應物是 `pnpm oidc:verify`)。
        Assert.Equal("https://pass.betaoi.cc", cfg.OidcIssuer);
        // 端點預設跟 issuer 走,且掛在**根路徑**(貝塔通不是 /oauth/*)。★ 五條路徑 2026-08-26 實測核過。
        Assert.Null(cfg.OidcEndpointBase);
        Assert.Equal("https://pass.betaoi.cc/auth", cfg.OidcAuthorizeBase);
        Assert.Equal("horus-client", cfg.OidcClientId);
        Assert.StartsWith("ws://", cfg.ServerWsBase);                // 內建預設伺服器位址
        Assert.StartsWith("http://", cfg.ServerHttpBase);
        Assert.False(string.IsNullOrWhiteSpace(cfg.MachineId));      // 主機名自動推導
        Assert.Equal("ag-" + cfg.MachineId, cfg.AgentId);
        Assert.Contains("luogu.com.cn", cfg.WhitelistHosts);         // 內建洛谷白名單備援
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
            Assert.Equal(new[] { "only.this.lan" }, cfg.WhitelistHosts);   // 替換而非追加(不含 luogu)
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
        // 端點覆蓋與 issuer 獨立，使用虛構入口證明關係；不啟用休眠 cn。
        var cfg = new AgentConfig { OidcEndpointBase = "https://idp-backup.test" };
        Assert.Equal("https://pass.betaoi.cc", cfg.OidcIssuer);
        Assert.Equal("https://idp-backup.test/auth", cfg.OidcAuthorizeBase);
        Assert.NotEqual(cfg.OidcIssuer, cfg.OidcEndpointBase);
    }
}
