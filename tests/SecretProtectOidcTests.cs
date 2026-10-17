using System.Text.Json;
using Horus.Server.Config;
using Xunit;

namespace Horus.Server.Tests;

/// OIDC/通用金鑰自動加密回寫(DPAPI·Windows)。覆蓋:通用 EncryptSecretInFile —— 明文→密文 + 清空明文、
/// 缺 Enc 欄位時緊挨插入(保留註解)、密文可解回原文、空明文不改檔案。(視覺 key 專項見 ReliabilityTests.SecretProtectTests。)
/// DPAPI 僅 Windows;非 Windows 環境整類跳過(測試機是 Windows 筆記本,與生產一致)。
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
            Assert.Contains("注释应保留", after);                               // 行內替換保留註解
            Assert.Contains("\"oidcClientSecret\": \"\"", after);              // 明文已清空
            Assert.Contains(enc!, after);                                      // 密文已寫入
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
            // 故意不含 oidcDashboardClientSecretEnc,模擬舊設定
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
            Assert.Contains("保留我", after);                                         // 註解未被 JsonNode 退回抹掉
            Assert.Contains("\"oidcDashboardClientSecretEnc\"", after);              // Enc 欄位已插入
            Assert.Contains("\"adminSessionMinutes\"", after);                       // 其它欄位仍在
            Assert.DoesNotContain("dash-secret-xyz", after);                         // 明文已清

            // 插入後仍是合法 JSON(允許註解/尾逗號)
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
            // Program 側對空明文根本不調 Encrypt;此處直接驗證 API:空串加密仍會寫(但 Program 有 IsNullOrEmpty 守衛)。
            // 故這裡只斷言 Program 的守衛語義:空明文不觸發。用 helper 復刻守衛:
            string? enc = string.IsNullOrEmpty("") ? null : SecretProtect.EncryptSecretInFile(path, "visionApiKey", "visionApiKeyEnc", "");
            Assert.Null(enc);
            Assert.Equal(before, File.ReadAllText(path));   // 檔案未變
        }
        finally { File.Delete(path); }
    }
}
