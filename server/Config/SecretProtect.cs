using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Horus.Server.Config;

/// 敏感設定(視覺端點 API key)加密儲存:**設定檔案裡不存明文**。
/// 用 Windows DPAPI(機器範圍)—— 無需另管主金鑰,密文**只在本機可解**(移機需重新加密,對監考筆記本是安全加分)。
///
/// 用法:在**部署那臺機器**上跑 `Horus.Server protect-secret <明文key>` 列印密文,粘進 server.config.json 的 `visionApiKeyEnc`;
/// 伺服器啟動時讀取該密文並解密載入(僅進記憶體)。DPAPI 是 Windows 專屬,非 Windows 呼叫會拋清晰錯誤。
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

    /// **自動加密回寫**(通用):把設定檔案裡 `plainField` 的明文加密為 `encField` 密文並清空明文欄位。
    /// 優先**行內替換**(保留註解與其它欄位原樣);缺 `encField` 欄位時優先**緊挨 plainField 插入**該欄位(仍保註解),
    /// 再不行才退回 JsonNode 重寫(補欄位·會丟註解)。回傳密文(供呼叫方更新記憶體設定);解析失敗回傳 null。
    /// **只動這兩個欄位,絕不寫入 env 注入的其它秘密**(唯讀改檔案自身內容;呼叫方須傳檔案裡的明文,而非 env 覆蓋後的值)。
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
            m => { hasEnc = true; return m.Groups[1].Value + enc + m.Groups[2].Value; });     // 密文寫入(MatchEvaluator 回傳字面值,enc 內 $ 不被解釋)
        if (hasKey && hasEnc) { File.WriteAllText(path, edited); return enc; }

        // encField 缺失但 plainField 在:在被清空的 plainField 行後緊挨插入 encField 行(保留其它註解/欄位)。
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

        // 退回:JsonNode 重寫(補欄位;註解會丟,資料保全,不含 env 秘密)
        JsonNode? root = JsonNode.Parse(text,
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (root is not JsonObject obj) return null;
        obj[encField] = enc;
        obj[plainField] = "";
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return enc;
    }

    /// 向後相容包裝:視覺 key 自動加密(等價 EncryptSecretInFile(path,"visionApiKey","visionApiKeyEnc",...))。
    [SupportedOSPlatform("windows")]
    public static string? EncryptVisionKeyInFile(string path, string plaintext)
        => EncryptSecretInFile(path, "visionApiKey", "visionApiKeyEnc", plaintext);

    /// 解析視覺端點 API key,按優先級:env(HORUS_VISION_KEY,整合測試/CI)> `visionApiKeyEnc`(DPAPI 解密)> `visionApiKey`(明文,僅整合測試)。
    /// 非 Windows 上遇 `visionApiKeyEnc` 拋錯(生產=Windows 筆記本)。
    public static string Resolve(ServerConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.VisionApiKey)) return cfg.VisionApiKey!;   // 明文(env 已在 Program 覆蓋進此欄位)/ 整合測試
        if (string.IsNullOrEmpty(cfg.VisionApiKeyEnc)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("visionApiKeyEnc(DPAPI)仅 Windows 可解密;非 Windows 请用 HORUS_VISION_KEY 明文注入。");
        return Unprotect(cfg.VisionApiKeyEnc!);
    }
}
