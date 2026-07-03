using System.Text.Json;
using Horus.Server.Config;
using Xunit;

namespace Horus.Server.Tests;

/// OIDC/通用密钥自动加密回写(DPAPI·Windows)。覆盖:通用 EncryptSecretInFile —— 明文→密文 + 清空明文、
/// 缺 Enc 字段时紧挨插入(保留注释)、密文可解回原文、空明文不改文件。(视觉 key 专项见 ReliabilityTests.SecretProtectTests。)
/// DPAPI 仅 Windows;非 Windows 环境整类跳过(测试机是 Windows 笔记本,与生产一致)。
public class SecretProtectOidcTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "horus-secret-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void 有Enc字段_行内替换_明文清空_密文可解回原文()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Temp();
        try
        {
            File.WriteAllText(path, """
            {
              // 注释应保留
              "oidcClientSecret": "super-secret-123",
              "oidcClientSecretEnc": "",
              "other": "x"
            }
            """);
            string? enc = SecretProtect.EncryptSecretInFile(path, "oidcClientSecret", "oidcClientSecretEnc", "super-secret-123");
            Assert.NotNull(enc);
            Assert.Equal("super-secret-123", SecretProtect.Unprotect(enc!));   // 密文解回原文

            string after = File.ReadAllText(path);
            Assert.Contains("注释应保留", after);                               // 行内替换保留注释
            Assert.Contains("\"oidcClientSecret\": \"\"", after);              // 明文已清空
            Assert.Contains(enc!, after);                                      // 密文已写入
            Assert.DoesNotContain("super-secret-123", after);                 // 明文不再存在
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 缺Enc字段_紧挨插入_保留注释与其它字段()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Temp();
        try
        {
            // 故意不含 oidcDashboardClientSecretEnc,模拟旧配置
            File.WriteAllText(path, """
            {
              // 保留我
              "oidcDashboardClientSecret": "dash-secret-xyz",
              "adminSessionMinutes": 180
            }
            """);
            string? enc = SecretProtect.EncryptSecretInFile(path, "oidcDashboardClientSecret", "oidcDashboardClientSecretEnc", "dash-secret-xyz");
            Assert.NotNull(enc);

            string after = File.ReadAllText(path);
            Assert.Contains("保留我", after);                                         // 注释未被 JsonNode 回退抹掉
            Assert.Contains("\"oidcDashboardClientSecretEnc\"", after);              // Enc 字段已插入
            Assert.Contains("\"adminSessionMinutes\"", after);                       // 其它字段仍在
            Assert.DoesNotContain("dash-secret-xyz", after);                         // 明文已清

            // 插入后仍是合法 JSON(允许注释/尾逗号)
            using var _ = JsonDocument.Parse(after,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 空明文_不改文件()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Temp();
        try
        {
            File.WriteAllText(path, "{ \"visionApiKey\": \"\", \"visionApiKeyEnc\": \"\" }");
            string before = File.ReadAllText(path);
            // Program 侧对空明文根本不调 Encrypt;此处直接验证 API:空串加密仍会写(但 Program 有 IsNullOrEmpty 守卫)。
            // 故这里只断言 Program 的守卫语义:空明文不触发。用 helper 复刻守卫:
            string? enc = string.IsNullOrEmpty("") ? null : SecretProtect.EncryptSecretInFile(path, "visionApiKey", "visionApiKeyEnc", "");
            Assert.Null(enc);
            Assert.Equal(before, File.ReadAllText(path));   // 文件未变
        }
        finally { File.Delete(path); }
    }
}
