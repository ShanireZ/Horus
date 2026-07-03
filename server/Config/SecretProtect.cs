using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Horus.Server.Config;

/// 敏感配置(视觉端点 API key)加密存储:**配置文件里不存明文**。
/// 用 Windows DPAPI(机器范围)—— 无需另管主密钥,密文**只在本机可解**(移机需重新加密,对监考笔记本是安全加分)。
///
/// 用法:在**部署那台机器**上跑 `Horus.Server protect-secret <明文key>` 打印密文,粘进 server.config.json 的 `visionApiKeyEnc`;
/// 服务器启动时读取该密文并解密加载(仅进内存)。DPAPI 是 Windows 专属,非 Windows 调用会抛清晰错误。
public static class SecretProtect
{
    [SupportedOSPlatform("windows")]
    public static string Protect(string plaintext)
    {
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(enc);
    }

    [SupportedOSPlatform("windows")]
    public static string Unprotect(string base64)
    {
        byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(base64), optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(dec);
    }

    /// **自动加密回写**(通用):把配置文件里 `plainField` 的明文加密为 `encField` 密文并清空明文字段。
    /// 优先**行内替换**(保留注释与其它字段原样);缺 `encField` 字段时优先**紧挨 plainField 插入**该字段(仍保注释),
    /// 再不行才回退 JsonNode 重写(补字段·会丢注释)。返回密文(供调用方更新内存配置);解析失败返回 null。
    /// **只动这两个字段,绝不写入 env 注入的其它秘密**(只读改文件自身内容;调用方须传文件里的明文,而非 env 覆盖后的值)。
    [SupportedOSPlatform("windows")]
    public static string? EncryptSecretInFile(string path, string plainField, string encField, string plaintext)
    {
        string enc = Protect(plaintext);
        string text = File.ReadAllText(path);
        string pf = Regex.Escape(plainField), ef = Regex.Escape(encField);

        bool hasKey = false, hasEnc = false;
        string edited = Regex.Replace(text, "(\"" + pf + "\"\\s*:\\s*\")[^\"]*(\")",
            m => { hasKey = true; return m.Groups[1].Value + m.Groups[2].Value; });          // 明文 → ""
        edited = Regex.Replace(edited, "(\"" + ef + "\"\\s*:\\s*\")[^\"]*(\")",
            m => { hasEnc = true; return m.Groups[1].Value + enc + m.Groups[2].Value; });     // 密文写入(MatchEvaluator 返回字面量,enc 内 $ 不被解释)
        if (hasKey && hasEnc) { File.WriteAllText(path, edited); return enc; }

        // encField 缺失但 plainField 在:在被清空的 plainField 行后紧挨插入 encField 行(保留其它注释/字段)。
        if (hasKey && !hasEnc)
        {
            bool inserted = false;
            string withEnc = Regex.Replace(edited, "([ \\t]*)(\"" + pf + "\"\\s*:\\s*\"\")(,?)([ \\t]*\\r?\\n)",
                m =>
                {
                    inserted = true;
                    string indent = m.Groups[1].Value;
                    return m.Value + indent + "\"" + encField + "\": \"" + enc + "\",\n";
                });
            if (inserted) { File.WriteAllText(path, withEnc); return enc; }
        }

        // 回退:JsonNode 重写(补字段;注释会丢,数据保全,不含 env 秘密)
        JsonNode? root = JsonNode.Parse(text,
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (root is not JsonObject obj) return null;
        obj[encField] = enc;
        obj[plainField] = "";
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return enc;
    }

    /// 向后兼容包装:视觉 key 自动加密(等价 EncryptSecretInFile(path,"visionApiKey","visionApiKeyEnc",...))。
    [SupportedOSPlatform("windows")]
    public static string? EncryptVisionKeyInFile(string path, string plaintext)
        => EncryptSecretInFile(path, "visionApiKey", "visionApiKeyEnc", plaintext);

    /// 解析视觉端点 API key,按优先级:env(HORUS_VISION_KEY,联调/CI)> `visionApiKeyEnc`(DPAPI 解密)> `visionApiKey`(明文,仅联调)。
    /// 非 Windows 上遇 `visionApiKeyEnc` 抛错(生产=Windows 笔记本)。
    public static string Resolve(ServerConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.VisionApiKey)) return cfg.VisionApiKey!;   // 明文(env 已在 Program 覆盖进此字段)/ 联调
        if (string.IsNullOrEmpty(cfg.VisionApiKeyEnc)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("visionApiKeyEnc(DPAPI)仅 Windows 可解密;非 Windows 请用 HORUS_VISION_KEY 明文注入。");
        return Unprotect(cfg.VisionApiKeyEnc!);
    }
}
