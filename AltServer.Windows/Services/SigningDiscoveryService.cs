using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace AltServer.Windows.Services;

/// <summary>
/// 智能签名配置发现服务：自动扫描证书库和本地文件
/// </summary>
public class SigningDiscoveryService
{
    private readonly string _baseDir;

    public SigningDiscoveryService()
    {
        _baseDir = AppContext.BaseDirectory;
    }

    public record CertificateInfo(string Subject, string Thumbprint, DateTime NotAfter, string Source);
    public record ProvisionInfo(string Name, string Path, DateTime Expiry, string Source);

    /// <summary>扫描 Windows 证书库中的代码签名证书</summary>
    public List<CertificateInfo> DiscoverCertificates()
    {
        var results = new List<CertificateInfo>();

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            foreach (var cert in store.Certificates)
            {
                if (!cert.HasPrivateKey) continue;
                if (cert.NotAfter < DateTime.Now) continue;

                var isCodeSigning = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                    .Any(ext => ext.EnhancedKeyUsages.Cast<Oid>()
                        .Any(u => u.FriendlyName.Contains("Code Signing") ||
                                  u.Value == "1.3.6.1.5.5.7.3.3"));

                if (!isCodeSigning) continue;

                results.Add(new CertificateInfo(
                    cert.Subject,
                    cert.Thumbprint,
                    cert.NotAfter,
                    $"证书库 ({cert.Subject})"));
            }
        }
        catch
        {
            // 证书库不可用时静默
        }

        return results;
    }

    /// <summary>扫描本地 .p12 文件</summary>
    public List<CertificateInfo> DiscoverP12Files()
    {
        var results = new List<CertificateInfo>();
        var searchDirs = GetSearchDirectories();

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.p12", SearchOption.AllDirectories))
                {
                    try
                    {
                        var cert = new X509Certificate2(file, "", X509KeyStorageFlags.EphemeralKeySet);
                        if (cert.HasPrivateKey && cert.NotAfter > DateTime.Now)
                        {
                            results.Add(new CertificateInfo(
                                cert.Subject,
                                cert.Thumbprint,
                                cert.NotAfter,
                                file));
                        }
                    }
                    catch
                    {
                        // 有密码保护或格式错误
                        var name = Path.GetFileNameWithoutExtension(file);
                        results.Add(new CertificateInfo(
                            name,
                            file,
                            DateTime.MaxValue,
                            file));
                    }
                }
            }
            catch { }
        }

        return results;
    }

    /// <summary>扫描本地 .mobileprovision 文件</summary>
    public List<ProvisionInfo> DiscoverProvisionProfiles()
    {
        var results = new List<ProvisionInfo>();
        var searchDirs = GetSearchDirectories();

        // 也搜索 iTunes 常见路径
        var extraDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "MobileDevice", "Provisioning Profiles"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Apple", "MobileDeviceSupport"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files",
                "Apple", "Mobile Device Support", "Provisioning Profiles"),
            _baseDir
        };

        var allDirs = searchDirs.Concat(extraDirs).Distinct();

        foreach (var dir in allDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.mobileprovision", SearchOption.AllDirectories))
                {
                    var expiry = SigningService.ParseProvisionExpiry(file);
                    var name = Path.GetFileNameWithoutExtension(file);

                    results.Add(new ProvisionInfo(
                        name,
                        file,
                        expiry ?? DateTime.MaxValue,
                        dir));
                }
            }
            catch { }
        }

        return results;
    }

    private List<string> GetSearchDirectories()
    {
        var dirs = new List<string>
        {
            _baseDir,
            Path.Combine(_baseDir, "certs"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltServer"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltServer"),
            @"C:\ProgramData\i4\i4tools\ipasign",
            @"C:\ProgramData\i4\i4tools\ipasign\cnf",
            @"C:\ProgramData\3u\3utools\ipasign",
            @"C:\ProgramData\3u\3utools\ipasign\cnf",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "i4", "i4tools", "ipasign"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "i4", "i4tools", "ipasign", "cnf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "3u", "3utools", "ipasign"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "3u", "3utools", "ipasign", "cnf"),
        };
        return dirs.Distinct().ToList();
    }
}
