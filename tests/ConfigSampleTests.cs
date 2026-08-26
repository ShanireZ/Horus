using System;
using System.IO;
using Horus.Agent.Config;
using Xunit;

namespace Horus.Server.Tests;

/// ★★★ **装机时被复制的那两份配置样例 × 实况。**
///
/// ## 起因:2026-08-26 发现 `agent.config.sample.json` 落后**两代**
///
/// 它写着 `"oidcIssuer": "https://betaoi.cc"` —— 那是**问天录时期**的域。
/// 贝塔通接手后 issuer 是 `betaoi.cn`(样例没跟),**P110**(08-23)又搬到 `pass.` 子域(样例还是没跟)。
///
/// ★★★ **危险不在于它旧,在于它是「抄给人用的那一份」**:
/// 样例里的 `oidcIssuer` 会**覆盖掉代码里正确的默认值**,
/// ★★ 于是**零配置部署本来是对的,照样例配反而是错的**。
/// ★ 成均那边撞过同一个形状(`.env.example` 少一个键,而「那一份才是装机时被复制的那个」)。
///
/// ## ★★ 这道门守得住什么、守不住什么
///
/// ★ **守得住**:样例里再出现一个写死的贝塔通 issuer(不论新旧)当场红;
///   样例本身语法坏掉(它是 JSONC,带注释)当场红。
/// ★★★ **守不住「内置默认值本身过期了」** —— 那需要打线上 discovery,
///   而 CI 不一定通公网。★ 这堵墙与成均那边是同一堵(那边的对应物是 `pnpm oidc:verify`)。
///   ⇒ **联调 / 装机前必须拿线上 discovery 核一次内置默认值。**写在这里而不是假装门更强。
public class ConfigSampleTests
{
    /// ★ 拼出来而不是写出来:本文件要扫的就是这个字样,写成字面量会让判据**找到它自己**。
    ///   (成均那边同一道门踩过三次自指,这里直接照抄它的规避。)
    private const string Scheme = "https://";

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Horus.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到包含 Horus.sln 的仓库根目录");
    }

    /// 样例的**正文**:只留非注释行。
    ///
    /// ★★★ 与成均 `.env.example` 那道门同一条口径:**注释里的旧值是历史证据,不是配置**。
    /// ★ 这两份样例里**有意**留着「本行此前写着某个陈旧的值」的订正记录 ——
    ///   把它们当配置读会让判据**假红**,而★★ **一道会误报的门两周之后没人看**。
    /// ★★ 代价写在这里:注释里若又出现一个可供照抄的域名,本门看不见它。
    ///   ⇒ 因此那两处注释都改成了「有意不写现值」,而不是换一个新值。
    private static string CodeLinesOf(string relativePath)
    {
        string full = Path.Combine(FindRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        var kept = new System.Text.StringBuilder();
        foreach (string raw in File.ReadAllLines(full))
        {
            if (!raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.AppendLine(raw);
        }
        return kept.ToString();
    }

    [Fact]
    public void Agent样例能被真正加载_且不覆盖内置issuer()
    {
        string path = Path.Combine(FindRoot(), "agent", "agent.config.sample.json");
        Assert.True(File.Exists(path), "Agent 配置样例不在了 —— 装机文档指着它");

        // ★ 走真正的 Load(而不是只解析 JSON):它带 ReadCommentHandling.Skip,
        //   ★★ 因此这条同时锁住「样例是合法 JSONC」——样例语法坏掉的表现是装机时一片空白。
        AgentConfig fromSample = AgentConfig.Load(path);
        AgentConfig builtin = AgentConfig.Load(Path.Combine(Path.GetTempPath(), "horus-nonexistent-" + Guid.NewGuid().ToString("N") + ".json"));

        // ★★★ 判据是**关系**:照样例配出来的 issuer,必须与零配置时**完全一样**。
        //   ★ 断言关系而不是断言那个值 —— 值会跟着上游漂,关系不会。
        Assert.Equal(builtin.OidcIssuer, fromSample.OidcIssuer);

        // ★★ 而它必须是个能用的绝对 URL —— 「留空」与「不写」不是一回事:
        //   JSON 里写 "oidcIssuer": "" 会把内置默认**覆盖成空串**(反序列化只认键在不在),
        //   ★ 表现是开考时 OidcLoginFlow 抛「oidc 模式需配 oidcIssuer」。
        Assert.False(string.IsNullOrWhiteSpace(fromSample.OidcIssuer), "样例把内置 issuer 覆盖成空了");
        Assert.True(Uri.TryCreate(fromSample.OidcIssuer, UriKind.Absolute, out _), "issuer 必须是绝对 URL");
        Assert.StartsWith(Scheme, fromSample.OidcAuthorizeBase);   // ★ 空 issuer 会让它退化成相对路径 "/auth"
    }

    [Fact]
    public void 两份样例里都不许写死贝塔通的issuer()
    {
        // ★★★ 契约(`../BetaPass/docs/rp-contract.md`「双域(P116)」)原话:
        //   「★★★ 别把它抄进任何代码或写死进配置的字面量里」。
        // ★ 判据钉的是「**这个域的裸根不许出现**」,不是「当前值是什么」——
        //   ★★ 前者不会漂(旧值只会更旧),后者会跟着上游一起腐烂。
        foreach (string rel in new[] { "agent/agent.config.sample.json", "server/server.config.sample.json" })
        {
            string text = CodeLinesOf(rel);
            // ★ 只拦**裸根域**:`.betaoi.cn` 的子域(Cloudflare Analytics 那行说的是本站主机名)是合法的。
            Assert.DoesNotContain(Scheme + "betaoi.cn", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Scheme + "betaoi.cc", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Scheme + "pass.betaoi.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 自证_判据不是恒空的()
    {
        // ★★★ 门的注释写「本门守 X」不等于它判得动 X。下面拿**已知答案的样本**验扫查本身 ——
        //   ★★ 不验的话,一个恒空的判据与一个「确实没问题」的判据**报告完全一样**。
        Assert.Contains("authMode", File.ReadAllText(Path.Combine(FindRoot(), "agent", "agent.config.sample.json")), StringComparison.Ordinal);

        // ★ 裸根域必须命中,而子域必须落空 —— 一刀切会把 Analytics 那行误伤,
        //   ★★ 而**一道会误报的门两周之后没人看**。
        Assert.Contains(Scheme + "betaoi.cn", Scheme + "betaoi.cn/auth", StringComparison.Ordinal);
        Assert.DoesNotContain(Scheme + "betaoi.cn", Scheme + "hr.betaoi.cn/auth", StringComparison.Ordinal);

        // ★★ 本文件自己不含那个字面量 —— 这是「不需要豁免名单」的前提(成均那边踩过三次自指)。
        string self = File.ReadAllText(Path.Combine(FindRoot(), "tests", "ConfigSampleTests.cs"));
        Assert.DoesNotContain(Scheme + "betaoi.cn", self, StringComparison.Ordinal);

        // ★★★ 剥注释这一步真的在起作用 —— 否则那两份样例里**有意留着**的订正记录会让判据恒红。
        Assert.DoesNotContain("此前写着", CodeLinesOf("agent/agent.config.sample.json"), StringComparison.Ordinal);
        Assert.Contains("此前写着", File.ReadAllText(Path.Combine(FindRoot(), "agent", "agent.config.sample.json")), StringComparison.Ordinal);
    }
}
