using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple 证书自动配置服务。
/// 走 GSA/SRP 认证（与 SideStore/icloud-auth 相同流程），拿到 GsIdmsToken + adsid
/// 后直接调用 Apple Developer Portal REST API。
/// </summary>
public class AppleProvisionService
{
    private readonly string _dataDir;
    private readonly AppleGsaClient _gsa;
    private readonly HttpClient _http;
    private string? _adsId;
    private string? _gsIdmsToken;

    public event Action<string>? Progress;

    public AppleProvisionService(string toolsDir, string dataDir, string? anisetteUrl = null)
    {
        _dataDir = dataDir;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        _gsa = new AppleGsaClient(toolsDir, dataDir, anisetteUrl);
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs"));
        Directory.CreateDirectory(Path.Combine(_dataDir, "profiles"));
    }

    public AppleProvisionService(AppleGsaClient gsa, string dataDir)
    {
        _dataDir = dataDir;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        _gsa = gsa;
        _adsId = gsa.AdsId ?? string.Empty;
        _gsIdmsToken = gsa.GsIdmsToken ?? string.Empty;
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        Directory.CreateDirectory(Path.Combine(_dataDir, "certs"));
        Directory.CreateDirectory(Path.Combine(_dataDir, "profiles"));
    }

    // MARK: - 认证（GSA/SRP 流程）

    public async Task<AuthResult> SignInAsync(string appleId, string password)
    {
        var result = await _gsa.AuthenticateAsync(appleId, password);
        // 认证成功后保存会话 token
        if (result.Status == AuthStatus.Success)
        {
            _adsId = _gsa.AdsId ?? string.Empty;
            _gsIdmsToken = _gsa.GsIdmsToken ?? string.Empty;
            LogService.Info($"[Provision] 保存 GSA 会话: adsid={_adsId}, token={(_gsIdmsToken?.Length ?? 0)}B");
        }
        return result;
    }

    public async Task<AuthResult> Submit2FACodeAsync(string code)
    {
        var result = await _gsa.Submit2FACodeAsync(code);
        // 2FA 成功后保存新会话
        if (result.Status == AuthStatus.Success)
        {
            _adsId = _gsa.AdsId ?? string.Empty;
            _gsIdmsToken = _gsa.GsIdmsToken ?? string.Empty;
            LogService.Info($"[Provision] 2FA 后保存 GSA 会话: adsid={_adsId}");
        }
        return result;
    }

    /// <summary>设置已认证的会话 token，供开发者门户 API 使用</summary>
    public void SetSessionToken(string adsId, string gsIdmsToken)
    {
        _adsId = adsId;
        _gsIdmsToken = gsIdmsToken;
    }

    private string BuildGSIdentityToken()
    {
        if (string.IsNullOrEmpty(_adsId) || string.IsNullOrEmpty(_gsIdmsToken))
            return string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_adsId}:{_gsIdmsToken}"));
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
            // 1. 直接使用 GSA 返回的 adsid 作为团队 ID
            if (string.IsNullOrEmpty(_adsId))
                throw new InvalidOperationException("未获取到开发者团队 ID，请重新登录");

            result.TeamId = _adsId;
            result.TeamName = $"Apple Developer ({_adsId})";

            // 2. 注册设备
            Progress?.Invoke($"注册设备 {deviceName}...");
            try
            {
                await RegisterDeviceAsync(deviceName, deviceUdid);
                result.DeviceRegistered = true;
            }
            catch (Exception ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
            {
                result.DeviceRegistered = true;
            }

            // 3. 注册 Bundle ID
            Progress?.Invoke($"注册 App ID: {bundleId}...");
            try
            {
                await RegisterBundleIdAsync(bundleId, appName);
                result.BundleIdRegistered = true;
            }
            catch (Exception ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
            {
                result.BundleIdRegistered = true;
            }

            // 4. 生成 CSR 并创建证书
            Progress?.Invoke("创建代码签名证书...");
            var (csrContent, privateKeyPem) = GenerateCSR();
            var cert = await CreateCertificateAsync(csrContent);
            result.CertificateId = cert.Id;

            // 5. 下载证书
            Progress?.Invoke("下载证书...");
            var certBytes = await DownloadCertificateAsync(cert.Id);
            var p12Path = await SaveCertificateAsync(certBytes, privateKeyPem, bundleId);
            result.P12Path = p12Path;

            // 6. 创建 provisioning profile
            Progress?.Invoke("创建 Provisioning Profile...");
            var profile = await CreateProfileAsync(
                $"{appName} Development",
                bundleId,
                new List<string> { cert.Id },
                new List<string>());
            result.ProfileId = profile.Id;

            // 7. 下载 profile
            Progress?.Invoke("下载配置文件...");
            var profileBytes = await DownloadProfileAsync(profile.Id);
            var mpPath = SaveProfile(profileBytes, bundleId);
            result.ProvisionPath = mpPath;

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            LogService.Error($"[Provision] 自动配置失败: {ex.Message}");
            return result;
        }
    }

    // MARK: - 开发者门户 API（使用 GsIdmsToken 直接认证）

    private async Task<List<DeveloperTeam>> GetTeamsAsync()
    {
        // 使用 GsIdmsToken 直接调用开发者门户 API
        var request = new HttpRequestMessage(HttpMethod.Get,
            "https://developer.apple.com/services/QH65B2/idmsa/webauth/getTeams");
        AddPortalHeaders(request);
        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        LogService.Info($"[Provision] getTeams 响应: {response.StatusCode}, body={Truncate(body, 200)}");

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"获取团队列表失败: {response.StatusCode} - {Truncate(body, 200)}");

        var data = JsonSerializer.Deserialize<JsonElement>(body);
        var teams = new List<DeveloperTeam>();

        if (data.TryGetProperty("teams", out var teamsArray))
        {
            foreach (var team in teamsArray.EnumerateArray())
            {
                teams.Add(new DeveloperTeam
                {
                    TeamId = team.GetProperty("teamId").GetString() ?? "",
                    TeamName = team.TryGetProperty("teamName", out var name) ? name.GetString() ?? "" : ""
                });
            }
        }

        if (teams.Count > 0)
        {
            _adsId = teams[0].TeamId;
        }

        return teams;
    }

    private async Task RegisterDeviceAsync(string name, string udid)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/devices/add?teamId={_adsId}");
        AddPortalHeaders(request);
        var body = JsonSerializer.Serialize(new { name, udid, platform = "IOS" });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"注册设备失败: {response.StatusCode} - {Truncate(responseBody, 200)}");
    }

    private async Task RegisterBundleIdAsync(string bundleIdentifier, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/identifiers/add?teamId={_adsId}");
        AddPortalHeaders(request);
        var body = JsonSerializer.Serialize(new { identifier = bundleIdentifier, name, type = "app", platform = "ios" });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"注册 Bundle ID 失败: {response.StatusCode} - {Truncate(responseBody, 200)}");
    }

    private async Task<PortalCertificate> CreateCertificateAsync(string csrContent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/certificates/add?teamId={_adsId}");
        AddPortalHeaders(request);
        var body = JsonSerializer.Serialize(new { csrContent, certificateType = "IOS_DEVELOPMENT" });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"创建证书失败: {response.StatusCode} - {Truncate(responseBody, 200)}");

        var data = JsonSerializer.Deserialize<JsonElement>(responseBody);
        // 尝试从响应中解析
        var certId = data.TryGetProperty("resultId", out var rid) ? rid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(certId))
            certId = data.TryGetProperty("certificateId", out var cid) ? cid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(certId))
            certId = data.TryGetProperty("certId", out var certIdVal) ? certIdVal.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(certId))
            throw new AppleAuthException($"创建证书成功但无法解析证书 ID: {Truncate(responseBody, 500)}");

        return new PortalCertificate { Id = certId, Name = "iOS Development" };
    }

    private async Task<byte[]> DownloadCertificateAsync(string certificateId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/certificates/download?id={certificateId}&teamId={_adsId}");
        AddPortalHeaders(request);
        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"下载证书失败: {response.StatusCode}");

        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<PortalProfile> CreateProfileAsync(string name, string bundleId,
        List<string> certificateIds, List<string> deviceIds)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/profiles/add?teamId={_adsId}");
        AddPortalHeaders(request);
        var body = JsonSerializer.Serialize(new
        {
            name,
            bundleId,
            certificateIds,
            deviceIds,
            subAccountId = _adsId,
            type = "IOS_APP_DEVELOPMENT"
        });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"创建配置文件失败: {response.StatusCode} - {Truncate(responseBody, 200)}");

        var data = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var profileId = data.TryGetProperty("resultId", out var rid) ? rid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(profileId))
            profileId = data.TryGetProperty("profileId", out var pid) ? pid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(profileId))
            profileId = data.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";

        return new PortalProfile { Id = profileId, Name = name };
    }

    private async Task<byte[]> DownloadProfileAsync(string profileId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/profiles/download?id={profileId}&teamId={_adsId}");
        AddPortalHeaders(request);
        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"下载配置文件失败: {response.StatusCode}");

        return await response.Content.ReadAsByteArrayAsync();
    }

    // MARK: - 内部方法

    private void AddPortalHeaders(HttpRequestMessage request)
    {
        var gsToken = BuildGSIdentityToken();
        if (!string.IsNullOrEmpty(gsToken))
            request.Headers.Add("X-Apple-Identity-Token", gsToken);
        if (!string.IsNullOrEmpty(_gsa.AuthToken))
            request.Headers.Add("X-Apple-GS-Token", _gsa.AuthToken);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("User-Agent", "Xcode");
        request.Headers.Add("Accept", "application/json");
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    // MARK: - CSR 生成

    private (string csrContent, string privateKeyPem) GenerateCSR()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=iOS Development",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

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
    public string TeamId { get; set; } = string.Empty;
    public bool DeviceRegistered { get; set; }
    public bool BundleIdRegistered { get; set; }
    public string CertificateId { get; set; } = string.Empty;
    public string P12Path { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProvisionPath { get; set; } = string.Empty;
}
