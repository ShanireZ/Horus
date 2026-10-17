using System;
using System.IO;
using Horus.Agent.Config;
using Xunit;

namespace Horus.Server.Tests;

/// ★★★ **裝機時被複製的那兩份設定範例 × 實況。**
///
/// ## 起因:2026-08-26 發現 `agent.config.sample.json` 落後**兩代**
///
/// 它寫著 `"oidcIssuer": "https://betaoi.cc"` —— 那是**問天錄時期**的域。
/// 貝塔通接手後 issuer 是 `betaoi.cn`(範例沒跟),**P110**(08-23)又搬到 `pass.` 子域(範例還是沒跟)。
///
/// ★★★ **危險不在於它舊,在於它是「抄給人用的那一份」**:
/// 範例裡的 `oidcIssuer` 會**覆蓋掉程式碼里正確的預設值**,
/// ★★ 於是**零設定部署本來是對的,照範例配反而是錯的**。
/// ★ 成均那邊撞過同一個形狀(`.env.example` 少一個鍵,而「那一份才是裝機時被複製的那個」)。
///
/// ## ★★ 這道門守得住什麼、守不住什麼
///
/// ★ **守得住**:範例裡再出現一個寫死的貝塔通 issuer(不論新舊)當場紅;
///   範例本身語法壞掉(它是 JSONC,帶註解)當場紅。
/// ★★★ **守不住「內建預設值本身過期了」** —— 那需要打線上 discovery,
///   而 CI 不一定通公用網路。★ 這堵牆與成均那邊是同一堵(那邊的對應物是 `pnpm oidc:verify`)。
///   ⇒ **整合測試 / 裝機前必須拿線上 discovery 核一次內建預設值。**寫在這裡而不是假裝門更強。
public class ConfigSampleTests
{
    /// ★ 拼出來而不是寫出來:本檔案要掃的就是這個字樣,寫成字面值會讓判據**找到它自己**。
    ///   (成均那邊同一道門踩過三次自指,這裡直接照抄它的規避。)
    private const string Scheme = "https://";

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Horus.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到包含 Horus.sln 的仓库根目录");
    }

    /// 範例的**正文**:只留非註解行。
    ///
    /// ★★★ 與成均 `.env.example` 那道門同一條口徑:**註解裡的舊值是歷史證據,不是設定**。
    /// ★ 這兩份範例裡**有意**留著「本行此前寫著某個陳舊的值」的訂正記錄 ——
    ///   把它們當設定讀會讓判據**假紅**,而★★ **一道會誤報的門兩週之後沒人看**。
    /// ★★ 代價寫在這裡:註解裡若又出現一個可供照抄的網域,本門看不見它。
    ///   ⇒ 因此那兩處註解都改成了「有意不寫現值」,而不是換一個新值。
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

        // ★ 走真正的 Load(而不是隻解析 JSON):它帶 ReadCommentHandling.Skip,
        //   ★★ 因此這條同時鎖住「範例是合法 JSONC」——範例語法壞掉的表現是裝機時一片空白。
        AgentConfig fromSample = AgentConfig.Load(path);
        AgentConfig builtin = AgentConfig.Load(Path.Combine(Path.GetTempPath(), "horus-nonexistent-" + Guid.NewGuid().ToString("N") + ".json"));

        // ★★★ 判據是**關係**:照範例配出來的 issuer,必須與零設定時**完全一樣**。
        //   ★ 斷言關係而不是斷言那個值 —— 值會跟著上游漂,關係不會。
        Assert.Equal(builtin.OidcIssuer, fromSample.OidcIssuer);

        // ★★ 而它必須是個能用的絕對 URL —— 「留空」與「不寫」不是一回事:
        //   JSON 裡寫 "oidcIssuer": "" 會把內建預設**覆蓋成空串**(反序列化只認鍵在不在),
        //   ★ 表現是開考時 OidcLoginFlow 拋「oidc 模式需配 oidcIssuer」。
        Assert.False(string.IsNullOrWhiteSpace(fromSample.OidcIssuer), "样例把内置 issuer 覆盖成空了");
        Assert.True(Uri.TryCreate(fromSample.OidcIssuer, UriKind.Absolute, out _), "issuer 必须是绝对 URL");
        Assert.StartsWith(Scheme, fromSample.OidcAuthorizeBase);   // ★ 空 issuer 會讓它退化成相對路徑 "/auth"
    }

    [Fact]
    public void 两份样例里都不许写死贝塔通的issuer()
    {
        // ★★★ 契約(`../BetaPass/docs/rp-contract.md`「雙域(P116)」)原話:
        //   「★★★ 別把它抄進任何程式碼或寫死進設定的字面值裡」。
        // ★ 判據釘的是「**這個域的裸根不許出現**」,不是「當前值是什麼」——
        //   ★★ 前者不會漂(舊值只會更舊),後者會跟著上游一起腐爛。
        foreach (string rel in new[] { "agent/agent.config.sample.json", "server/server.config.sample.json" })
        {
            string text = CodeLinesOf(rel);
            // ★ 只攔**裸根域**:`.betaoi.cn` 的子域(Cloudflare Analytics 那行說的是本站主機名)是合法的。
            Assert.DoesNotContain(Scheme + "betaoi.cn", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Scheme + "betaoi.cc", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Scheme + "pass.betaoi.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 自证_判据不是恒空的()
    {
        // ★★★ 門的註解寫「本門守 X」不等於它判得動 X。下面拿**已知答案的樣本**驗掃查本身 ——
        //   ★★ 不驗的話,一個恆空的判據與一個「確實沒問題」的判據**報告完全一樣**。
        Assert.Contains("authMode", File.ReadAllText(Path.Combine(FindRoot(), "agent", "agent.config.sample.json")), StringComparison.Ordinal);

        // ★ 裸根域必須命中,而子域必須落空 —— 一刀切會把 Analytics 那行誤傷,
        //   ★★ 而**一道會誤報的門兩週之後沒人看**。
        Assert.Contains(Scheme + "betaoi.cn", Scheme + "betaoi.cn/auth", StringComparison.Ordinal);
        Assert.DoesNotContain(Scheme + "betaoi.cn", Scheme + "hr.betaoi.cn/auth", StringComparison.Ordinal);

        // ★★ 本檔案自己不含那個字面值 —— 這是「不需要豁免名單」的前提(成均那邊踩過三次自指)。
        string self = File.ReadAllText(Path.Combine(FindRoot(), "tests", "ConfigSampleTests.cs"));
        Assert.DoesNotContain(Scheme + "betaoi.cn", self, StringComparison.Ordinal);

        // ★★★ 剝註解這一步真的在起作用 —— 否則那兩份範例裡**有意留著**的訂正記錄會讓判據恆紅。
        Assert.DoesNotContain("此前寫著", CodeLinesOf("agent/agent.config.sample.json"), StringComparison.Ordinal);
        Assert.Contains("此前寫著", File.ReadAllText(Path.Combine(FindRoot(), "agent", "agent.config.sample.json")), StringComparison.Ordinal);
    }
}
