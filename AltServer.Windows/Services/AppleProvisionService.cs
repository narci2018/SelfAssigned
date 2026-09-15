using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple 证书自动配置服务：
/// 输入 Apple ID + 密码 → 自动登录 → 创建证书 → 创建配置文件 → 保存本地
/// </summary>
public class AppleProvisionService
{
    private readonly string _dataDir;
    private readonly ApplePortalClient _portal;

    public event Action<string>? Progress;

    public AppleProvisionService(string dataDir)
    {
        _dataDir = dataDir;
        _portal = new ApplePortalClient();
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs"));
        Directory.CreateDirectory(Path.Combine(_dataDir, "profiles"));
    }

    // MARK: - 认证

    public async Task<AuthResult> SignInAsync(string appleId, string password)
    {
        return await _portal.SignInAsync(appleId, password);
    }

    public async Task<AuthResult> Submit2FACodeAsync(string code)
    {
        return await _portal.Submit2FACodeAsync(code);
    }

    public async Task RequestSMSCodeAsync(string? phoneNumberId = null)
    {
        await _portal.RequestSMSCodeAsync(phoneNumberId);
    }

    // MARK: - 一键配置

    public async Task<ProvisionResult> AutoProvisionAsync(
        string bundleId,
        string appName,
        string deviceName,
        string deviceUdid)
    {
        var result = new ProvisionResult();

        try
        {
            // 1. 获取团队
            Progress?.Invoke("获取开发者团队信息...");
            var teams = await _portal.GetTeamsAsync();
            if (teams.Count == 0)
                throw new InvalidOperationException("未找到开发者团队，请确认 Apple ID 已加入开发者计划");

            result.TeamName = teams[0].TeamName;

            // 2. 注册设备
            Progress?.Invoke($"注册设备 {deviceName}...");
            try
            {
                await _portal.RegisterDeviceAsync(deviceName, deviceUdid);
                result.DeviceRegistered = true;
            }
            catch (AppleAuthException ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
            {
                result.DeviceRegistered = true;
            }

            // 3. 注册 Bundle ID
            Progress?.Invoke($"注册 App ID: {bundleId}...");
            try
            {
                await _portal.RegisterBundleIdAsync(bundleId, appName);
                result.BundleIdRegistered = true;
            }
            catch (AppleAuthException ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
            {
                result.BundleIdRegistered = true;
            }

            // 4. 生成 CSR 并创建证书
            Progress?.Invoke("创建代码签名证书...");
            var (csrContent, privateKeyPem) = GenerateCSR();
            var cert = await _portal.CreateCertificateAsync(csrContent, "IOS_DEVELOPMENT");
            result.CertificateId = cert.Id;

            // 5. 下载证书
            Progress?.Invoke("下载证书...");
            var certBytes = await _portal.DownloadCertificateAsync(cert.Id);
            var p12Path = await SaveCertificateAsync(certBytes, privateKeyPem, bundleId);
            result.P12Path = p12Path;

            // 6. 创建 provisioning profile
            Progress?.Invoke("创建 Provisioning Profile...");
            var profile = await _portal.CreateProfileAsync(
                $"{appName} Development",
                bundleId,
                new List<string> { cert.Id },
                new List<string>(), // 设备ID后续填充
                "IOS_APP_DEVELOPMENT");
            result.ProfileId = profile.Id;

            // 7. 下载 profile
            Progress?.Invoke("下载配置文件...");
            var profileBytes = await _portal.DownloadProfileAsync(profile.Id);
            var mpPath = SaveProfile(profileBytes, bundleId);
            result.ProvisionPath = mpPath;

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    // MARK: - CSR 生成

    private (string csrContent, string privateKeyPem) GenerateCSR()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=iOS Development",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var serialNumber = new byte[20];
        RandomNumberGenerator.Fill(serialNumber);
        request.CertificateSerialNumber = serialNumber;

        var csrBytes = request.CreateSigningRequest();
        var csrPem = $"-----BEGIN CERTIFICATE REQUEST-----\n" +
                     Convert.ToBase64String(csrBytes, Base64FormattingOptions.InsertLineBreaks) +
                     "\n-----END CERTIFICATE REQUEST-----";

        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        return (Convert.ToBase64String(csrBytes), privateKeyPem);
    }

    // MARK: - 保存文件

    private async Task<string> SaveCertificateAsync(byte[] certDer, string privateKeyPem, string bundleId)
    {
        var cert = new X509Certificate2(certDer);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var pfxBytes = cert.Export(X509ContentType.Pfx, "temp123");
        var p12Dir = Path.Combine(_dataDir, "certs");
        var p12Path = Path.Combine(p12Dir, $"{bundleId}_{DateTime.Now:yyyyMMdd}.p12");

        await File.WriteAllBytesAsync(p12Path, pfxBytes);
        return p12Path;
    }

    private string SaveProfile(byte[] profileData, string bundleId)
    {
        var profilesDir = Path.Combine(_dataDir, "profiles");
        var mpPath = Path.Combine(profilesDir, $"{bundleId}_{DateTime.Now:yyyyMMdd}.mobileprovision");
        File.WriteAllBytes(mpPath, profileData);
        return mpPath;
    }
}

// MARK: - 结果模型

public class ProvisionResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public bool DeviceRegistered { get; set; }
    public bool BundleIdRegistered { get; set; }
    public string CertificateId { get; set; } = string.Empty;
    public string P12Path { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProvisionPath { get; set; } = string.Empty;
}
