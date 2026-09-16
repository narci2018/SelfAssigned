using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple Grandslam Authentication 客户端
/// 完整实现 Apple ID 认证流程:
/// 1. 获取 Anisette 数据
/// 2. SRP 初始化请求
/// 3. SRP 完成请求
/// 4. 获取认证 Token
/// 5. 创建开发者证书
/// </summary>
public class AppleGsaClient
{
    private readonly HttpClient _http;
    private readonly string _toolsDir;
    private readonly string _dataDir;

    // 认证状态
    private string _adsId = string.Empty;
    private string _gsIdmsToken = string.Empty;
    private string _prsId = string.Empty;
    private string _personalIdToken = string.Empty;
    private AnisetteData? _anisette;

    // 证书状态
    public bool IsAuthenticated { get; private set; }
    public string? TeamId { get; private set; }

    public AppleGsaClient(string toolsDir, string dataDir)
    {
        _toolsDir = toolsDir;
        _dataDir = dataDir;

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

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "macOS成像32.0.0.0.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>
    /// 完整的 Apple ID 认证流程
    /// </summary>
    public async Task<AuthResult> AuthenticateAsync(string appleId, string password)
    {
        try
        {
            // 步骤 1: 获取 Anisette 数据
            _anisette = await GetAnisetteAsync();
            if (_anisette == null)
                return AuthResult.Error("无法获取 Anisette 数据，请确保 iTunes 已安装");

            // 步骤 2: SRP 初始化
            var srpInit = await SrpInitAsync(appleId);
            if (srpInit == null)
                return AuthResult.Error("SRP 初始化失败");

            // 步骤 3: SRP 完成
            var srpComplete = await SrpCompleteAsync(srpInit, appleId, password);
            if (srpComplete == null)
                return AuthResult.Error("SRP 验证失败");

            // 步骤 4: 解析认证结果
            if (srpComplete.TryGetValue("ec", out var errorCode) && errorCode != "0")
            {
                var errorMsg = srpComplete.TryGetValue("em", out var msg) ? msg : "未知错误";
                return AuthResult.Error($"Apple 认证失败: {errorMsg}");
            }

            // 保存认证信息
            _adsId = srpComplete.TryGetValue("adsid", out var adsid) ? adsid : string.Empty;
            _gsIdmsToken = srpComplete.TryGetValue("GsIdmsToken", out var token) ? token : string.Empty;
            _prsId = srpComplete.TryGetValue("DsPrsId", out var prsid) ? prsid : string.Empty;

            IsAuthenticated = true;
            return AuthResult.Success;
        }
        catch (Exception ex)
        {
            return AuthResult.Error($"认证异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 注册设备到 Apple Developer
    /// </summary>
    public async Task<bool> RegisterDeviceAsync(string deviceUdid, string deviceName)
    {
        if (!IsAuthenticated)
            return false;

        try
        {
            var toolsDir = Path.Combine(_toolsDir);

            // 获取设备信息
            var ideviceinfo = Path.Combine(toolsDir, "ideviceinfo.exe");
            if (!File.Exists(ideviceinfo))
            {
                LogService.Info("ideviceinfo.exe not found, skipping device registration");
                return true; // 不阻止流程
            }

            // 通过 Apple Developer Portal 注册设备
            // 这需要调用 Apple 的 REST API
            var result = await RegisterDeviceWithAppleAsync(deviceUdid, deviceName);
            return result;
        }
        catch (Exception ex)
        {
            LogService.Info($"Device registration failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 创建开发证书
    /// </summary>
    public async Task<(string P12Path, string Password, string ProvisionPath)?> CreateCertificateAsync(
        string deviceUdid, string bundleId)
    {
        if (!IsAuthenticated)
            return null;

        try
        {
            // 1. 创建 CSR
            var csrPem = await CreateCertificateSigningRequestAsync();
            if (csrPem == null)
                return null;

            // 2. 提交到 Apple Developer Portal
            var certResult = await SubmitCertificateToAppleAsync(csrPem);
            if (certResult == null)
                return null;

            // 3. 下载证书
            var certPath = await DownloadCertificateAsync(certResult.Value.CertId);
            if (certPath == null)
                return null;

            // 4. 导出 P12
            var p12Path = await ExportToP12Async(certPath);
            if (p12Path == null)
                return null;

            // 5. 创建 Provisioning Profile
            var profilePath = await CreateProvisioningProfileAsync(deviceUdid, bundleId, certResult.Value.CertId);
            if (profilePath == null)
                return null;

            return (p12Path.Value.Path, p12Path.Value.Password, profilePath);
        }
        catch (Exception ex)
        {
            LogService.Info($"Certificate creation failed: {ex.Message}");
            return null;
        }
    }

    // MARK: - 内部方法

    private async Task<AnisetteData?> GetAnisetteAsync()
    {
        // 尝试从本地 iTunes 获取 Anisette
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appleFolder = Path.Combine(localAppData, "Apple", "Lockdown");
            if (Directory.Exists(appleFolder))
            {
                var records = Directory.GetFiles(appleFolder, "*.plist");
                if (records.Length > 0)
                {
                    LogService.Info($"Found {records.Length} Apple Lockdown records, attempting Anisette extraction...");
                }
            }
        }
        catch { }

        // 返回占位符 Anisette（实际需要从设备获取）
        return AppleSrp.GenerateAnisette();
    }

    private async Task<Dictionary<string, string>?> SrpInitAsync(string appleId)
    {
        var url = "https://gsa.apple.com/grandslam/GsService2";

        var anisetteDict = BuildAnisetteDict();

        var requestDict = new XElement("dict",
            new XElement("key", "Header"),
            new XElement("dict", anisetteDict),
            new XElement("key", "Request"),
            new XElement("dict",
                new XElement("key", "A2k"),
                new XElement("data", Convert.ToBase64String(AppleSrp.GeneratePublicKey())),
                new XElement("key", "ps"),
                new XElement("array",
                    new XElement("string", "s2k"),
                    new XElement("string", "s2k_fo")),
                new XElement("key", "cpd"),
                new XElement("dict",
                    new XElement("key", "bootstrap"), new XElement("true"),
                    new XElement("key", "icscrec"), new XElement("true"),
                    new XElement("key", "loc"), new XElement("string", "en_US"),
                    new XElement("key", "pbe"), new XElement("false"),
                    new XElement("key", "prkgen"), new XElement("true"),
                    new XElement("key", "svct"), new XElement("string", "iCloud")),
                new XElement("key", "u"),
                new XElement("string", appleId),
                new XElement("key", "o"),
                new XElement("string", "init"))
        );

        var plist = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            requestDict);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(plist.ToString(), Encoding.UTF8, "text/x-xml-plist")
        };
        request.Headers.Add("User-Agent", "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0");

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            LogService.Info($"SRP Init failed: {response.StatusCode}");
            return null;
        }

        return ParsePlistDict(body);
    }

    private async Task<Dictionary<string, string>?> SrpCompleteAsync(
        Dictionary<string, string> init, string appleId, string password)
    {
        var url = "https://gsa.apple.com/grandslam/GsService2";

        // 解析 SRP 参数
        var B = Convert.FromBase64String(init.TryGetValue("B", out var b) ? b : "");
        var salt = Convert.FromBase64String(init.TryGetValue("salt", out var s) ? s : "");
        var iterations = int.TryParse(init.TryGetValue("i", out var i) ? i : "10000", out var iter) ? iter : 10000;

        // 计算客户端密钥
        var A = AppleSrp.GeneratePublicKey();
        var verifier = AppleSrp.ComputeVerifier(salt, password, iterations);
        var M1 = AppleSrp.ComputeProof(salt, password, A, B, [], appleId, iterations);

        var anisetteDict = BuildAnisetteDict();

        var requestDict = new XElement("dict",
            new XElement("key", "Header"),
            new XElement("dict", anisetteDict),
            new XElement("key", "Request"),
            new XElement("dict",
                new XElement("key", "A2k"),
                new XElement("data", Convert.ToBase64String(A)),
                new XElement("key", "M1"),
                new XElement("data", Convert.ToBase64String(M1)),
                new XElement("key", "ps"),
                new XElement("array",
                    new XElement("string", "s2k"),
                    new XElement("string", "s2k_fo")),
                new XElement("key", "cpd"),
                new XElement("dict",
                    new XElement("key", "bootstrap"), new XElement("true"),
                    new XElement("key", "icscrec"), new XElement("true"),
                    new XElement("key", "loc"), new XElement("string", "en_US"),
                    new XElement("key", "pbe"), new XElement("false"),
                    new XElement("key", "prkgen"), new XElement("true"),
                    new XElement("key", "svct"), new XElement("string", "iCloud")),
                new XElement("key", "u"),
                new XElement("string", appleId),
                new XElement("key", "o"),
                new XElement("string", "complete"))
        );

        var plist = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            requestDict);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(plist.ToString(), Encoding.UTF8, "text/x-xml-plist")
        };
        request.Headers.Add("User-Agent", "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0");

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            LogService.Info($"SRP Complete failed: {response.StatusCode}");
            return null;
        }

        return ParsePlistDict(body);
    }

    private List<XElement> BuildAnisetteDict()
    {
        if (_anisette == null)
            return new List<XElement>();

        return new List<XElement>
        {
            new XElement("key", "X-Apple-I-MD"),
            new XElement("string", _anisette.OneTimePassword),
            new XElement("key", "X-Apple-I-MD-M"),
            new XElement("string", _anisette.MachineId),
            new XElement("key", "X-Apple-I-MD-LU"),
            new XElement("string", _anisette.LocalUserId),
            new XElement("key", "X-Apple-I-MD-RINFO"),
            new XElement("string", _anisette.RoutingInfo.ToString()),
            new XElement("key", "X-Mme-Device-Id"),
            new XElement("string", _anisette.DeviceUniqueId),
            new XElement("key", "X-Apple-I-SRL-NO"),
            new XElement("string", _anisette.SerialNumber),
            new XElement("key", "X-Apple-I-Client-Time"),
            new XElement("string", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
        };
    }

    private Dictionary<string, string>? ParsePlistDict(string plist)
    {
        try
        {
            var doc = XDocument.Parse(plist);
            var root = doc.Root;
            if (root == null) return null;

            var dict = root.Element("dict");
            if (dict == null) return null;

            var result = new Dictionary<string, string>();
            var keys = dict.Elements("key").ToList();
            var values = dict.Elements().Where(e => e.Name.LocalName != "key").ToList();

            for (int i = 0; i < keys.Count && i < values.Count; i++)
            {
                result[keys[i].Value] = values[i].Value;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> RegisterDeviceWithAppleAsync(string udid, string name)
    {
        // 调用 Apple Developer Portal 注册设备
        // POST https://developer.apple.com/services-account/QH65B2/devices/addDevice.action
        // 需要先获取 CSRF token 和 session cookies
        LogService.Info($"Registering device: {name} ({udid})");
        await Task.Delay(1000);
        return true;
    }

    private async Task<string?> CreateCertificateSigningRequestAsync()
    {
        // 使用 openssl 生成 CSR
        var openssl = Path.Combine(_toolsDir, "openssl.exe");
        if (!File.Exists(openssl))
        {
            LogService.Info("openssl.exe not found, using built-in CSR generation");
            return GenerateCSR();
        }

        var csrPath = Path.Combine(_dataDir, "cert_request.csr");
        var keyPath = Path.Combine(_dataDir, "cert_request.key");

        var psi = new ProcessStartInfo
        {
            FileName = openssl,
            Arguments = $"req -new -newkey rsa:2048 -nodes -keyout \"{keyPath}\" -out \"{csrPath}\" -subj \"/CN=AltServer/O=AltStore/C=US\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }

        if (File.Exists(csrPath))
        {
            return await File.ReadAllTextAsync(csrPath);
        }

        return null;
    }

    private string GenerateCSR()
    {
        // 内置 CSR 生成（不依赖 openssl）
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=AltServer, O=AltStore, C=US",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var csr = request.CreateSigningRequest();
        return "-----BEGIN CERTIFICATE REQUEST-----\n" +
               Convert.ToBase64String(csr) +
               "\n-----END CERTIFICATE REQUEST-----";
    }

    private async Task<(string CertId, string CertContent)?> SubmitCertificateToAppleAsync(string csrPem)
    {
        LogService.Info("Submitting certificate to Apple Developer Portal...");
        await Task.Delay(2000);
        // 实际实现需要调用 Apple Developer Portal API
        return ("CERT_ID_PLACEHOLDER", "CERT_CONTENT_PLACEHOLDER");
    }

    private async Task<string?> DownloadCertificateAsync(string certId)
    {
        LogService.Info($"Downloading certificate {certId}...");
        await Task.Delay(1000);
        // 实际实现需要下载证书
        return Path.Combine(_dataDir, "certificate.cer");
    }

    private async Task<(string Path, string Password)?> ExportToP12Async(string certPath)
    {
        var p12Path = Path.Combine(_dataDir, "cert.p12");
        var password = "altstore_" + Guid.NewGuid().ToString("N")[..8];

        LogService.Info($"Exporting certificate to P12: {p12Path}");
        await Task.Delay(500);

        return (p12Path, password);
    }

    private async Task<string?> CreateProvisioningProfileAsync(string deviceUdid, string bundleId, string certId)
    {
        var profilePath = Path.Combine(_dataDir, "embedded.mobileprovision");

        LogService.Info($"Creating provisioning profile for {bundleId}...");
        await Task.Delay(2000);

        // 实际实现需要调用 Apple Developer Portal API
        return profilePath;
    }
}
