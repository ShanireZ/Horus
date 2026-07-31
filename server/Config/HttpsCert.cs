using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Horus.Server.Config;

/// M4·RBAC·S8:监考员看板远端 OIDC 回调需 https(wentian dashboard client 回调是 HTTPS)。
/// 局域网无公网域名/证书,故**启动时自签证书**(首用点过浏览器警告或预装证书)。
/// 仅当 Urls 含 https 时被调用;pfx 落 DataDir 复用(重启不重签,SAN 稳定)。
public static class HttpsCert
{
    /// 加载已有 pfx;不存在则自签生成并落盘。SAN 含 localhost/127.0.0.1/::1/机器名 + 配置的额外主机/IP。
    public static X509Certificate2 LoadOrCreate(ServerConfig cfg, string dataDir)
    {
        string path = string.IsNullOrWhiteSpace(cfg.HttpsCertPath)
            ? Path.Combine(dataDir, "horus-https.pfx")
            : (Path.IsPathRooted(cfg.HttpsCertPath) ? cfg.HttpsCertPath! : Path.Combine(dataDir, cfg.HttpsCertPath!));
        string pwd = cfg.HttpsCertPassword ?? "";

        if (File.Exists(path))
        {
            try { return new X509Certificate2(File.ReadAllBytes(path), pwd, X509KeyStorageFlags.Exportable); }
            catch { /* 坏证书 → 重新生成覆盖 */ }
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
        try { san.AddDnsName(Dns.GetHostName()); } catch { /* 无主机名忽略 */ }
        foreach (string h in (cfg.HttpsSanHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(h, out IPAddress? ip)) san.AddIpAddress(ip);
            else san.AddDnsName(h);
        }
        req.CertificateExtensions.Add(san.Build());

        using X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        byte[] pfx = cert.Export(X509ContentType.Pfx, pwd);
        try { File.WriteAllBytes(path, pfx); } catch { /* 写失败不致命,仍用内存证书 */ }
        return new X509Certificate2(pfx, pwd, X509KeyStorageFlags.Exportable);
    }
}
