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

        if (!string.IsNullOrEmpty(options.P12Path))
        {
            if (!File.Exists(options.P12Path)) throw new FileNotFoundException("找不到证书文件 (.p12)", options.P12Path);
            sb.Append($"-k \"{options.P12Path}\"");
            if (!string.IsNullOrEmpty(options.P12Password))
                sb.Append($" -p \"{options.P12Password}\"");
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

        var arguments = $" -o \"{outputIpa}\" {sb} \"{inputIpa}\"";

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

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 zsign.exe");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            process.WaitForExit();

            var outText = stdout.Result;
            var errText = stderr.Result;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"zsign 签名失败 (退出码 {process.ExitCode}):\n{errText}");
            }

            if (!File.Exists(outputIpa))
            {
                throw new InvalidOperationException("zsign 未生成输出文件");
            }

            // 附带 zsign 输出信息供UI显示
            LastSigningOutput = outText;
            return outputIpa;
        }, ct);
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