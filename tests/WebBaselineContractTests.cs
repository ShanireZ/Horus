using System.Text.Json;
using Xunit;

namespace Horus.Server.Tests;

public class WebBaselineContractTests
{
    private static readonly string[] WatchedNewlyMarkers =
    [
        "document.startViewTransition",
        "scheduler.yield",
        "showPopover(",
        "Temporal.",
        "CSS.highlights",
        "navigation.addEventListener"
    ];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Horus.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到包含 Horus.sln 的仓库根目录");
    }

    private static JsonDocument ReadPolicy()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "baseline.config.json")));

    [Fact]
    public void 看板声明为受控原生Web_不虚构构建目标()
    {
        using JsonDocument doc = ReadPolicy();
        JsonElement root = doc.RootElement;

        Assert.Equal("controlled-web", root.GetProperty("runtime").GetString());
        Assert.Equal("newly", root.GetProperty("featureTarget").GetString());
        Assert.Equal("not-applicable", root.GetProperty("buildTarget").GetProperty("strategy").GetString());
        Assert.False(root.GetProperty("downstream").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void 受控浏览器合同锁定Chrome与Edge当前和前一主版本()
    {
        using JsonDocument doc = ReadPolicy();
        JsonElement contract = doc.RootElement.GetProperty("browserContract");
        string[] engines = contract.GetProperty("engines").EnumerateArray()
            .Select(item => item.GetString() ?? "")
            .ToArray();

        Assert.Equal(["chrome", "edge"], engines);
        Assert.Equal("current-and-previous-major", contract.GetProperty("releaseWindow").GetString());
        Assert.False(string.IsNullOrWhiteSpace(contract.GetProperty("preflight").GetString()));
    }

    [Fact]
    public void 原生资源存在_受监视的Newly能力必须登记检测与降级()
    {
        string root = FindRoot();
        string[] assets =
        [
            "server/wwwroot/index.html",
            "server/wwwroot/styles.css",
            "server/wwwroot/app.js"
        ];
        string allSource = string.Join("\n", assets.Select(path =>
        {
            string fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"缺少看板资源 {path}");
            return File.ReadAllText(fullPath);
        }));

        using JsonDocument doc = ReadPolicy();
        JsonElement[] declarations = doc.RootElement.GetProperty("newlyFeatures").EnumerateArray().ToArray();
        string[] declaredMarkers = declarations
            .Select(item => item.GetProperty("marker").GetString() ?? "")
            .ToArray();

        foreach (JsonElement declaration in declarations)
        {
            Assert.False(string.IsNullOrWhiteSpace(declaration.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(declaration.GetProperty("detection").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(declaration.GetProperty("fallback").GetString()));
            string marker = declaration.GetProperty("marker").GetString() ?? "";
            Assert.False(string.IsNullOrWhiteSpace(marker));
            Assert.Contains(marker, allSource);
        }

        foreach (string marker in WatchedNewlyMarkers)
        {
            if (allSource.Contains(marker, StringComparison.Ordinal)) Assert.Contains(marker, declaredMarkers);
        }
    }
}
