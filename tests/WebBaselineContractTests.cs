using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static string ReadDotnetSdkVersion()
    {
        var startInfo = new ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 dotnet --version");
        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet --version 失败：{error}");
        return output;
    }

    private static string WithoutComments(string source)
    {
        source = Regex.Replace(source, @"<!--[\s\S]*?-->", "");
        var output = new StringBuilder(source.Length);
        char? quote = null;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (quote is not null)
            {
                output.Append(current);
                if (current == '\\' && index + 1 < source.Length)
                {
                    output.Append(next);
                    index++;
                }
                else if (current == quote) quote = null;
                continue;
            }
            if (current is '"' or '\'' or '`')
            {
                quote = current;
                output.Append(current);
                continue;
            }
            if (current == '/' && next == '/')
            {
                int lineEnd = source.IndexOf('\n', index + 2);
                if (lineEnd < 0) break;
                output.Append('\n');
                index = lineEnd;
                continue;
            }
            if (current == '/' && next == '*')
            {
                int commentEnd = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0) break;
                index = commentEnd + 1;
                continue;
            }
            output.Append(current);
        }
        return output.ToString();
    }

    [Fact]
    public void Newly标记只写在注释中时不算活动实现()
    {
        const string source = "/* document.startViewTransition */\n// scheduler.yield\n<!-- showPopover( -->\nconst url = \"https://example.test\";";
        string activeSource = WithoutComments(source);
        Assert.DoesNotContain("document.startViewTransition", activeSource);
        Assert.DoesNotContain("scheduler.yield", activeSource);
        Assert.DoesNotContain("showPopover(", activeSource);
        Assert.Contains("https://example.test", activeSource);
    }

    [Fact]
    public void 看板声明为受控原生Web_不虚构构建目标()
    {
        using JsonDocument doc = ReadPolicy();
        JsonElement root = doc.RootElement;

        Assert.Equal("controlled-web", root.GetProperty("runtime").GetString());
        Assert.Equal("newly", root.GetProperty("featureTarget").GetString());
        Assert.Equal("not-applicable", root.GetProperty("buildTarget").GetProperty("strategy").GetString());
        Assert.False(root.GetProperty("downstream").GetProperty("enabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("criticalFallback").GetString()));
        Assert.NotEmpty(root.GetProperty("verification").EnumerateArray());
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

        JsonElement approvedMajors = contract.GetProperty("approvedMajors");
        JsonElement snapshot = doc.RootElement.GetProperty("snapshot");
        string approvedAtRaw = snapshot.GetProperty("approvedAt").GetString() ?? "";
        Assert.True(
            DateOnly.TryParseExact(approvedAtRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly approvedAt),
            "Baseline 快照日期必须是 yyyy-MM-dd");
        int ageDays = DateOnly.FromDateTime(DateTime.Now).DayNumber - approvedAt.DayNumber;
        Assert.InRange(ageDays, 0, 92);
        JsonElement reviewVersions = snapshot.GetProperty("reviewMachineVersions");
        foreach (string engine in engines)
        {
            int[] majors = approvedMajors.GetProperty(engine).EnumerateArray()
                .Select(item => item.GetInt32())
                .ToArray();
            Assert.Equal(2, majors.Length);
            Assert.Equal(majors[0] + 1, majors[1]);

            string observed = reviewVersions.GetProperty(engine).GetString() ?? "";
            Assert.True(Version.TryParse(observed, out Version? version), $"{engine} 快照版本无效");
            Assert.Equal(majors[1], version.Major);
        }
        string approvedSdk = snapshot.GetProperty("dotnetSdk").GetString() ?? "";
        Assert.Matches(@"^8\.0\.\d+$", approvedSdk);
        Assert.Equal(approvedSdk, ReadDotnetSdkVersion());
    }

    [Fact]
    public void 原生资源存在_受监视的Newly能力必须登记检测与降级()
    {
        string root = FindRoot();
        string webRoot = Path.Combine(root, "server", "wwwroot");
        Assert.True(Directory.Exists(webRoot), "缺少看板原生资源目录 server/wwwroot");
        string[] browserExtensions = [".html", ".css", ".js"];
        string[] assets = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Where(path => browserExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(assets);
        string activeSource = string.Join("\n", assets.Select(path =>
        {
            Assert.True(File.Exists(path), $"缺少看板资源 {path}");
            return WithoutComments(File.ReadAllText(path));
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
            string detectionMarker = declaration.GetProperty("detectionMarker").GetString() ?? "";
            string fallbackMarker = declaration.GetProperty("fallbackMarker").GetString() ?? "";
            Assert.False(string.IsNullOrWhiteSpace(marker));
            Assert.Contains(marker, activeSource);
            Assert.False(string.IsNullOrWhiteSpace(detectionMarker));
            Assert.False(string.IsNullOrWhiteSpace(fallbackMarker));
            Assert.Contains(detectionMarker, activeSource);
            Assert.Contains(fallbackMarker, activeSource);
        }

        foreach (string marker in WatchedNewlyMarkers)
        {
            if (activeSource.Contains(marker, StringComparison.Ordinal)) Assert.Contains(marker, declaredMarkers);
        }
    }
}
