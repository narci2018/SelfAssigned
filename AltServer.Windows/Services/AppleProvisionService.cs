using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple 证书自动配置服务。
/// 使用 Xcode 原生开发者服务协议 (developerservices2.apple.com/services/QH65B2/*.action)
/// 配合 GSA 获取的 GsIdmsToken + adsid + com.apple.gs.xcode.auth 令牌，
/// 直接完成 Team 获取、设备注册、App ID 注册、证书申请与 Mobileprovision 下载。
/// </summary>
public class AppleProvisionService
{
    private const string XCODE_SERVICES_BASE = "https://developerservices2.apple.com/services/QH65B2";
    private const string CLIENT_ID = "XABBG36SBA";
    private const string PROTOCOL_VERSION = "QH65B2";

    private readonly string _dataDir;
    private readonly AppleGsaClient _gsa;
    private readonly HttpClient _http;
    private string? _adsId;
    private string? _gsIdmsToken;
    private string? _teamId;

    public event Action<string>? Progress;

    public AppleProvisionService(string toolsDir, string dataDir, string? anisetteUrl = null)
    {
        _dataDir = dataDir;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        _gsa = new AppleGsaClient(toolsDir, dataDir, anisetteUrl);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
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
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
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
            if (string.IsNullOrEmpty(_adsId))
                throw new InvalidOperationException("未获取到开发者团队 ID，请重新登录");

            // 1. 获取团队列表 (优先使用真正的 teamId)
            try
            {
                Progress?.Invoke("获取开发者团队信息...");
                var teams = await GetTeamsAsync();
                if (teams.Count > 0 && !string.IsNullOrEmpty(teams[0].TeamId))
                {
                    _teamId = teams[0].TeamId;
                    result.TeamId = _teamId;
                    result.TeamName = teams[0].TeamName;
                    LogService.Info($"[Provision] 开发者团队: {result.TeamName} ({_teamId})");
                }
            }
            catch (Exception ex)
            {
                LogService.Warning($"[Provision] 获取团队列表提示: {ex.Message}，尝试使用默认 ID");
            }

            if (string.IsNullOrEmpty(_teamId))
            {
                _teamId = _adsId;
                result.TeamId = _adsId;
                result.TeamName = $"Apple Developer ({_adsId})";
            }

            // 2. 注册设备
            Progress?.Invoke($"注册设备 {deviceName}...");
            string registeredDeviceId = deviceUdid;
            try
            {
                registeredDeviceId = await RegisterDeviceAsync(deviceName, deviceUdid);
                result.DeviceRegistered = true;
            }
            catch (Exception ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
            {
                result.DeviceRegistered = true;
            }
            catch (Exception ex)
            {
                LogService.Warning($"[Provision] 注册设备提示: {ex.Message}");
            }

            // 3. 注册 Bundle ID
            Progress?.Invoke($"注册 App ID: {bundleId}...");
            string appIdId = bundleId;
            try
            {
                appIdId = await RegisterBundleIdAsync(bundleId, appName);
                result.BundleIdRegistered = true;
            }
            catch (Exception ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
            {
                result.BundleIdRegistered = true;
                var existingId = await FindAppIdIdAsync(bundleId);
                if (!string.IsNullOrEmpty(existingId))
                    appIdId = existingId;
            }
            catch (Exception ex)
            {
                LogService.Warning($"[Provision] 注册 Bundle ID 提示: {ex.Message}");
            }

            // 4. 生成 CSR 并创建证书（或复用本地有效 P12）
            Progress?.Invoke("配置代码签名证书...");
            string? localP12 = FindValidLocalP12(bundleId);
            string? p12Path = null;
            string certId = string.Empty;

            try
            {
                var (csrPem, privateKeyPem) = GenerateCSR();
                var (newCertId, certBytes) = await CreateCertificateAsync(csrPem);
                certId = newCertId;
                Progress?.Invoke("导出 P12 证书...");
                p12Path = await SaveCertificateAsync(certBytes, privateKeyPem, bundleId);
            }
            catch (Exception ex) when (localP12 != null)
            {
                LogService.Warning($"[Provision] 创建新证书提示: {ex.Message}，复用本地已有有效证书: {Path.GetFileName(localP12)}");
                p12Path = localP12;
            }

            if (string.IsNullOrEmpty(p12Path))
            {
                throw new AppleAuthException("未能创建或加载有效的代码签名证书 (.p12)");
            }

            result.CertificateId = certId;
            result.P12Path = p12Path;

            // 5. 生成并下载 Provisioning Profile
            Progress?.Invoke("生成并下载 Provisioning Profile...");
            var (profileId, profileBytes) = await DownloadTeamProfileAsync(appIdId);
            result.ProfileId = profileId;

            // 6. 保存 profile
            Progress?.Invoke("保存配置文件...");
            var mpPath = SaveProfile(profileBytes, bundleId);
            result.ProvisionPath = mpPath;

            result.Success = true;
            LogService.Success($"[Provision] 自动配置成功！P12={Path.GetFileName(p12Path)}, Profile={Path.GetFileName(mpPath)}");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            var fullMsg = ex.InnerException != null ? $"{ex.Message} -> {ex.InnerException.Message}" : ex.Message;
            result.ErrorMessage = fullMsg;
            LogService.Error($"[Provision] 自动配置失败: {fullMsg}");
            LogService.Debug($"[Provision] 堆栈: {ex}");
            return result;
        }
    }

    // MARK: - Xcode Developer Services API

    private async Task<List<DeveloperTeam>> GetTeamsAsync()
    {
        var parameters = new Dictionary<string, object>();
        var dict = await SendXcodeRequestAsync("listTeams.action", parameters);

        var teams = new List<DeveloperTeam>();
        if (dict.TryGetValue("teams", out var teamsObj) && teamsObj is List<object> teamsList)
        {
            foreach (var item in teamsList)
            {
                if (item is Dictionary<string, object> teamDict)
                {
                    var teamId = teamDict.TryGetValue("teamId", out var tid) ? tid?.ToString() ?? "" : "";
                    var teamName = teamDict.TryGetValue("name", out var tn) ? tn?.ToString() ?? "" : "";
                    if (!string.IsNullOrEmpty(teamId))
                    {
                        teams.Add(new DeveloperTeam
                        {
                            TeamId = teamId,
                            TeamName = string.IsNullOrEmpty(teamName) ? $"Team {teamId}" : teamName
                        });
                    }
                }
            }
        }

        if (teams.Count > 0)
        {
            _teamId = teams[0].TeamId;
        }

        return teams;
    }

    private async Task<string> RegisterDeviceAsync(string name, string udid)
    {
        var teamId = _teamId ?? _adsId ?? "";

        // 1. 先查询设备是否已在团队设备列表中
        try
        {
            var listParams = new Dictionary<string, object> { ["teamId"] = teamId };
            var listResult = await SendXcodeRequestAsync("ios/listDevices.action", listParams);
            if (listResult.TryGetValue("devices", out var devsObj) && devsObj is List<object> devsList)
            {
                foreach (var item in devsList)
                {
                    if (item is Dictionary<string, object> d)
                    {
                        var devNum = d.TryGetValue("deviceNumber", out var dn) ? dn?.ToString() : "";
                        var devId = d.TryGetValue("deviceId", out var did) ? did?.ToString() : "";
                        if (string.Equals(devNum, udid, StringComparison.OrdinalIgnoreCase))
                        {
                            LogService.Info($"[Provision] 设备已在团队中: {devId ?? udid}");
                            return !string.IsNullOrEmpty(devId) ? devId : udid;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warning($"[Provision] 查询设备列表提示: {ex.Message}");
        }

        // 2. 注册设备
        var addParams = new Dictionary<string, object>
        {
            ["teamId"] = teamId,
            ["name"] = name,
            ["deviceNumber"] = udid
        };

        try
        {
            var addResult = await SendXcodeRequestAsync("ios/addDevice.action", addParams);
            if (addResult.TryGetValue("device", out var dObj) && dObj is Dictionary<string, object> devDict)
            {
                if (devDict.TryGetValue("deviceId", out var did)) return did?.ToString() ?? udid;
                if (devDict.TryGetValue("deviceNumber", out var dn)) return dn?.ToString() ?? udid;
            }
        }
        catch (Exception ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
        {
            LogService.Info($"[Provision] 设备已注册: {udid}");
        }

        return udid;
    }

    private async Task<string> RegisterBundleIdAsync(string bundleIdentifier, string name)
    {
        var teamId = _teamId ?? _adsId ?? "";

        // 1. 先查询是否存在已注册的 App ID
        var existingId = await FindAppIdIdAsync(bundleIdentifier);
        if (!string.IsNullOrEmpty(existingId))
        {
            LogService.Info($"[Provision] App ID 已存在: {existingId}");
            return existingId;
        }

        // 2. 注册新 App ID
        var addParams = new Dictionary<string, object>
        {
            ["teamId"] = teamId,
            ["appIdName"] = name,
            ["name"] = name,
            ["identifier"] = bundleIdentifier
        };

        try
        {
            var addResult = await SendXcodeRequestAsync("ios/addAppId.action", addParams);
            if (addResult.TryGetValue("appId", out var appObj) && appObj is Dictionary<string, object> appDict)
            {
                if (appDict.TryGetValue("appIdId", out var aid)) return aid?.ToString() ?? bundleIdentifier;
            }
        }
        catch (Exception ex) when (ex.Message.Contains("already") || ex.Message.Contains("exists"))
        {
            var reCheckId = await FindAppIdIdAsync(bundleIdentifier);
            if (!string.IsNullOrEmpty(reCheckId)) return reCheckId;
        }

        return bundleIdentifier;
    }

    private async Task<string?> FindAppIdIdAsync(string bundleId)
    {
        try
        {
            var listParams = new Dictionary<string, object>
            {
                ["teamId"] = _teamId ?? _adsId ?? ""
            };
            var listResult = await SendXcodeRequestAsync("ios/listAppIds.action", listParams);
            if (listResult.TryGetValue("appIds", out var appIdsObj) && appIdsObj is List<object> appIdsList)
            {
                foreach (var item in appIdsList)
                {
                    if (item is Dictionary<string, object> appDict)
                    {
                        var ident = appDict.TryGetValue("identifier", out var idn) ? idn?.ToString() : null;
                        if (string.Equals(ident, bundleId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (appDict.TryGetValue("appIdId", out var aid)) return aid?.ToString();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warning($"[Provision] 查询 App ID 列表提示: {ex.Message}");
        }
        return null;
    }

    private async Task<(string certId, byte[] certBytes)> CreateCertificateAsync(string csrPem)
    {
        var teamId = _teamId ?? _adsId ?? "";
        var parameters = new Dictionary<string, object>
        {
            ["teamId"] = teamId,
            ["csrContent"] = csrPem
        };

        var dict = await SendXcodeRequestAsync("ios/submitDevelopmentCSR.action", parameters);

        string certId = "";
        byte[]? certBytes = null;

        if (dict.TryGetValue("certRequest", out var crObj) && crObj is Dictionary<string, object> crDict)
        {
            if (crDict.TryGetValue("certificateId", out var cid)) certId = cid?.ToString() ?? "";
            if (crDict.TryGetValue("certContent", out var cc))
            {
                certBytes = cc is byte[] b ? b : (cc is string s ? Convert.FromBase64String(s) : null);
            }
        }
        if (certBytes == null && dict.TryGetValue("certificate", out var cObj) && cObj is Dictionary<string, object> cDict)
        {
            if (string.IsNullOrEmpty(certId) && cDict.TryGetValue("certificateId", out var cid)) certId = cid?.ToString() ?? "";
            if (cDict.TryGetValue("certContent", out var cc))
            {
                certBytes = cc is byte[] b ? b : (cc is string s ? Convert.FromBase64String(s) : null);
            }
        }
        if (certBytes == null && dict.TryGetValue("certContent", out var directCc))
        {
            certBytes = directCc is byte[] b ? b : (directCc is string s ? Convert.FromBase64String(s) : null);
        }

        if (certBytes == null || certBytes.Length == 0)
        {
            throw new AppleAuthException("创建证书成功但未在响应中找到证书数据 (certContent)");
        }

        if (string.IsNullOrEmpty(certId))
        {
            certId = Guid.NewGuid().ToString();
        }

        return (certId, certBytes);
    }

    private async Task<(string profileId, byte[] profileBytes)> DownloadTeamProfileAsync(string appIdId)
    {
        var teamId = _teamId ?? _adsId ?? "";
        var parameters = new Dictionary<string, object>
        {
            ["teamId"] = teamId,
            ["appIdId"] = appIdId
        };

        var dict = await SendXcodeRequestAsync("ios/downloadTeamProvisioningProfile.action", parameters);

        string profileId = "";
        byte[]? profileBytes = null;

        if (dict.TryGetValue("provisioningProfile", out var ppObj) && ppObj is Dictionary<string, object> ppDict)
        {
            if (ppDict.TryGetValue("provisioningProfileId", out var pid)) profileId = pid?.ToString() ?? "";
            if (ppDict.TryGetValue("encodedProfile", out var ep))
            {
                profileBytes = ep is byte[] b ? b : (ep is string s ? Convert.FromBase64String(s) : null);
            }
        }

        if (profileBytes == null || profileBytes.Length == 0)
        {
            throw new AppleAuthException("未在响应中获取到 Provisioning Profile 数据 (encodedProfile)");
        }

        if (string.IsNullOrEmpty(profileId))
        {
            profileId = Guid.NewGuid().ToString();
        }

        return (profileId, profileBytes);
    }

    // MARK: - HTTP 请求核心封装

    private async Task<Dictionary<string, object>> SendXcodeRequestAsync(string action, Dictionary<string, object> parameters)
    {
        parameters["clientId"] = CLIENT_ID;
        parameters["protocolVersion"] = PROTOCOL_VERSION;
        parameters["requestId"] = Guid.NewGuid().ToString().ToUpperInvariant();
        parameters["userLocale"] = "en_US";

        var url = $"{XCODE_SERVICES_BASE}/{action}?clientId={CLIENT_ID}";
        var plistXml = BuildPlist(parameters);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(plistXml, Encoding.UTF8, "text/x-xml-plist")
        };
        AddXcodeHeaders(request);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        LogService.Info($"[Provision] {action} 响应: {response.StatusCode}, body: {Truncate(body, 300)}");

        if (!response.IsSuccessStatusCode)
            throw new AppleAuthException($"开发者服务请求失败: {response.StatusCode} - {Truncate(body, 200)}");

        var dict = ParsePlist(body);
        if (dict == null)
            throw new AppleAuthException($"无法解析开发者服务响应: {Truncate(body, 200)}");

        if (dict.TryGetValue("resultCode", out var rcObj))
        {
            long rc = Convert.ToInt64(rcObj);
            if (rc != 0)
            {
                var userString = dict.TryGetValue("userString", out var u) ? u?.ToString() : null;
                var resultString = dict.TryGetValue("resultString", out var r) ? r?.ToString() : null;
                var errorMsg = !string.IsNullOrEmpty(userString) ? userString :
                               !string.IsNullOrEmpty(resultString) ? resultString : $"ResultCode {rc}";
                throw new AppleAuthException($"开发者服务返回错误 ({rc}): {errorMsg}");
            }
        }

        return dict;
    }

    private void AddXcodeHeaders(HttpRequestMessage request)
    {
        var gsToken = BuildGSIdentityToken();
        if (!string.IsNullOrEmpty(gsToken))
            request.Headers.TryAddWithoutValidation("X-Apple-Identity-Token", gsToken);
        if (!string.IsNullOrEmpty(_gsa.AuthToken))
            request.Headers.TryAddWithoutValidation("X-Apple-GS-Token", _gsa.AuthToken);

        var anisette = _gsa.Anisette;
        if (anisette != null)
        {
            if (!string.IsNullOrEmpty(anisette.ClientTime))
                request.Headers.TryAddWithoutValidation("X-Apple-I-Client-Time", anisette.ClientTime);
            if (!string.IsNullOrEmpty(anisette.MachineId))
                request.Headers.TryAddWithoutValidation("X-Apple-I-MD-M", anisette.MachineId);
            if (!string.IsNullOrEmpty(anisette.OneTimePassword))
                request.Headers.TryAddWithoutValidation("X-Apple-I-MD", anisette.OneTimePassword);
            if (anisette.RoutingInfo != 0)
                request.Headers.TryAddWithoutValidation("X-Apple-I-MD-RINFO", anisette.RoutingInfo.ToString());
            if (!string.IsNullOrEmpty(anisette.TimeZone))
                request.Headers.TryAddWithoutValidation("X-Apple-I-TimeZone", anisette.TimeZone);
            if (!string.IsNullOrEmpty(anisette.Locale))
                request.Headers.TryAddWithoutValidation("X-Apple-Locale", anisette.Locale);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", "Xcode");
        request.Headers.TryAddWithoutValidation("Accept", "text/x-xml-plist");
        request.Headers.TryAddWithoutValidation("X-Mme-Client-Info", "<MacBookPro15,1> <macOS;13.5;22G74> <com.apple.dt.Xcode/14.3.1 (14E300c)>");
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    // MARK: - Plist 生成与解析

    private static string BuildPlist(Dictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        WritePlistDict(sb, dict);
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    private static void WritePlistValue(StringBuilder sb, object value)
    {
        switch (value)
        {
            case string s:
                sb.Append("<string>").Append(SecurityElement.Escape(s)).AppendLine("</string>");
                break;
            case int i:
                sb.Append("<integer>").Append(i).AppendLine("</integer>");
                break;
            case long l:
                sb.Append("<integer>").Append(l).AppendLine("</integer>");
                break;
            case bool b:
                sb.AppendLine(b ? "<true/>" : "<false/>");
                break;
            case byte[] data:
                sb.Append("<data>").Append(Convert.ToBase64String(data)).AppendLine("</data>");
                break;
            case Dictionary<string, object> dict:
                WritePlistDict(sb, dict);
                break;
            case System.Collections.IEnumerable list when !(value is string):
                sb.AppendLine("<array>");
                foreach (var item in list)
                {
                    WritePlistValue(sb, item);
                }
                sb.AppendLine("</array>");
                break;
            default:
                sb.Append("<string>").Append(SecurityElement.Escape(value.ToString() ?? "")).AppendLine("</string>");
                break;
        }
    }

    private static void WritePlistDict(StringBuilder sb, Dictionary<string, object> dict)
    {
        sb.AppendLine("<dict>");
        foreach (var kvp in dict)
        {
            sb.Append("<key>").Append(kvp.Key).AppendLine("</key>");
            WritePlistValue(sb, kvp.Value);
        }
        sb.AppendLine("</dict>");
    }

    private static object? ParsePlistElement(XElement elem)
    {
        switch (elem.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var children = elem.Elements().ToList();
                for (int i = 0; i < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName == "key" && i + 1 < children.Count)
                    {
                        var key = children[i].Value;
                        var val = ParsePlistElement(children[i + 1]);
                        if (val != null) dict[key] = val;
                    }
                }
                return dict;
            case "array":
                var list = new List<object>();
                foreach (var child in elem.Elements())
                {
                    var val = ParsePlistElement(child);
                    if (val != null) list.Add(val);
                }
                return list;
            case "string":
                return elem.Value;
            case "integer":
                return long.TryParse(elem.Value, out var n) ? n : 0L;
            case "data":
                try { return Convert.FromBase64String(elem.Value.Replace(" ", "").Replace("\r", "").Replace("\n", "")); }
                catch { return elem.Value; }
            case "true":
                return true;
            case "false":
                return false;
            case "date":
                return elem.Value;
            default:
                return elem.Value;
        }
    }

    private static Dictionary<string, object>? ParsePlist(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var rootDict = doc.Root?.Element("dict");
            if (rootDict != null)
                return ParsePlistElement(rootDict) as Dictionary<string, object>;
        }
        catch (Exception ex)
        {
            LogService.Warning($"[Provision] 解析 plist 异常: {ex.Message}");
        }
        return null;
    }

    // MARK: - CSR 生成与本地证书管理

    private (string csrPem, string privateKeyPem) GenerateCSR()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=iOS Development",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var csrBytes = request.CreateSigningRequest();
        var csrPem = "-----BEGIN CERTIFICATE REQUEST-----\n" +
                     Convert.ToBase64String(csrBytes, Base64FormattingOptions.InsertLineBreaks) +
                     "\n-----END CERTIFICATE REQUEST-----";

        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        return (csrPem, privateKeyPem);
    }

    private string? FindValidLocalP12(string bundleId)
    {
        try
        {
            var p12Dir = Path.Combine(_dataDir, "certs");
            if (!Directory.Exists(p12Dir)) return null;

            var files = Directory.GetFiles(p12Dir, "*.p12").OrderByDescending(File.GetLastWriteTime);
            foreach (var f in files)
            {
                try
                {
                    var cert = new X509Certificate2(f, "temp123", X509KeyStorageFlags.Exportable);
                    if (cert.HasPrivateKey && DateTime.Now < cert.NotAfter.AddDays(-1))
                    {
                        LogService.Info($"[Provision] 找到本地有效证书: {Path.GetFileName(f)} (有效期至 {cert.NotAfter:yyyy-MM-dd})");
                        return f;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private async Task<string> SaveCertificateAsync(byte[] certDer, string privateKeyPem, string bundleId)
    {
        var cert = new X509Certificate2(certDer);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        using var certWithKey = cert.CopyWithPrivateKey(rsa);

        var pfxBytes = certWithKey.Export(X509ContentType.Pfx, "temp123");
        var p12Dir = Path.Combine(_dataDir, "certs");
        Directory.CreateDirectory(p12Dir);
        var p12Path = Path.Combine(p12Dir, $"{bundleId}_{DateTime.Now:yyyyMMdd}.p12");

        await File.WriteAllBytesAsync(p12Path, pfxBytes);
        return p12Path;
    }

    private string SaveProfile(byte[] profileData, string bundleId)
    {
        var profilesDir = Path.Combine(_dataDir, "profiles");
        Directory.CreateDirectory(profilesDir);
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
