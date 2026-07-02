using System.Text.Json;
using Horus.Server.Config;

namespace Horus.Server.Identity;

/// M4:OIDC client_secret 解析(Server-Broker·secret 只在本服务器)。优先级:env(已在 Program 折进 OidcClientSecret)
/// > `OidcClientSecretEnc`(DPAPI 解密) > 空。与视觉 key 同机制(SecretProtect)。
public static class OidcSecret
{
    public static string Resolve(ServerConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.OidcClientSecret)) return cfg.OidcClientSecret!;
        if (string.IsNullOrEmpty(cfg.OidcClientSecretEnc)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("oidcClientSecretEnc(DPAPI)仅 Windows 可解;非 Windows 用 HORUS_OIDC_SECRET env。");
        return SecretProtect.Unprotect(cfg.OidcClientSecretEnc!);
    }
}

/// M4:cpplearn JWKS(RSA 公钥)加载。**内联 `oidcJwksJson` 优先**(局域网离线·免运行时拉取);
/// 否则启动时从 `{issuer}/.well-known/jwks.json` 拉取并缓存到 dataDir,拉取失败回退缓存。
public static class OidcJwks
{
    public static async Task<string> LoadAsync(ServerConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.OidcJwksJson)) return cfg.OidcJwksJson!;

        string url = cfg.OidcIssuer!.TrimEnd('/') + "/.well-known/jwks.json";
        string cachePath = Path.Combine(Path.GetFullPath(cfg.DataDir), "oidc-jwks-cache.json");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string json = await http.GetStringAsync(url);
            using JsonDocument d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("keys", out _))
            {
                try { await File.WriteAllTextAsync(cachePath, json); } catch { /* 缓存写失败不致命 */ }
                return json;
            }
        }
        catch { /* 拉取失败 → 回退缓存 */ }

        if (File.Exists(cachePath)) return await File.ReadAllTextAsync(cachePath);
        throw new InvalidOperationException(
            $"无法获取 OIDC JWKS(内联 oidcJwksJson 未配、从 {url} 拉取失败、无本地缓存)。请配 oidcJwksJson 或确保启动时可达 issuer。");
    }
}
