using System.Text.Json;
using Horus.Server.Config;

namespace Horus.Server.Identity;

/// M4:OIDC client_secret 解析(Server-Broker·secret 只在本伺服器)。優先級:env(已在 Program 折進 OidcClientSecret)
/// > `OidcClientSecretEnc`(DPAPI 解密) > 空。與視覺 key 同機制(SecretProtect)。
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

    /// M4·RBAC:監考員 dashboard client_secret 解析。優先級:env(已折進 OidcDashboardClientSecret) > Enc(DPAPI) > 空。
    public static string ResolveDashboard(ServerConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.OidcDashboardClientSecret)) return cfg.OidcDashboardClientSecret!;
        if (string.IsNullOrEmpty(cfg.OidcDashboardClientSecretEnc)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("oidcDashboardClientSecretEnc(DPAPI)仅 Windows 可解;非 Windows 用 HORUS_OIDC_DASHBOARD_SECRET env。");
        return SecretProtect.Unprotect(cfg.OidcDashboardClientSecretEnc!);
    }
}

/// M4:wentian JWKS(RSA 公鑰)載入。**內聯 `oidcJwksJson` 優先**(區域網路離線·免執行環境拉取);
/// 否則啟動時從貝塔通的 `/jwks`(端點前綴見 ServerConfig.OidcEndpointBase·P72)拉取並快取到 dataDir,拉取失敗退回快取。
public static class OidcJwks
{
    public static async Task<string> LoadAsync(ServerConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.OidcJwksJson)) return cfg.OidcJwksJson!;

        string url = cfg.OidcJwksEndpoint!;
        string cachePath = Path.Combine(Path.GetFullPath(cfg.DataDir), "oidc-jwks-cache.json");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string json = await http.GetStringAsync(url);
            using JsonDocument d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("keys", out _))
            {
                try { await File.WriteAllTextAsync(cachePath, json); } catch { /* 快取寫失敗不致命 */ }
                return json;
            }
        }
        catch { /* 拉取失敗 → 退回快取 */ }

        if (File.Exists(cachePath)) return await File.ReadAllTextAsync(cachePath);
        throw new InvalidOperationException(
            $"无法获取 OIDC JWKS(内联 oidcJwksJson 未配、从 {url} 拉取失败、无本地缓存)。请配 oidcJwksJson 或确保启动时可达 issuer。");
    }
}

