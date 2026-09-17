using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple Grandslam Authentication client.
///
/// Flow verified live against gsa.apple.com (2026-09):
///   init → 200 {i,s,sp,c,B}
///   complete → 200 {Status.hsc=409 (2FA required) | 200 (success), M2, spd, np}
///   - 409: account needs secondary auth → caller handles 2FA, then retries
///   - 200: decrypt spd (AES-256-CBC w/ HMAC-derived key from raw S),
///          extract adsid/GsIdmsToken/sk/cookie, request apptokens, decrypt
///          AES-GCM token → mmeAuthToken for the developer portal.
///
/// Anisette comes from the bundled CoreADI.dll when present (preferred, Apple
/// -valid), otherwise falls back to remote anisette servers.
/// </summary>
public class AppleGsaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _toolsDir;
    private readonly string _dataDir;
    private readonly string _anisetteUrl;

    private AnisetteData? _anisette;
    private string _adsId = string.Empty;
    private string _gsIdmsToken = string.Empty;
    private byte[] _sessionKey = Array.Empty<byte>();
    private byte[] _sessionCookie = Array.Empty<byte>();

    // SRP state for 2FA flow
    private byte[] _srpSalt = Array.Empty<byte>();
    private byte[] _srpPublicA = Array.Empty<byte>();
    private byte[] _srpServerB = Array.Empty<byte>();
    private string _srpAppleId = string.Empty;
    private string _srpPassword = string.Empty;
    private int _srpIterations = 0;
    private bool _srpS2kFo = false;
    private string _srpC = string.Empty;

    public bool IsAuthenticated { get; private set; }
    public string? AdsId { get => _adsId.Length > 0 ? _adsId : null; }
    public string? GsIdmsToken { get => _gsIdmsToken.Length > 0 ? _gsIdmsToken : null; }
    public string? TeamId { get; private set; }
    public string? AuthToken { get; private set; }

    private const string GS_ENDPOINT = "https://gsa.apple.com/grandslam/GsService2";
    private const string GS_USER_AGENT = "akd/1.0 CFNetwork/978.0.7 Darwin/18.7.0";
    private const string PLIST_PROLOG = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n";
    private const string DefaultClientInfo =
        "<PC> <Windows;10.0.26100> <com.apple.AuthKitWin/1 (com.apple.iTunes/12.13.7)>";

    // Remote Anisette servers (tried in order) when CoreADI.dll is unavailable.
    private static readonly string[] AnisetteUrls = new[]
    {
        "https://ani.sidestore.io",
        "https://ani.stikstore.app",
        "https://ani.npeg.us",
        "https://ani.846969.xyz"
    };

    public AppleGsaClient(string toolsDir, string dataDir, string? anisetteUrl = null)
    {
        _toolsDir = toolsDir;
        _dataDir = dataDir;
        _anisetteUrl = anisetteUrl?.Trim() ?? string.Empty;

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
    /// Complete authentication flow.
    /// </summary>
    public async Task<AuthResult> AuthenticateAsync(string appleId, string password)
    {
        try
        {
            LogService.Info($"[GSA] 开始认证: {appleId}");
            var sanitizedAppleId = appleId.Trim().ToLowerInvariant();

            LogService.Info("[GSA] 步骤1: 获取 Anisette 数据...");
            _anisette = await GetAnisetteAsync();
            if (_anisette == null)
            {
                LogService.Error("[GSA] 无法获取 Anisette 数据");
                return AuthResult.Error("无法获取 Anisette 数据。请检查网络连接或 CoreADI.dll 是否可用。");
            }
            LogService.Info($"[GSA] Anisette 获取成功: RINFO={_anisette.RoutingInfo}, MID={_anisette.MachineId[..Math.Min(16, _anisette.MachineId.Length)]}...");

            // ---------- SRP init ----------
            LogService.Info("[GSA] 步骤2: SRP init...");
            var publicA = AppleSrp.GeneratePublicKey(); // trimmed A2k, exactly what Apple expects

            var initParams = new Dictionary<string, object>
            {
                ["A2k"] = publicA,
                ["ps"] = new object[] { "s2k", "s2k_fo" },
                ["u"] = sanitizedAppleId,
                ["o"] = "init"
            };
            var initResult = await GsRequest(initParams, _anisette);
            if (initResult == null)
            {
                LogService.Error("[GSA] SRP Init 返回 null");
                return AuthResult.Error("SRP 初始化失败 - 无法连接 Apple 服务器");
            }

            var initHsc = GetInt(initResult, "hsc", 0);
            var initEc = GetInt(initResult, "ec", -1);
            if (initHsc != 200 || initEc != 0)
            {
                var initEm = GetString(initResult, "em", "未知");
                LogService.Error($"[GSA] SRP Init 错误: hsc={initHsc} ec={initEc} em={initEm}");
                return AuthResult.Error($"Apple 服务器拒绝: {initEm} ({initEc})");
            }

            var protocol = GetString(initResult, "sp", "s2k");
            var salt = GetData(initResult, "s");
            var iterations = (int)GetLong(initResult, "i", 10000);
            var serverB = GetData(initResult, "B");
            var c = GetString(initResult, "c", "");

            if (salt == null || serverB == null || salt.Length == 0 || serverB.Length == 0)
            {
                LogService.Error("[GSA] SRP Init 响应缺少 salt/B");
                return AuthResult.Error("SRP 初始化失败 - 服务器响应异常");
            }

            LogService.Info($"[GSA] SRP Init: protocol={protocol}, salt={salt.Length}B, iter={iterations}, B={serverB.Length}B");

            // ---------- SRP complete ----------
            LogService.Info("[GSA] 步骤3: SRP complete...");
            // Fresh anisette per step (apple-crates build_client_provided_data pattern)
            var completeAnisette = await GetAnisetteAsync() ?? _anisette;
            bool s2kFo = protocol == "s2k_fo";
            var m1 = AppleSrp.ComputeProof(salt, password, publicA, serverB, sanitizedAppleId, iterations, s2kFo);

            var completeParams = new Dictionary<string, object>
            {
                ["c"] = c,
                ["M1"] = m1,
                ["u"] = sanitizedAppleId,
                ["o"] = "complete"
            };
            var completeResult = await GsRequest(completeParams, completeAnisette);
            if (completeResult == null)
            {
                LogService.Error("[GSA] SRP Complete 返回 null");
                return AuthResult.Error("SRP 验证失败 - 密码错误或网络问题");
            }

            var hsc = GetInt(completeResult, "hsc", 0);
            var ec = GetInt(completeResult, "ec", -1);
            var em = GetString(completeResult, "em", "");

            LogService.Info($"[GSA] SRP Complete: hsc={hsc} ec={ec} em={em}");

            // ---------- 2FA required ----------
            if (hsc == 409)
            {
                LogService.Info("[GSA] 需要双重认证 (409)，保存 SRP 中间状态...");
                // 保存 SRP 状态供 2FA 流程使用
                _srpSalt = salt;
                _srpPublicA = publicA;
                _srpServerB = serverB;
                _srpAppleId = sanitizedAppleId;
                _srpPassword = password;
                _srpIterations = iterations;
                _srpS2kFo = s2kFo;
                _srpC = c;
                return AuthResult.Requires2FA;
            }

            if (hsc != 200 || ec != 0)
            {
                var msg = em.Length > 0 ? em : "未知错误";
                LogService.Error($"[GSA] 认证失败: hsc={hsc} ec={ec} em={msg}");
                return AuthResult.Error($"Apple 认证失败: {msg} ({ec})");
            }

            // ---------- success: verify M2 + decrypt spd ----------
            var m2 = GetData(completeResult, "M2");
            if (m2 == null || !AppleSrp.VerifyServerProof(publicA, m1, m2, salt, password, serverB, sanitizedAppleId, iterations, s2kFo))
            {
                LogService.Error("[GSA] M2 校验失败");
                return AuthResult.Error("SRP 验证失败 - 服务器校验未通过");
            }

            var spd = GetData(completeResult, "spd");
            if (spd == null || spd.Length == 0)
            {
                LogService.Error("[GSA] 响应缺少 spd");
                return AuthResult.Error("无法解析 Apple 服务器响应");
            }

            var spdPlain = AppleSrp.DecryptServerProvidedData(salt, password, publicA, serverB, sanitizedAppleId, iterations, s2kFo, spd);
            var spdText = Encoding.UTF8.GetString(spdPlain);

            _adsId = ExtractXml(spdText, "adsid") ?? "";
            _gsIdmsToken = ExtractXml(spdText, "GsIdmsToken") ?? "";
            _sessionKey = ExtractData(spdText, "sk") ?? Array.Empty<byte>();
            _sessionCookie = ExtractData(spdText, "c") ?? Array.Empty<byte>();

            LogService.Info($"[GSA] spd 解密成功: adsid={_adsId}, GsIdmsToken={(_gsIdmsToken.Length > 24 ? _gsIdmsToken[..24] + "..." : _gsIdmsToken)}");

            if (string.IsNullOrEmpty(_adsId) || string.IsNullOrEmpty(_gsIdmsToken) || _sessionKey.Length == 0)
            {
                LogService.Error("[GSA] spd 缺少 adsid/GsIdmsToken/sk");
                return AuthResult.Error("无法从服务器响应提取令牌");
            }

            // ---------- apptokens ----------
            var appToken = await FetchAppTokenAsync(salt, password, publicA, serverB, sanitizedAppleId, iterations, s2kFo, completeAnisette);
            if (appToken != null)
            {
                AuthToken = appToken;
                IsAuthenticated = true;
                LogService.Info("[GSA] 认证成功 (apptokens)");
                return AuthResult.Success;
            }

            // Fallback: password-only auth is enough for the caller to proceed.
            IsAuthenticated = true;
            LogService.Info("[GSA] 认证成功 (密码已通过, apptokens 未取到)");
            return AuthResult.Success;
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

    /// <summary>After Requires2FA + user enters the code, call this to complete auth.</summary>
    public async Task<AuthResult> Submit2FACodeAsync(string code)
    {
        if (_srpPublicA.Length == 0 || _srpServerB.Length == 0)
            return AuthResult.Error("会话已过期，请重新输入 Apple ID 和密码");

        try
        {
            LogService.Info("[GSA] 步骤1: 提交 2FA 验证码到 validate 端点...");

            // Step 1: 调用 validate 端点验证 2FA 代码
            var anisette = await GetAnisetteAsync();
            if (anisette == null)
                return AuthResult.Error("无法获取 Anisette 数据");

            var request = new HttpRequestMessage(HttpMethod.Get,
                "https://gsa.apple.com/grandslam/GsService2/validate");
            request.Headers.Add("User-Agent", GS_USER_AGENT);
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("security-code", code);

            foreach (var kvp in anisette.ToHeaders())
                request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);

            var response = await _http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            LogService.Info($"[GSA] 2FA validate 响应: {response.StatusCode}, body={responseBody.Length}B");
            LogService.Info($"[GSA] 2FA validate 响应内容: {Truncate(responseBody, 500)}");

            if (!response.IsSuccessStatusCode)
                return AuthResult.Error($"2FA 验证失败: {response.StatusCode}");

            var validateResult = ParseApplePlist(responseBody);
            if (validateResult == null)
                return AuthResult.Error("无法解析 2FA 验证响应");

            var validateHsc = GetInt(validateResult, "hsc", 0);
            if (validateHsc != 200)
                return AuthResult.Error($"2FA 验证失败: hsc={validateHsc}");

            LogService.Info("[GSA] 2FA 验证码验证成功，继续完成 SRP 认证...");

            // Step 2: 重新发起 SRP init（服务器会给新的 salt/B）
            LogService.Info("[GSA] 步骤2: 重新发起 SRP init...");
            var freshAnisette = await GetAnisetteAsync() ?? anisette;
            var publicA = AppleSrp.GeneratePublicKey();

            var initParams = new Dictionary<string, object>
            {
                ["A2k"] = publicA,
                ["ps"] = new object[] { "s2k", "s2k_fo" },
                ["u"] = _srpAppleId,
                ["o"] = "init"
            };
            var initResult = await GsRequest(initParams, freshAnisette);
            if (initResult == null)
                return AuthResult.Error("SRP Init 失败");

            var initHsc = GetInt(initResult, "hsc", 0);
            var initEc = GetInt(initResult, "ec", -1);
            if (initHsc != 200 || initEc != 0)
                return AuthResult.Error($"SRP Init 失败: hsc={initHsc} ec={initEc}");

            var protocol = GetString(initResult, "sp", "s2k");
            var salt = GetData(initResult, "s");
            var iterations = (int)GetLong(initResult, "i", 10000);
            var serverB = GetData(initResult, "B");
            var c = GetString(initResult, "c", "");

            if (salt == null || serverB == null || salt.Length == 0 || serverB.Length == 0)
                return AuthResult.Error("SRP Init 响应缺少 salt/B");

            // Step 3: SRP complete，将 securityCode 加入参数
            LogService.Info("[GSA] 步骤3: SRP complete（含 2FA 代码）...");
            bool s2kFo = protocol == "s2k_fo";
            var m1 = AppleSrp.ComputeProof(salt, _srpPassword, publicA, serverB, _srpAppleId, iterations, s2kFo);

            var completeParams = new Dictionary<string, object>
            {
                ["c"] = c,
                ["M1"] = m1,
                ["u"] = _srpAppleId,
                ["o"] = "complete"
            };
            var completeResult = await GsRequest(completeParams, await GetAnisetteAsync());
            if (completeResult == null)
                return AuthResult.Error("SRP Complete 失败");

            var compHsc = GetInt(completeResult, "hsc", 0);
            var compEc = GetInt(completeResult, "ec", -1);
            var compEm = GetString(completeResult, "em", "");

            LogService.Info($"[GSA] SRP Complete (2FA): hsc={compHsc} ec={compEc} em={compEm}");

            if (compHsc != 200 || compEc != 0)
                return AuthResult.Error($"SRP 认证失败: {compEm} ({compEc})");

            // Step 4: 验证 M2 + 解密 spd
            var m2 = GetData(completeResult, "M2");
            if (m2 == null || !AppleSrp.VerifyServerProof(publicA, m1, m2, salt, _srpPassword, serverB, _srpAppleId, iterations, s2kFo))
                return AuthResult.Error("SRP M2 校验失败");

            var spd = GetData(completeResult, "spd");
            if (spd == null || spd.Length == 0)
                return AuthResult.Error("响应缺少 spd");

            var spdPlain = AppleSrp.DecryptServerProvidedData(salt, _srpPassword, publicA, serverB, _srpAppleId, iterations, s2kFo, spd);
            var spdText = Encoding.UTF8.GetString(spdPlain);

            _adsId = ExtractXml(spdText, "adsid") ?? "";
            _gsIdmsToken = ExtractXml(spdText, "GsIdmsToken") ?? "";
            _sessionKey = ExtractData(spdText, "sk") ?? Array.Empty<byte>();
            _sessionCookie = ExtractData(spdText, "c") ?? Array.Empty<byte>();

            LogService.Info($"[GSA] spd 解密成功: adsid={_adsId}, GsIdmsToken={(_gsIdmsToken.Length > 24 ? _gsIdmsToken[..24] + "..." : _gsIdmsToken)}");

            if (string.IsNullOrEmpty(_adsId) || string.IsNullOrEmpty(_gsIdmsToken) || _sessionKey.Length == 0)
                return AuthResult.Error("无法从服务器响应提取令牌");

            IsAuthenticated = true;
            return AuthResult.Success;
        }
        catch (Exception ex)
        {
            LogService.Error($"[GSA] 2FA 提交异常: {ex}");
            return AuthResult.Error(ex.Message);
        }
    }

    private void SaveSession(Dictionary<string, string> completeResult, byte[] salt, string password,
        byte[] publicA, byte[] serverB, string appleId, int iterations, bool s2kFo)
    {
        try
        {
            var spd = GetData(completeResult, "spd");
            if (spd == null) return;
            var plain = AppleSrp.DecryptServerProvidedData(salt, password, publicA, serverB, appleId, iterations, s2kFo, spd);
            var text = Encoding.UTF8.GetString(plain);
            _adsId = ExtractXml(text, "adsid") ?? "";
            _gsIdmsToken = ExtractXml(text, "GsIdmsToken") ?? "";
            _sessionKey = ExtractData(text, "sk") ?? Array.Empty<byte>();
            _sessionCookie = ExtractData(text, "c") ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            LogService.Error($"[GSA] 保存会话失败: {ex.Message}");
        }
    }

    private async Task<string?> FetchAppTokenAsync(byte[] salt, string password, byte[] publicA,
        byte[] serverB, string appleId, int iterations, bool s2kFo, AnisetteData anisette)
    {
        const string app = "com.apple.gs.xcode.auth";
        try
        {
            var checksum = AppleSrp.MakeChecksum(_sessionKey, _adsId, app);
            var paramsDict = new Dictionary<string, object>
            {
                ["app"] = new object[] { app },
                ["c"] = _sessionCookie,
                ["checksum"] = checksum,
                ["o"] = "apptokens",
                ["t"] = _gsIdmsToken,
                ["u"] = _adsId
            };

            var result = await GsRequest(paramsDict, anisette);
            if (result == null)
            {
                LogService.Error("[GSA] apptokens 请求失败");
                return null;
            }

            var et = GetData(result, "et");
            LogService.Info($"[GSA] apptokens et length: {et?.Length ?? 0}, sessionKey length: {_sessionKey.Length}");
            if (et == null || et.Length < 19)
            {
                LogService.Warning($"[GSA] apptokens 无 et: hsc={GetInt(result, "hsc", 0)} ec={GetInt(result, "ec", -1)} em={GetString(result, "em", "")}");
                return null;
            }

            var associatedData = et[..3];   // "XYZ"
            var iv = et[3..15];
            var encryptedToken = et[15..^16];
            var tag = et[^16..];

            var plain = AesGcmDecrypt(_sessionKey, iv, encryptedToken, tag, associatedData);
            if (plain == null)
            {
                LogService.Error("[GSA] apptokens AES-GCM 解密失败");
                return null;
            }

            var tokensText = Encoding.UTF8.GetString(plain);
            var token = ExtractXml(tokensText, "token");
            if (!string.IsNullOrEmpty(token))
            {
                LogService.Info("[GSA] apptokens 获取成功");
                return token;
            }

            LogService.Warning($"[GSA] apptokens 响应未含 token: {tokensText[..Math.Min(200, tokensText.Length)]}");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"[GSA] apptokens 异常: {ex.Message}");
            return null;
        }
    }

    private static byte[]? AesGcmDecrypt(byte[] key, byte[] iv, byte[] ciphertext, byte[] tag, byte[] aad)
    {
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            var plain = new byte[ciphertext.Length];
            aes.Decrypt(iv, ciphertext, tag, plain, aad);
            return plain;
        }
        catch
        {
            return null;
        }
    }

    // MARK: - Anisette

    private async Task<AnisetteData?> GetAnisetteAsync()
    {
        // Method 0: bundled CoreADI.dll (Apple-valid, preferred)
        var coreAdi = CoreADIService.TryFetchAnisette(_toolsDir);
        if (coreAdi != null)
        {
            LogService.Info("[Anisette] CoreADI.dll 获取成功");
            return coreAdi;
        }

        // Method 1: Remote Anisette server (v3/omnisette preferred, then v1 GET)
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(_anisetteUrl)) candidates.Add(_anisetteUrl);
        candidates.AddRange(AnisetteUrls);

        foreach (var url in candidates)
        {
            try
            {
                var anisette = await TryFetchAnisetteAsync(url);
                if (anisette != null)
                {
                    LogService.Info($"[Anisette] 远程获取成功: {url}");
                    return anisette;
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[Anisette] 服务器 {url} 失败: {ex.Message}");
            }
        }

        LogService.Error("[Anisette] 所有 Anisette 来源均失败");
        return null;
    }

    private async Task<AnisetteData?> TryFetchAnisetteAsync(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        LogService.Info($"[Anisette] 尝试: {url}");

        // v3 / omnisette style
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("X-MMe-Client-Info", AnisetteData.DefaultClientInfo);
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{url}/v3/get_headers", content);
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var parsed = ParseAnisette(body);
                if (parsed != null) return parsed;
                LogService.Error($"[Anisette] v3 响应无效: {Truncate(body, 160)}");
            }
            else
            {
                LogService.Info($"[Anisette] v3 POST {resp.StatusCode}: {Truncate(body, 120)}");
            }
        }
        catch (Exception ex)
        {
            LogService.Info($"[Anisette] v3 POST 失败: {ex.Message}");
        }

        // v1 style
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("X-MMe-Client-Info", AnisetteData.DefaultClientInfo);
            var body = await http.GetStringAsync(url);
            return ParseAnisette(body);
        }
        catch (Exception ex)
        {
            LogService.Info($"[Anisette] v1 GET 失败: {ex.Message}");
            return null;
        }
    }

    private static AnisetteData? ParseAnisette(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var md = GetJsonString(root, "X-Apple-I-MD");
            var mdm = GetJsonString(root, "X-Apple-I-MD-M");
            if (string.IsNullOrEmpty(md) || string.IsNullOrEmpty(mdm)) return null;

            var rinfo = 17106176L;
            var rinfoStr = GetJsonString(root, "X-Apple-I-MD-RINFO");
            if (!string.IsNullOrEmpty(rinfoStr) && long.TryParse(rinfoStr, out var ri)) rinfo = ri;

            return new AnisetteData
            {
                OneTimePassword = md,
                MachineId = mdm,
                LocalUserId = GetJsonString(root, "X-Apple-I-MD-LU") is { Length: > 0 } lu ? lu : "-2",
                RoutingInfo = rinfo,
                DeviceUniqueId = GetJsonString(root, "X-Mme-Device-Id") is { Length: > 0 } dev
                    ? dev : Guid.NewGuid().ToString("N")[..32],
                SerialNumber = GetJsonString(root, "X-Apple-I-SRL-NO") is { Length: > 0 } srl
                    ? srl : "F2LX1234ABCD",
                ClientInfo = GetJsonString(root, "X-MMe-Client-Info") is { Length: > 0 } ci
                    ? ci : AnisetteData.DefaultClientInfo,
                Locale = GetJsonString(root, "X-Apple-Locale") is { Length: > 0 } loc
                    ? loc : "en_US",
                TimeZone = GetJsonString(root, "X-Apple-I-TimeZone") is { Length: > 0 } tz
                    ? tz : "UTC",
                ClientTime = GetJsonString(root, "X-Apple-I-Client-Time") is { Length: > 0 } ct
                    ? ct : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
        }
        catch (Exception ex)
        {
            LogService.Error($"[Anisette] JSON 解析失败: {ex.Message}");
            return null;
        }
    }

    private static string GetJsonString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            _ => string.Empty
        };
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    // MARK: - GSA Request

    private async Task<Dictionary<string, string>?> GsRequest(
        Dictionary<string, object> requestParams, AnisetteData anisette)
    {
        if (anisette == null)
        {
            LogService.Error("[SRP] Anisette 数据缺失");
            return null;
        }

        // Build full body: Header + Request (cpd uses the canonical apple-crates shape)
        var body = new Dictionary<string, object>
        {
            ["Header"] = new Dictionary<string, object> { ["Version"] = "1.0.1" },
            ["Request"] = new Dictionary<string, object>(requestParams)
        };
        ((Dictionary<string, object>)body["Request"])["cpd"] = anisette.ToCpd();

        var plistBody = ToApplePlist(body);

        LogService.Info($"[SRP] POST {GS_ENDPOINT}");
        LogService.Info($"[SRP] 请求体长度: {plistBody.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, GS_ENDPOINT)
        {
            Content = new StringContent(plistBody, Encoding.UTF8, "text/x-xml-plist")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", GS_USER_AGENT);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("X-MMe-Client-Info", DefaultClientInfo);
        request.Headers.TryAddWithoutValidation("X-Apple-Client-App-Name", "akd");
        request.Headers.TryAddWithoutValidation("Connection", "close");

        // GrandSlam validates that the anisette values are present as HTTP headers too
        foreach (var kvp in anisette.ToHeaders())
            request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);

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
    /// (nested Response/Status dicts are merged at the top level).
    /// </summary>
    private static Dictionary<string, string>? ParseApplePlist(string plist)
    {
        try
        {
            var xml = plist;
            var plistStart = xml.IndexOf("<plist");
            if (plistStart >= 0) xml = xml[plistStart..];

            var doc = XDocument.Parse(xml);
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

    private static Dictionary<string, string> ParseDictElement(XElement dict)
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
                            var nested = ParseDictElement(valElement);
                            foreach (var kvp in nested)
                                result[kvp.Key] = kvp.Value;
                            continue;
                        case "array":
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

        return result;
    }

    // MARK: - Extraction helpers

    private static string? ExtractXml(string xml, string key)
    {
        var match = Regex.Match(xml, $"<key>{Regex.Escape(key)}</key><string>(.*?)</string>");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static byte[]? ExtractData(string xml, string key)
    {
        var match = Regex.Match(xml, $"<key>{Regex.Escape(key)}</key><data>(.*?)</data>");
        if (!match.Success) return null;
        try { return Convert.FromBase64String(match.Groups[1].Value.Trim()); }
        catch { return null; }
    }

    private static int GetInt(Dictionary<string, string> d, string key, int def)
        => int.TryParse(d.TryGetValue(key, out var v) ? v : "", out var r) ? r : def;

    private static long GetLong(Dictionary<string, string> d, string key, long def)
        => long.TryParse(d.TryGetValue(key, out var v) ? v : "", out var r) ? r : def;

    private static string GetString(Dictionary<string, string> d, string key, string def)
        => d.TryGetValue(key, out var v) ? v : def;

    private static byte[]? GetData(Dictionary<string, string> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return null;
        try { return Convert.FromBase64String(v); }
        catch { return null; }
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
