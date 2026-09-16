using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple Grandslam Authentication client (ported from iPASide gsa.py + anisette.py)
/// Key differences from naive implementation:
/// - Anisette data from remote server (no local DLLs needed)
/// - Proper plist format with XML prolog and DOCTYPE
/// - Header includes Version "1.0.1"
/// - cpd includes ALL anisette headers
/// - Proper SRP with s2k/s2k_fo protocol handling
/// </summary>
public class AppleGsaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _toolsDir;
    private readonly string _dataDir;

    private AnisetteData? _anisette;
    private string _sessionKey = string.Empty;
    private string _adsId = string.Empty;
    private string _gsIdmsToken = string.Empty;

    public bool IsAuthenticated { get; private set; }
    public string? TeamId { get; private set; }

    private const string GS_ENDPOINT = "https://gsa.apple.com/grandslam/GsService2";
    private const string GS_USER_AGENT = "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0";
    private const string PLIST_PROLOG = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n";

    // Remote Anisette servers (tried in order)
    private static readonly string[] AnisetteUrls = new[]
    {
        "https://ani.sidestore.io/",
        "https://ani.sidestore.io/v3/get_headers"
    };

    public AppleGsaClient(string toolsDir, string dataDir)
    {
        _toolsDir = toolsDir;
        _dataDir = dataDir;

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

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
    }

    /// <summary>
    /// Complete authentication flow
    /// </summary>
    public async Task<AuthResult> AuthenticateAsync(string appleId, string password)
    {
        try
        {
            LogService.Info($"[GSA] 开始认证: {appleId}");

            // Step 1: Get Anisette data
            LogService.Info("[GSA] 步骤1: 获取 Anisette 数据...");
            _anisette = await GetAnisetteAsync();
            if (_anisette == null)
            {
                LogService.Error("[GSA] 无法获取 Anisette 数据");
                return AuthResult.Error("无法获取 Anisette 数据。请检查网络连接。");
            }
            var otpPreview = _anisette.OneTimePassword.Length > 20
                ? _anisette.OneTimePassword[..20]
                : _anisette.OneTimePassword;
            LogService.Info($"[GSA] Anisette 获取成功: OTP={otpPreview}...");

            // Step 2: SRP Init
            LogService.Info("[GSA] 步骤2: SRP 初始化...");
            var anisetteHeaders = _anisette.ToDictionary();
            var initResult = await GsRequest(new Dictionary<string, object>
            {
                ["A2k"] = AppleSrp.GeneratePublicKey(),
                ["ps"] = new object[] { "s2k", "s2k_fo" },
                ["u"] = appleId,
                ["o"] = "init"
            }, anisetteHeaders);

            if (initResult == null)
            {
                LogService.Error("[GSA] SRP Init 返回 null");
                return AuthResult.Error("SRP 初始化失败 - 无法连接 Apple 服务器");
            }

            // Check for errors
            if (initResult.TryGetValue("ec", out var initEc) && initEc != "0")
            {
                var em = initResult.TryGetValue("em", out var initEm) ? initEm : "未知";
                LogService.Error($"[GSA] SRP Init 错误: ec={initEc}, em={em}");
                return AuthResult.Error($"Apple 服务器拒绝: {em}");
            }

            // Get SRP parameters
            var protocol = initResult.TryGetValue("sp", out var sp) ? sp : "s2k";
            var salt = initResult.TryGetValue("s", out var s) && !string.IsNullOrEmpty(s)
                ? Convert.FromBase64String(s) : new byte[32];
            var iterations = initResult.TryGetValue("i", out var iterStr) && int.TryParse(iterStr, out var iter)
                ? iter : 10000;
            var serverB = initResult.TryGetValue("B", out var b64B) && !string.IsNullOrEmpty(b64B)
                ? Convert.FromBase64String(b64B) : Array.Empty<byte>();
            var c = initResult.TryGetValue("c", out var cVal) ? cVal : "";

            LogService.Info($"[GSA] SRP Init: protocol={protocol}, salt={salt.Length}B, iter={iterations}");

            if (serverB.Length == 0)
            {
                LogService.Error("[GSA] SRP Init 响应中缺少 B 参数");
                return AuthResult.Error("SRP 初始化失败 - 服务器响应异常");
            }

            // Step 3: Compute M1 and send complete
            LogService.Info("[GSA] 步骤3: SRP 验证...");
            bool s2kFo = protocol == "s2k_fo";
            var A = AppleSrp.GeneratePublicKey();
            var M1 = AppleSrp.ComputeProof(salt, password, A, serverB, appleId, iterations, s2kFo);

            var completeResult = await GsRequest(new Dictionary<string, object>
            {
                ["c"] = c,
                ["M1"] = M1,
                ["u"] = appleId,
                ["o"] = "complete"
            }, anisetteHeaders);

            if (completeResult == null)
            {
                LogService.Error("[GSA] SRP Complete 返回 null");
                return AuthResult.Error("SRP 验证失败 - 密码错误或网络问题");
            }

            // Check result
            if (completeResult.TryGetValue("ec", out var completeEc))
            {
                LogService.Info($"[GSA] SRP Complete: ec={completeEc}");

                if (completeEc == "0")
                {
                    // Success
                    _adsId = completeResult.TryGetValue("adsid", out var adsid) ? adsid : "";
                    _gsIdmsToken = completeResult.TryGetValue("GsIdmsToken", out var token) ? token : "";
                    IsAuthenticated = true;
                    LogService.Info($"[GSA] 认证成功! adsid={_adsId}");
                    return AuthResult.Success;
                }
                else if (completeEc == "-26402")
                {
                    LogService.Info("[GSA] 需要双重认证");
                    return AuthResult.Requires2FA;
                }
                else
                {
                    var em = completeResult.TryGetValue("em", out var msg) ? msg : "未知错误";
                    LogService.Error($"[GSA] 认证失败: ec={completeEc}, em={em}");
                    return AuthResult.Error($"Apple 认证失败: {em}");
                }
            }

            // No ec field, check for adsid as success indicator
            if (completeResult.ContainsKey("adsid"))
            {
                _adsId = completeResult["adsid"];
                IsAuthenticated = true;
                return AuthResult.Success;
            }

            LogService.Error("[GSA] 无法解析认证响应");
            return AuthResult.Error("无法解析 Apple 服务器响应");
        }
        catch (TaskCanceledException)
        {
            LogService.Error("[GSA] 请求超时");
            return AuthResult.Error("请求超时 - 请检查网络连接和 VPN 设置");
        }
        catch (HttpRequestException ex)
        {
            LogService.Error($"[GSA] 网络错误: {ex.Message}");
            return AuthResult.Error($"网络错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogService.Error($"[GSA] 认证异常: {ex}");
            return AuthResult.Error($"认证异常: {ex.Message}");
        }
    }

    // MARK: - Anisette

    private async Task<AnisetteData?> GetAnisetteAsync()
    {
        // Method 1: Remote Anisette server
        try
        {
            foreach (var url in AnisetteUrls)
            {
                try
                {
                    LogService.Info($"[Anisette] 尝试远程服务器: {url}");

                    string? response = null;

                    if (url == "https://ani.sidestore.io/")
                    {
                        // Root URL returns Anisette directly via GET
                        using var getHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                        getHttp.DefaultRequestHeaders.Add("X-MMe-Client-Info",
                            "<MacBookPro13,2> <macOS;13.1;22C65> <com.apple.AuthKit/1 (com.apple.dt.Xcode/3594.4.19)>");
                        response = await getHttp.GetStringAsync(url);
                    }
                    else
                    {
                        // v3 endpoint requires POST with empty body
                        using var postHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                        postHttp.DefaultRequestHeaders.Add("X-MMe-Client-Info",
                            "<MacBookPro13,2> <macOS;13.1;22C65> <com.apple.AuthKit/1 (com.apple.dt.Xcode/3594.4.19)>");
                        var postResponse = await postHttp.PostAsync(url, null);
                        LogService.Info($"[Anisette] v3 POST 响应: {postResponse.StatusCode}");
                        if (postResponse.IsSuccessStatusCode)
                            response = await postResponse.Content.ReadAsStringAsync();
                        else
                            LogService.Error($"[Anisette] v3 POST 失败: {await postResponse.Content.ReadAsStringAsync()}");
                    }

                    if (response == null) continue;

                    LogService.Info($"[Anisette] 服务器响应长度: {response.Length}");

                    var json = JsonDocument.Parse(response);
                    var root = json.RootElement;

                    var anisette = new AnisetteData
                    {
                        OneTimePassword = root.TryGetProperty("X-Apple-I-MD", out var md) ? md.GetString() ?? "" : "",
                        MachineId = root.TryGetProperty("X-Apple-I-MD-M", out var mdm) ? mdm.GetString() ?? "" : "",
                        RoutingInfo = root.TryGetProperty("X-Apple-I-MD-RINFO", out var rinfo)
                            && long.TryParse(rinfo.GetString(), out var ri) ? ri : 17106176,
                        LocalUserId = root.TryGetProperty("X-Apple-I-MD-LU", out var lu) ? lu.GetString() ?? "-2" : "-2",
                        DeviceUniqueId = root.TryGetProperty("X-Mme-Device-Id", out var devid)
                            ? devid.GetString() ?? Guid.NewGuid().ToString("N")[..32]
                            : Guid.NewGuid().ToString("N")[..32],
                        SerialNumber = root.TryGetProperty("X-Apple-I-SRL-NO", out var srl)
                            ? srl.GetString() ?? "F2LX1234ABCD"
                            : "F2LX1234ABCD"
                    };

                    if (!string.IsNullOrEmpty(anisette.OneTimePassword) && !string.IsNullOrEmpty(anisette.MachineId))
                    {
                        LogService.Info($"[Anisette] 远程获取成功!");
                        return anisette;
                    }
                    LogService.Error("[Anisette] 服务器返回了空的 Anisette 数据");
                }
                catch (Exception ex)
                {
                    LogService.Error($"[Anisette] 服务器 {url} 失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[Anisette] 远程获取异常: {ex.Message}");
        }

        // Method 2: Local anisette-server (fallback)
        try
        {
            var anisetteServer = Path.Combine(_toolsDir, "anisette-server.exe");
            if (File.Exists(anisetteServer))
            {
                LogService.Info("[Anisette] 尝试本地 anisette-server...");
                var localResult = await TryLocalAnisetteServer(anisetteServer);
                if (localResult != null) return localResult;
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[Anisette] 本地服务器失败: {ex.Message}");
        }

        LogService.Error("[Anisette] 所有 Anisette 来源均失败");
        return null;
    }

    private async Task<AnisetteData?> TryLocalAnisetteServer(string serverPath)
    {
        var workDir = Path.Combine(_dataDir, "anisette-work");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);
        File.Copy(serverPath, Path.Combine(workDir, "anisette-server.exe"), true);

        // Copy Apple DLLs
        var searchPaths = new[]
        {
            @"C:\Program Files\Common Files\Apple\Apple Application Support",
            @"C:\Program Files\iTunes",
        };

        foreach (var dir in searchPaths)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var dll in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    try { File.Copy(dll, Path.Combine(workDir, Path.GetFileName(dll)), true); } catch { }
                }
            }
            catch { }
        }

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(workDir, "anisette-server.exe"),
            Arguments = "-p 6969",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000);
            if (process.HasExited)
            {
                LogService.Error($"[Anisette] 本地服务器退出 (exit={process.ExitCode})");
                return null;
            }

            try
            {
                using var testHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await testHttp.GetStringAsync("http://localhost:6969/get_headers?udid=-2");
                var json = JsonDocument.Parse(response);
                var root = json.RootElement;

                var anisette = new AnisetteData
                {
                    OneTimePassword = root.TryGetProperty("X-Apple-I-MD", out var md) ? md.GetString() ?? "" : "",
                    MachineId = root.TryGetProperty("X-Apple-I-MD-M", out var mdm) ? mdm.GetString() ?? "" : "",
                    RoutingInfo = 17106176,
                    LocalUserId = "-2",
                    DeviceUniqueId = Guid.NewGuid().ToString("N")[..32],
                    SerialNumber = "F2LX1234ABCD"
                };

                try { process.Kill(); } catch { }
                return anisette;
            }
            catch { }
        }

        try { process.Kill(); } catch { }
        return null;
    }

    // MARK: - GSA Request

    private async Task<Dictionary<string, string>?> GsRequest(
        Dictionary<string, object> requestParams,
        Dictionary<string, string> anisetteHeaders)
    {
        // Build cpd (client-provided data) - includes ALL anisette headers + flags
        var cpd = new Dictionary<string, object>
        {
            ["bootstrap"] = true,
            ["icscrec"] = true,
            ["pbe"] = false,
            ["prkgen"] = true,
            ["svct"] = "iCloud",
            ["loc"] = anisetteHeaders.TryGetValue("X-Apple-Locale", out var loc) ? loc : "en_US",
        };

        // Add ALL anisette headers to cpd (except X-MMe-Client-Info)
        foreach (var kvp in anisetteHeaders)
        {
            if (kvp.Key != "X-MMe-Client-Info")
                cpd[kvp.Key] = kvp.Value;
        }

        // Build full body: Header + Request
        var body = new Dictionary<string, object>
        {
            ["Header"] = new Dictionary<string, object> { ["Version"] = "1.0.1" },
            ["Request"] = new Dictionary<string, object>(requestParams)
        };
        ((Dictionary<string, object>)body["Request"])["cpd"] = cpd;

        // Serialize to Apple plist format
        var plistBody = ToApplePlist(body);

        LogService.Info($"[SRP] POST {GS_ENDPOINT}");
        LogService.Info($"[SRP] 请求体长度: {plistBody.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, GS_ENDPOINT)
        {
            Content = new StringContent(plistBody, Encoding.UTF8, "text/x-xml-plist")
        };
        request.Headers.Add("User-Agent", GS_USER_AGENT);
        request.Headers.Add("Accept", "*/*");

        LogService.Info("[SRP] 发送请求...");
        var response = await _http.SendAsync(request);
        LogService.Info($"[SRP] 响应: {response.StatusCode}");

        var responseBody = await response.Content.ReadAsStringAsync();
        LogService.Info($"[SRP] 响应体长度: {responseBody.Length}");

        if (!response.IsSuccessStatusCode)
        {
            var preview = responseBody.Length > 200 ? responseBody[..200] : responseBody;
            LogService.Error($"[SRP] 失败: {response.StatusCode} - {preview}");
            return null;
        }

        return ParseApplePlist(responseBody);
    }

    // MARK: - Apple Plist Serialization/Deserialization

    /// <summary>
    /// Serialize a dictionary to Apple plist XML format
    /// </summary>
    private static string ToApplePlist(object obj)
    {
        var sb = new StringBuilder();
        sb.Append(PLIST_PROLOG);
        sb.Append("<plist version=\"1.0\">\n");
        AppendPlistValue(sb, obj, 0);
        sb.Append("</plist>");
        return sb.ToString();
    }

    private static void AppendPlistValue(StringBuilder sb, object? value, int indent)
    {
        var pad = new string(' ', indent * 2);

        if (value is null)
        {
            sb.Append($"{pad}<none/>\n");
        }
        else if (value is bool b)
        {
            sb.Append($"{pad}<{(b ? "true" : "false")}/>\n");
        }
        else if (value is int i)
        {
            sb.Append($"{pad}<integer>{i}</integer>\n");
        }
        else if (value is long l)
        {
            sb.Append($"{pad}<integer>{l}</integer>\n");
        }
        else if (value is double d)
        {
            sb.Append($"{pad}<real>{d}</real>\n");
        }
        else if (value is string s)
        {
            sb.Append($"{pad}<string>{EscapeXml(s)}</string>\n");
        }
        else if (value is byte[] data)
        {
            sb.Append($"{pad}<data>{Convert.ToBase64String(data)}</data>\n");
        }
        else if (value is Dictionary<string, object> dict)
        {
            sb.Append($"{pad}<dict>\n");
            foreach (var kvp in dict)
            {
                sb.Append($"{pad}  <key>{EscapeXml(kvp.Key)}</key>\n");
                AppendPlistValue(sb, kvp.Value, indent + 2);
            }
            sb.Append($"{pad}</dict>\n");
        }
        else if (value is object[] arr)
        {
            sb.Append($"{pad}<array>\n");
            foreach (var item in arr)
            {
                AppendPlistValue(sb, item, indent + 2);
            }
            sb.Append($"{pad}</array>\n");
        }
        else if (value is List<object> list)
        {
            sb.Append($"{pad}<array>\n");
            foreach (var item in list)
            {
                AppendPlistValue(sb, item, indent + 2);
            }
            sb.Append($"{pad}</array>\n");
        }
        else
        {
            sb.Append($"{pad}<string>{EscapeXml(value.ToString() ?? "")}</string>\n");
        }
    }

    private static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    /// <summary>
    /// Parse Apple plist XML response into a flat string dictionary
    /// Handles both Response-wrapped and direct dict responses
    /// </summary>
    private Dictionary<string, string>? ParseApplePlist(string plist)
    {
        try
        {
            LogService.Info($"[Plist] 解析响应...");

            // Strip XML prolog if present
            var xml = plist;
            var plistStart = xml.IndexOf("<plist");
            if (plistStart >= 0) xml = xml[plistStart..];
            var dictStart = xml.IndexOf("<dict");
            if (dictStart >= 0) xml = xml[dictStart..];

            var doc = System.Xml.Linq.XDocument.Parse($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n{xml}\n</plist>");
            var root = doc.Root;
            if (root == null) return null;

            var dict = root.Element("dict") ?? root;
            return ParseDictElement(dict);
        }
        catch (Exception ex)
        {
            LogService.Error($"[Plist] 解析失败: {ex.Message}");
            LogService.Error($"[Plist] 原始响应前500字符: {plist[..Math.Min(500, plist.Length)]}");
            return null;
        }
    }

    private Dictionary<string, string> ParseDictElement(System.Xml.Linq.XElement dict)
    {
        var result = new Dictionary<string, string>();
        var elements = dict.Elements().ToList();

        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].Name.LocalName == "key")
            {
                var key = elements[i].Value;
                i++;
                if (i < elements.Count)
                {
                    var valElement = elements[i];
                    string val;
                    switch (valElement.Name.LocalName)
                    {
                        case "string":
                        case "integer":
                        case "real":
                            val = valElement.Value;
                            break;
                        case "true":
                            val = "true";
                            break;
                        case "false":
                            val = "false";
                            break;
                        case "data":
                            val = valElement.Value;
                            break;
                        case "dict":
                            // For nested dicts (like Response), flatten with prefix
                            var nested = ParseDictElement(valElement);
                            foreach (var kvp in nested)
                                result[kvp.Key] = kvp.Value;
                            continue;
                        case "array":
                            // Array of strings
                            var items = valElement.Elements("string").Select(e => e.Value).ToList();
                            val = string.Join(",", items);
                            break;
                        default:
                            val = valElement.Value;
                            break;
                    }
                    result[key] = val;
                }
            }
        }

        LogService.Info($"[Plist] 解析成功, {result.Count} 个字段");
        foreach (var kvp in result)
        {
            var preview = kvp.Value.Length > 50 ? kvp.Value[..50] : kvp.Value;
            LogService.Info($"  {kvp.Key} = {preview}...");
        }

        return result;
    }

    // MARK: - Certificate Management (stubs)

    public Task<bool> RegisterDeviceAsync(string udid, string name)
    {
        LogService.Info($"[Cert] 注册设备: {name} ({udid})");
        return Task.FromResult(true);
    }

    public Task<(string P12Path, string Password, string ProvisionPath)?> CreateCertificateAsync(
        string deviceUdid, string bundleId)
    {
        LogService.Info($"[Cert] 创建证书: {bundleId}");
        return Task.FromResult<(string, string, string)?>(null);
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
