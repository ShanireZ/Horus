using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Horus.Server.Config;

/// M4·RBAC·S8:監考員看板遠端 OIDC 回呼需 https(wentian dashboard client 回呼是 HTTPS)。
/// 區域網路無公用網路網域/證書,故**啟動時自簽證書**(首用點過瀏覽器警告或預裝證書)。
/// 僅當 Urls 含 https 時被呼叫;pfx 落 DataDir 重複使用(重新啟動不重籤,SAN 穩定)。
public static class HttpsCert
{
    /// 載入已有 pfx;不存在則自籤生成並寫入磁碟。SAN 含 localhost/127.0.0.1/::1/機器名 + 設定的額外主機/IP。
    public static X509Certificate2 LoadOrCreate(ServerConfig cfg, string dataDir)
    {
        string path = string.IsNullOrWhiteSpace(cfg.HttpsCertPath)
            ? Path.Combine(dataDir, "horus-https.pfx")
            : (Path.IsPathRooted(cfg.HttpsCertPath) ? cfg.HttpsCertPath! : Path.Combine(dataDir, cfg.HttpsCertPath!));
        string pwd = cfg.HttpsCertPassword ?? "";

        if (File.Exists(path))
        {
            try { return new X509Certificate2(File.ReadAllBytes(path), pwd, X509KeyStorageFlags.Exportable); }
            catch { /* 壞證書 → 重新生成覆蓋 */ }
        }

        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Horus 监考服务器", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        try { san.AddDnsName(Dns.GetHostName()); } catch { /* 無主機名忽略 */ }
        foreach (string h in (cfg.HttpsSanHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(h, out IPAddress? ip)) san.AddIpAddress(ip);
            else san.AddDnsName(h);
        }
        req.CertificateExtensions.Add(san.Build());

        using X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        byte[] pfx = cert.Export(X509ContentType.Pfx, pwd);
        try { File.WriteAllBytes(path, pfx); } catch { /* 寫失敗不致命,仍用記憶體證書 */ }
        return new X509Certificate2(pfx, pwd, X509KeyStorageFlags.Exportable);
    }
}
