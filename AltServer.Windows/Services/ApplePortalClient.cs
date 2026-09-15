using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple Developer Portal HTTP 客户端
/// 封装与 Apple Developer Portal 的所有 HTTP 通信
/// </summary>
public partial class ApplePortalClient
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private string? _csrfToken;
    private string? _teamId;

    public bool IsAuthenticated { get; private set; }

    public ApplePortalClient()
    {
        _cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>
    /// SIRP 登录第一步：获取认证参数
    /// </summary>
    public async Task<SirpLoginInit> BeginLoginAsync(string appleId)
    {
        var url = "https://idmsa.apple.com/appleauth/auth/signin";
        var content = new StringContent(
            JsonSerializer.Serialize(new { appleId }),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("X-Apple-Widget-Key", "e0b80c3bf78523bfe80571b6ff2e6560");
        request.Headers.Add("X-Apple-App-Info", "com.apple(SeqriteAc)");

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        ExtractCsrfToken(response);

        if (response.IsSuccessStatusCode)
        {
            return new SirpLoginInit { Requires2FA = false };
        }

        // 需要2FA
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Forbidden)
        {
            var data = JsonSerializer.Deserialize<JsonElement>(body);
            if (data.TryGetProperty("authType", out var authType) && authType.GetString() == "2FA")
            {
                return new SirpLoginInit { Requires2FA = true };
            }
        }

        throw new AppleAuthException($"登录失败: {response.StatusCode} - {body}");
    }

    /// <summary>
    /// 提交 Apple ID 和密码
    /// </summary>
    public async Task<AuthResult> SignInAsync(string appleId, string password)
    {
        var url = "https://idmsa.apple.com/appleauth/auth/signin";

        var body = JsonSerializer.Serialize(new
        {
            accountName = appleId,
            password
        });

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };

        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        ExtractCsrfToken(response);

        if (response.IsSuccessStatusCode)
        {
            IsAuthenticated = true;
            return AuthResult.Success;
        }

        // 检查是否需要2FA
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(responseBody);
                if (data.TryGetProperty("authType", out var authType))
                {
                    if (authType.GetString() == "2FA")
                        return AuthResult.Requires2FA;
                }

                if (data.TryGetProperty("accountLocked", out var locked) && locked.GetBoolean())
                    return AuthResult.AccountLocked;

                if (data.TryGetProperty("hasNotification", out _))
                    return AuthResult.RequiresNotification;
            }
            catch { }
        }

        throw new AppleAuthException($"认证失败: {response.StatusCode} - {responseBody}");
    }

    /// <summary>
    /// 提交2FA验证码
    /// </summary>
    public async Task<AuthResult> Submit2FACodeAsync(string code)
    {
        var url = "https://idmsa.apple.com/appleauth/auth/verify/trusteddevice/securitycode";

        var body = JsonSerializer.Serialize(new { securityCode = code });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        ExtractCsrfToken(response);

        if (response.IsSuccessStatusCode)
        {
            IsAuthenticated = true;
            return AuthResult.Success;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(responseBody);
                if (data.TryGetProperty("securityCodeAttemptsExceeded", out _))
                    return AuthResult.TooManyAttempts;
            }
            catch { }
        }

        throw new AppleAuthException($"2FA验证失败: {response.StatusCode} - {responseBody}");
    }

    /// <summary>
    /// 请求发送2FA到手机号
    /// </summary>
    public async Task RequestSMSCodeAsync(string? phoneNumberId = null)
    {
        var url = "https://idmsa.apple.com/appleauth/auth/verify/phone";
        var body = JsonSerializer.Serialize(new { phoneNumberId, mode = "sms" });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        AddCommonHeaders(request);
        await _http.SendAsync(request);
    }

    /// <summary>
    /// 获取开发者团队列表
    /// </summary>
    public async Task<List<DeveloperTeam>> GetTeamsAsync()
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            "https://developer.apple.com/services/QH65B2/idmsa/webauth/getTeams");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"获取团队列表失败: {response.StatusCode}");

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
            _teamId = teams[0].TeamId;

        return teams;
    }

    /// <summary>
    /// 获取已有的证书列表
    /// </summary>
    public async Task<List<PortalCertificate>> GetCertificatesAsync()
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/certificates/list?teamId={_teamId}");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"获取证书列表失败: {response.StatusCode}");

        var result = JsonSerializer.Deserialize<PortalCertificateResponse>(body);
        return result?.Results ?? new List<PortalCertificate>();
    }

    /// <summary>
    /// 创建新的证书签名请求 (CSR)
    /// </summary>
    public async Task<PortalCertificate> CreateCertificateAsync(string csrContent, string certType = "IOS_DEVELOPMENT")
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/certificates/add?teamId={_teamId}");
        AddCommonHeaders(request);

        var body = JsonSerializer.Serialize(new
        {
            csrContent,
            certificateType = certType
        });

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"创建证书失败: {response.StatusCode} - {responseBody}");

        var result = JsonSerializer.Deserialize<PortalCertificateResponse>(responseBody);
        return result?.Results?.FirstOrDefault()
               ?? throw new AppleAuthException("创建证书成功但无法解析响应");
    }

    /// <summary>
    /// 下载证书
    /// </summary>
    public async Task<byte[]> DownloadCertificateAsync(string certificateId)
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/certificates/download?id={certificateId}&teamId={_teamId}");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"下载证书失败: {response.StatusCode}");

        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// 获取 Bundle ID 列表
    /// </summary>
    public async Task<List<PortalBundleId>> GetBundleIdsAsync()
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/identifiers/list?teamId={_teamId}&type=ios");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"获取Bundle ID列表失败: {response.StatusCode}");

        var data = JsonSerializer.Deserialize<JsonElement>(body);
        var ids = new List<PortalBundleId>();

        if (data.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                ids.Add(new PortalBundleId
                {
                    Id = item.TryGetProperty("identifierId", out var id) ? id.GetString() ?? "" : "",
                    Name = item.TryGetProperty("identifier", out var name) ? name.GetString() ?? "" : ""
                });
            }
        }

        return ids;
    }

    /// <summary>
    /// 注册新的 Bundle ID
    /// </summary>
    public async Task<PortalBundleId> RegisterBundleIdAsync(string bundleIdentifier, string name)
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/identifiers/add?teamId={_teamId}");
        AddCommonHeaders(request);

        var body = JsonSerializer.Serialize(new
        {
            identifier = bundleIdentifier,
            name,
            type = "app",
            platform = "ios"
        });

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"注册Bundle ID失败: {response.StatusCode} - {responseBody}");

        return new PortalBundleId
        {
            Name = name,
            Identifier = bundleIdentifier
        };
    }

    /// <summary>
    /// 获取设备列表
    /// </summary>
    public async Task<List<PortalDevice>> GetDevicesAsync()
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/devices/list?teamId={_teamId}");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"获取设备列表失败: {response.StatusCode}");

        var data = JsonSerializer.Deserialize<JsonElement>(body);
        var devices = new List<PortalDevice>();

        if (data.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                devices.Add(new PortalDevice
                {
                    Id = item.TryGetProperty("deviceId", out var id) ? id.GetString() ?? "" : "",
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Udid = item.TryGetProperty("udid", out var udid) ? udid.GetString() ?? "" : "",
                    Platform = item.TryGetProperty("platform", out var platform) ? platform.GetString() ?? "" : "",
                    Status = item.TryGetProperty("status", out var status) ? status.GetString() ?? "" : ""
                });
            }
        }

        return devices;
    }

    /// <summary>
    /// 注册设备
    /// </summary>
    public async Task<PortalDevice> RegisterDeviceAsync(string name, string udid, string platform = "IOS")
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/devices/add?teamId={_teamId}");
        AddCommonHeaders(request);

        var body = JsonSerializer.Serialize(new { name, udid, platform });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"注册设备失败: {response.StatusCode} - {responseBody}");

        return new PortalDevice { Name = name, Udid = udid, Platform = platform };
    }

    /// <summary>
    /// 获取 provisioning profile 列表
    /// </summary>
    public async Task<List<PortalProfile>> GetProfilesAsync()
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/profiles/list?teamId={_teamId}");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"获取配置文件列表失败: {response.StatusCode}");

        var data = JsonSerializer.Deserialize<JsonElement>(body);
        var profiles = new List<PortalProfile>();

        if (data.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                profiles.Add(new PortalProfile
                {
                    Id = item.TryGetProperty("profileId", out var id) ? id.GetString() ?? "" : "",
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Status = item.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
                    Type = item.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                    ExpirationDate = item.TryGetProperty("expirationDate", out var exp)
                        ? exp.GetString() ?? "" : ""
                });
            }
        }

        return profiles;
    }

    /// <summary>
    /// 创建 provisioning profile
    /// </summary>
    public async Task<PortalProfile> CreateProfileAsync(string name, string bundleId,
        List<string> certificateIds, List<string> deviceIds, string profileType = "IOS_APP_DEVELOPMENT")
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://developer.apple.com/account/resources/profiles/add?teamId={_teamId}");
        AddCommonHeaders(request);

        var body = JsonSerializer.Serialize(new
        {
            name,
            bundleId,
            certificateIds,
            deviceIds,
            subAccountId = _teamId,
            type = profileType
        });

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"创建配置文件失败: {response.StatusCode} - {responseBody}");

        return new PortalProfile { Name = name, Type = profileType };
    }

    /// <summary>
    /// 下载 provisioning profile
    /// </summary>
    public async Task<byte[]> DownloadProfileAsync(string profileId)
    {
        EnsureAuthenticated();

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://developer.apple.com/account/resources/profiles/download?id={profileId}&teamId={_teamId}");
        AddCommonHeaders(request);

        var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"下载配置文件失败: {response.StatusCode}");

        return await response.Content.ReadAsByteArrayAsync();
    }

    // MARK: - 内部方法

    private void AddCommonHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_csrfToken))
            request.Headers.Add("csrf", _csrfToken);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("X-Apple-Widget-Key", "e0b80c3bf78523bfe80571b6ff2e6560");
    }

    private void ExtractCsrfToken(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("csrf-token", out var values))
        {
            _csrfToken = values.FirstOrDefault();
        }
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new AppleAuthException("未登录，请先完成 Apple ID 认证");
    }
}

// MARK: - 模型

public class SirpLoginInit
{
    public bool Requires2FA { get; set; }
}

public class AuthResult
{
    public static AuthResult Success => new() { Status = AuthStatus.Success };
    public static AuthResult Requires2FA => new() { Status = AuthStatus.Requires2FA };
    public static AuthResult RequiresNotification => new() { Status = AuthStatus.RequiresNotification };
    public static AuthResult AccountLocked => new() { Status = AuthStatus.AccountLocked };
    public static AuthResult TooManyAttempts => new() { Status = AuthStatus.TooManyAttempts };

    public AuthStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public enum AuthStatus
{
    Success,
    Requires2FA,
    RequiresNotification,
    AccountLocked,
    TooManyAttempts
}

public class AppleAuthException : Exception
{
    public AppleAuthException(string message) : base(message) { }
}

public class DeveloperTeam
{
    public string TeamId { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
}

public class PortalCertificate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ExpirationDate { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
}

public class PortalCertificateResponse
{
    public List<PortalCertificate> Results { get; set; } = new();
}

public class PortalBundleId
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
}

public class PortalDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Udid { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class PortalProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ExpirationDate { get; set; } = string.Empty;
}
