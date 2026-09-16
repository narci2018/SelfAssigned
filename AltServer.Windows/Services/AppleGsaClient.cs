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
/// </summary>
public class AppleGsaClient
{
    private readonly HttpClient _http;
    private readonly string _toolsDir;
    private readonly string _dataDir;
    private Process? _anisetteProcess;

    private string _adsId = string.Empty;
    private string _gsIdmsToken = string.Empty;
    private string _prsId = string.Empty;
    private AnisetteData? _anisette;

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

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>
    /// 完整认证流程
    /// </summary>
    public async Task<AuthResult> AuthenticateAsync(string appleId, string password)
    {
        try
        {
            LogService.Info($"[GSA] 开始认证: {appleId}");

            // 步骤 1: 获取 Anisette
            LogService.Info("[GSA] 步骤1: 获取 Anisette 数据...");
            _anisette = await GetAnisetteAsync();
            if (_anisette == null)
            {
                LogService.Error("[GSA] 无法获取 Anisette 数据");
                return AuthResult.Error("无法获取 Anisette 数据。请确保已安装 iTunes 和 iCloud (从 Apple 网站下载，非 Microsoft Store 版本)");
            }
            LogService.Info($"[GSA] Anisette 获取成功: OTP={_anisette.OneTimePassword[..Math.Min(20, _anisette.OneTimePassword.Length)]}...");

            // 步骤 2: SRP Init
            LogService.Info("[GSA] 步骤2: SRP 初始化请求...");
            var srpInit = await SrpInitAsync(appleId);
            if (srpInit == null)
            {
                LogService.Error("[GSA] SRP Init 返回 null");
                return AuthResult.Error("SRP 初始化失败 - 无法连接 Apple 服务器");
            }
            LogService.Info($"[GSA] SRP Init 响应: ec={(srpInit.TryGetValue("ec", out var ec) ? ec : "N/A")}");

            // 检查错误码
            if (srpInit.TryGetValue("ec", out var initEc) && initEc != "0")
            {
                var em = srpInit.TryGetValue("em", out var initEm) ? initEm : "未知";
                LogService.Error($"[GSA] SRP Init 错误: ec={initEc}, em={em}");
                return AuthResult.Error($"Apple 服务器拒绝: {em}");
            }

            // 步骤 3: SRP Complete
            LogService.Info("[GSA] 步骤3: SRP 验证...");
            var srpComplete = await SrpCompleteAsync(srpInit, appleId, password);
            if (srpComplete == null)
            {
                LogService.Error("[GSA] SRP Complete 返回 null");
                return AuthResult.Error("SRP 验证失败 - 密码错误或网络问题");
            }

            // 解析结果
            if (srpComplete.TryGetValue("ec", out var errorCode))
            {
                LogService.Info($"[GSA] SRP Complete: ec={errorCode}");

                if (errorCode == "0")
                {
                    // 成功
                    _adsId = srpComplete.TryGetValue("adsid", out var adsid) ? adsid : "";
                    _gsIdmsToken = srpComplete.TryGetValue("GsIdmsToken", out var token) ? token : "";
                    _prsId = srpComplete.TryGetValue("DsPrsId", out var prsid) ? prsid : "";
                    IsAuthenticated = true;
                    LogService.Info($"[GSA] 认证成功! adsid={_adsId}");
                    return AuthResult.Success;
                }
                else if (errorCode == "-26402")
                {
                    // 需要 2FA
                    LogService.Info("[GSA] 需要双重认证");
                    return AuthResult.Requires2FA;
                }
                else
                {
                    var em = srpComplete.TryGetValue("em", out var msg) ? msg : "未知错误";
                    LogService.Error($"[GSA] 认证失败: ec={errorCode}, em={em}");
                    return AuthResult.Error($"Apple 认证失败: {em}");
                }
            }

            // 没有 ec 字段，检查其他成功标志
            if (srpComplete.ContainsKey("adsid"))
            {
                _adsId = srpComplete["adsid"];
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
        // 方案1: 启动 anisette-server
        try
        {
            var anisetteServer = Path.Combine(_toolsDir, "anisette-server.exe");
            if (File.Exists(anisetteServer))
            {
                LogService.Info($"[Anisette] 启动 anisette-server: {anisetteServer}");

                var psi = new ProcessStartInfo
                {
                    FileName = anisetteServer,
                    Arguments = "-p 6969",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _anisetteProcess = Process.Start(psi);
                if (_anisetteProcess != null)
                {
                    LogService.Info("[Anisette] 等待服务器启动 (5s)...");
                    await Task.Delay(5000);

                    // 检查进程是否还在运行
                    if (_anisetteProcess.HasExited)
                    {
                        LogService.Error($"[Anisette] anisette-server 已退出, exit code: {_anisetteProcess.ExitCode}");
                        var stderr = await _anisetteProcess.StandardError.ReadToEndAsync();
                        LogService.Error($"[Anisette] stderr: {stderr}");
                    }
                    else
                    {
                        LogService.Info("[Anisette] 请求 Anisette 数据...");
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                        try
                        {
                            var response = await http.GetStringAsync("http://localhost:6969/get_headers?udid=-2");
                            LogService.Info($"[Anisette] 原始响应: {response}");

                            var json = JsonDocument.Parse(response);
                            var root = json.RootElement;

                            var anisette = new AnisetteData
                            {
                                OneTimePassword = root.TryGetProperty("X-Apple-I-MD", out var md) ? md.GetString() ?? "" : "",
                                MachineId = root.TryGetProperty("X-Apple-I-MD-M", out var mdm) ? mdm.GetString() ?? "" : "",
                                RoutingInfo = root.TryGetProperty("X-Apple-I-MD-RINFO", out var rinfo) ? long.TryParse(rinfo.GetString(), out var ri) ? ri : 17106176 : 17106176,
                                LocalUserId = "-2",
                                DeviceUniqueId = Guid.NewGuid().ToString("N")[..40],
                                SerialNumber = "F2LX1234ABCD"
                            };

                            LogService.Info($"[Anisette] 成功获取: OTP长度={anisette.OneTimePassword.Length}, MachineId长度={anisette.MachineId.Length}");
                            return anisette;
                        }
                        catch (Exception ex)
                        {
                            LogService.Error($"[Anisette] HTTP 请求失败: {ex.Message}");
                        }

                        // 停止服务器
                        try { _anisetteProcess.Kill(); } catch { }
                    }
                }
                else
                {
                    LogService.Error("[Anisette] 无法启动 anisette-server 进程");
                }
            }
            else
            {
                LogService.Info("[Anisette] anisette-server.exe 不存在");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[Anisette] 异常: {ex.Message}");
        }

        // 方案2: 回退
        LogService.Info("[Anisette] 使用占位符数据 (认证可能失败)");
        return AppleSrp.GenerateAnisette();
    }

    // MARK: - SRP

    private async Task<Dictionary<string, string>?> SrpInitAsync(string appleId)
    {
        var url = "https://gsa.apple.com/grandslam/GsService2";
        LogService.Info($"[SRP] POST {url}");

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

        var plistStr = plist.ToString();
        LogService.Info($"[SRP] 请求体长度: {plistStr.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(plistStr, Encoding.UTF8, "text/x-xml-plist")
        };
        request.Headers.Add("User-Agent", "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0");

        LogService.Info("[SRP] 发送请求...");
        var response = await _http.SendAsync(request);
        LogService.Info($"[SRP] 响应: {response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        LogService.Info($"[SRP] 响应体长度: {body.Length}");

        if (!response.IsSuccessStatusCode)
        {
            LogService.Error($"[SRP] 失败: {response.StatusCode} - {body[..Math.Min(200, body.Length)]}");
            return null;
        }

        return ParsePlistDict(body);
    }

    private async Task<Dictionary<string, string>?> SrpCompleteAsync(
        Dictionary<string, string> init, string appleId, string password)
    {
        var url = "https://gsa.apple.com/grandslam/GsService2";

        // 解析 SRP 参数
        if (!init.TryGetValue("B", out var b64B) || string.IsNullOrEmpty(b64B))
        {
            LogService.Error("[SRP] 响应中缺少 B 参数");
            return null;
        }

        var B = Convert.FromBase64String(b64B);
        var salt = init.TryGetValue("salt", out var s) && !string.IsNullOrEmpty(s) ? Convert.FromBase64String(s) : new byte[32];
        var iterations = init.TryGetValue("i", out var iterStr) && int.TryParse(iterStr, out var iter) ? iter : 10000;

        LogService.Info($"[SRP] B长度={B.Length}, salt长度={salt.Length}, iterations={iterations}");

        // 计算
        var A = AppleSrp.GeneratePublicKey();
        var M1 = AppleSrp.ComputeProof(salt, password, A, B, [], appleId, iterations);

        LogService.Info("[SRP] 构建 Complete 请求...");

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

        LogService.Info("[SRP] 发送 Complete 请求...");
        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        LogService.Info($"[SRP] Complete 响应: {response.StatusCode}, 长度={body.Length}");

        if (!response.IsSuccessStatusCode)
        {
            LogService.Error($"[SRP] Complete 失败: {body[..Math.Min(500, body.Length)]}");
            return null;
        }

        return ParsePlistDict(body);
    }

    // MARK: - 辅助

    private List<XElement> BuildAnisetteDict()
    {
        if (_anisette == null)
        {
            LogService.Error("[Anisette] _anisette 为 null, 使用空值");
            return new List<XElement>();
        }

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
            LogService.Info($"[Plist] 解析响应: {plist[..Math.Min(200, plist.Length)]}...");

            var doc = XDocument.Parse(plist);
            var root = doc.Root;
            if (root == null)
            {
                LogService.Error("[Plist] 根元素为 null");
                return null;
            }

            var dict = root.Element("dict");
            if (dict == null)
            {
                LogService.Error("[Plist] dict 元素不存在");
                return null;
            }

            var result = new Dictionary<string, string>();
            var keys = dict.Elements("key").ToList();
            var values = dict.Elements().Where(e => e.Name.LocalName != "key").ToList();

            for (int i = 0; i < keys.Count && i < values.Count; i++)
            {
                result[keys[i].Value] = values[i].Value;
            }

            LogService.Info($"[Plist] 解析成功, {result.Count} 个字段");
            foreach (var kvp in result)
            {
                LogService.Info($"  {kvp.Key} = {kvp.Value[..Math.Min(50, kvp.Value.Length)]}...");
            }

            return result;
        }
        catch (Exception ex)
        {
            LogService.Error($"[Plist] 解析失败: {ex.Message}");
            return null;
        }
    }

    // MARK: - 证书管理 (框架)

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
        try { _anisetteProcess?.Kill(); } catch { }
    }
}
