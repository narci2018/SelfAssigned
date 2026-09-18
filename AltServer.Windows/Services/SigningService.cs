using System.Diagnostics;
using System.IO;
using System.Text;

namespace AltServer.Windows.Services;

/// <summary>
/// IPA签名服务：封装 zsign 工具
/// 依赖：zsign.exe
/// 签名流程：使用 .p12 私钥证书 + .mobileprovision 配置文件重新签名IPA
/// </summary>
public class SigningService
{
    private readonly string _zsignPath;

    public SigningService(string toolsDir)
    {
        _zsignPath = Path.Combine(toolsDir, "zsign.exe");
    }

    public class SigningOptions
    {
        public string P12Path { get; set; } = string.Empty;
        public string P12Password { get; set; } = string.Empty;
        public string MobileProvisionPath { get; set; } = string.Empty;
        public string BundleId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool RemoveQuartzDebug { get; set; }
        public string EntitlementsPath { get; set; } = string.Empty;
    }

    /// <summary>签名IPA文件</summary>
    /// <returns>签名后的IPA路径</returns>
    public async Task<string> SignIpaAsync(string inputIpa, string outputIpa, SigningOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(_zsignPath))
        {
            throw new FileNotFoundException($"未找到 zsign.exe。请下载 zsign 放入 {Path.GetDirectoryName(_zsignPath)}", _zsignPath);
        }

        var outputDir = Path.GetDirectoryName(outputIpa)!;
        Directory.CreateDirectory(outputDir);

        var sb = new StringBuilder();

        // 自动检测并匹配描述文件中的 Bundle ID（如果未显式指定且非通配符）
        if (string.IsNullOrEmpty(options.BundleId) && !string.IsNullOrEmpty(options.MobileProvisionPath))
        {
            var detectedBundleId = ParseProvisionBundleId(options.MobileProvisionPath);
            if (!string.IsNullOrEmpty(detectedBundleId))
            {
                options.BundleId = detectedBundleId;
                LogService.Info($"[Sign] 检测到描述文件绑定 Bundle ID: {detectedBundleId}，自动应用 -b 参数");
            }
        }

        if (!string.IsNullOrEmpty(options.P12Path))
        {
            if (!File.Exists(options.P12Path)) throw new FileNotFoundException("找不到证书文件 (.p12)", options.P12Path);
            
            // 自动解析 P12 密码（尝试 hint 密码、temp123、空密码等）
            var resolvedPassword = ResolveP12Password(options.P12Path, options.P12Password);
            sb.Append($"-k \"{options.P12Path}\"");
            if (!string.IsNullOrEmpty(resolvedPassword))
                sb.Append($" -p \"{resolvedPassword}\"");
        }
        else
        {
            throw new InvalidOperationException("需要提供 .p12 证书文件");
        }

        if (!string.IsNullOrEmpty(options.MobileProvisionPath))
        {
            if (!File.Exists(options.MobileProvisionPath)) throw new FileNotFoundException("找不到配置文件 (.mobileprovision)", options.MobileProvisionPath);
            sb.Append($" -m \"{options.MobileProvisionPath}\"");
        }

        if (!string.IsNullOrEmpty(options.BundleId))
            sb.Append($" -b {options.BundleId}");

        if (!string.IsNullOrEmpty(options.DisplayName))
            sb.Append($" -n \"{options.DisplayName}\"");

        if (options.RemoveQuartzDebug)
            sb.Append(" --remove-quartz-debug");

        if (!string.IsNullOrEmpty(options.EntitlementsPath) && File.Exists(options.EntitlementsPath))
            sb.Append($" -e \"{options.EntitlementsPath}\"");

        // 默认快速压缩级别 (1)
        sb.Append(" -z 1");

        var arguments = $" -o \"{outputIpa}\" {sb} \"{inputIpa}\"";
        var maskedArgs = System.Text.RegularExpressions.Regex.Replace(arguments, @"-p\s+""[^""]+""", "-p \"******\"");
        LogService.Info($"[Sign] 执行: zsign {maskedArgs.Trim()}");

        return await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = _zsignPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            var outputLines = new List<string>();
            var errorLines = new List<string>();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    outputLines.Add(e.Data);
                    var line = e.Data.Trim();
                    if (line.StartsWith(">>> Error") || line.StartsWith(">>> Can't") || line.Contains("error:"))
                        LogService.Error($"[zsign] {line}");
                    else if (line.StartsWith(">>> Warn"))
                        LogService.Warning($"[zsign] {line}");
                    else if (line.StartsWith(">>>"))
                        LogService.Info($"[zsign] {line}");
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    errorLines.Add(e.Data);
                    LogService.Error($"[zsign-err] {e.Data.Trim()}");
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 zsign.exe");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            var outText = string.Join("\n", outputLines);
            var errText = string.Join("\n", errorLines);
            var combinedOutput = string.IsNullOrWhiteSpace(errText)
                ? outText
                : $"{outText}\n{errText}";

            LastSigningOutput = combinedOutput;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"zsign 签名失败 (退出码 {process.ExitCode}):\n{combinedOutput.Trim()}");
            }

            if (!File.Exists(outputIpa))
            {
                throw new InvalidOperationException($"zsign 未生成输出文件: {outputIpa}");
            }

            return outputIpa;
        }, ct);
    }

    /// <summary>自动测试并解析 P12 文件的有效密码</summary>
    public static string ResolveP12Password(string p12Path, string? hintPassword = null)
    {
        if (!File.Exists(p12Path)) return hintPassword ?? "";

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(hintPassword)) candidates.Add(hintPassword);
        candidates.Add("temp123");
        candidates.Add("");
        candidates.Add("123456");

        foreach (var cand in candidates.Distinct())
        {
            try
            {
                using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    p12Path, cand, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
                if (cert.HasPrivateKey)
                {
                    return cand;
                }
            }
            catch { }
        }

        return hintPassword ?? "";
    }

    /// <summary>解析.mobileprovision文件中的application-identifier并提取Bundle ID</summary>
    public static string? ParseProvisionBundleId(string mobileProvisionPath)
    {
        if (!File.Exists(mobileProvisionPath)) return null;

        try
        {
            var bytes = File.ReadAllBytes(mobileProvisionPath);
            var text = Encoding.UTF8.GetString(bytes);

            const string marker = "<key>application-identifier</key>";
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;

            var strStart = text.IndexOf("<string>", idx + marker.Length, StringComparison.Ordinal);
            if (strStart < 0) return null;
            strStart += "<string>".Length;

            var strEnd = text.IndexOf("</string>", strStart, StringComparison.Ordinal);
            if (strEnd < 0) return null;

            var appId = text[strStart..strEnd].Trim();
            if (appId.EndsWith(".*"))
            {
                return null;
            }

            var dotIdx = appId.IndexOf('.');
            if (dotIdx >= 0 && dotIdx + 1 < appId.Length)
            {
                return appId[(dotIdx + 1)..];
            }

            return appId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析.mobileprovision文件中的过期时间</summary>
    public static DateTime? ParseProvisionExpiry(string mobileProvisionPath)
    {
        if (!File.Exists(mobileProvisionPath)) return null;

        try
        {
            var bytes = File.ReadAllBytes(mobileProvisionPath);

            // 二进制plist是XML格式嵌入的，提取<ExpirationDate>
            var text = Encoding.UTF8.GetString(bytes);
            const string marker = "<key>ExpirationDate</key><date>";
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;

            var start = idx + marker.Length;
            var end = text.IndexOf("</date>", start, StringComparison.Ordinal);
            if (end < 0) return null;

            var dateStr = text[start..end];
            if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var utcDate))
            {
                return utcDate.ToLocalTime();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public string? LastSigningOutput { get; private set; }
}