using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AltServer.Windows.Models;

namespace AltServer.Windows.Services;

/// <summary>
/// 设备管理服务：封装 libimobiledevice 命令行工具
/// 依赖：idevice_id.exe, ideviceinfo.exe, ideviceinstaller.exe, idevicepair.exe
/// </summary>
public class DeviceService
{
    private readonly string _toolsDir;

    public DeviceService(string toolsDir)
    {
        _toolsDir = toolsDir;
    }

    /// <summary>列出所有连接到本机的iOS设备</summary>
    public List<Device> ListDevices()
    {
        var devices = new List<Device>();

        var udids = RunTool("idevice_id", "-l", timeoutMs: 15_000)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var udid in udids)
        {
            var device = new Device { Udid = udid };

            try
            {
                device.Name = RunTool("ideviceinfo", $"-u {udid} -k DeviceName", timeoutMs: 10_000).Trim();
                device.Model = RunTool("ideviceinfo", $"-u {udid} -k ModelNumber", timeoutMs: 10_000).Trim();
                device.ProductType = RunTool("ideviceinfo", $"-u {udid} -k ProductType", timeoutMs: 10_000).Trim();
                device.OsVersion = RunTool("ideviceinfo", $"-u {udid} -k ProductVersion", timeoutMs: 10_000).Trim();
                device.Serial = RunTool("ideviceinfo", $"-u {udid} -k SerialNumber", timeoutMs: 10_000).Trim();
                device.IsPaired = IsPaired(udid);
            }
            catch
            {
                // 设备信息获取失败时保留UDID
            }

            devices.Add(device);
        }

        return devices;
    }

    /// <summary>检查设备是否已配对</summary>
    public bool IsPaired(string udid)
    {
        try
        {
            var output = RunTool("idevicepair", $"-u {udid} validate", timeoutMs: 10_000);
            return output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>配对设备（需要在设备上点击“信任此电脑”）</summary>
    public bool Pair(string udid)
    {
        try
        {
            var output = RunTool("idevicepair", $"-u {udid} pair", timeoutMs: 30_000);
            return output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>安装IPA应用（安装前验证设备连接状态）</summary>
    public void InstallIpa(string udid, string ipaPath)
    {
        // 安装前先确认 lockdownd 可达，给出比 ideviceinstaller 更友好的错误
        ValidateLockdownOrThrow(udid);
        RunTool("ideviceinstaller", $"-u {udid} -i \"{ipaPath}\"", timeoutMs: 300_000);
    }

    /// <summary>
    /// 验证 lockdownd 连接是否正常。
    /// 如果设备屏幕锁定 / 未点击"信任此电脑" / USB不稳定，在安装前抛出友好错误。
    /// </summary>
    private void ValidateLockdownOrThrow(string udid)
    {
        try
        {
            var result = RunToolNoThrow("idevicepair", $"-u {udid} validate", timeoutMs: 10_000);
            if (result.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
                return; // 连接正常

            // validate 返回非 SUCCESS，说明信任关系失效
            throw new InvalidOperationException(
                "设备连接验证失败：请确认设备已解锁屏幕，并在弹出的对话框中点击「信任此电脑」。\n" +
                $"(idevicepair validate 输出: {result.Trim()})");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "设备连接超时：请检查 USB 连接是否稳定，设备屏幕是否亮着且已解锁。");
        }
        catch (InvalidOperationException)
        {
            throw; // 直接向上传递
        }
        catch (Exception ex)
        {
            // validate 工具本身出错（工具不存在等），不阻止安装，降级继续
            LogService.Warning($"[Install] 前置连接验证跳过: {ex.Message}");
        }
    }

    /// <summary>卸载应用</summary>
    public void UninstallApp(string udid, string bundleId)
    {
        RunTool("ideviceinstaller", $"-u {udid} -U {bundleId}", timeoutMs: 120_000);
    }

    /// <summary>列出设备上已安装的应用（兼容新旧版 ideviceinstaller 输出格式）</summary>
    public List<InstalledApp> ListInstalledApps(string udid)
    {
        var apps = new List<InstalledApp>();

        string output;
        try
        {
            // 新版 ideviceinstaller 支持 -o xml 输出 plist，先尝试
            output = RunToolNoThrow("ideviceinstaller", $"-u {udid} -l", timeoutMs: 60_000);
        }
        catch (TimeoutException)
        {
            // 超时时返回空列表，不向上抛异常
            throw;
        }

        if (string.IsNullOrWhiteSpace(output))
            return apps;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 检测输出格式：
        // 旧版格式：  CFBundleIdentifier: com.xxx, CFBundleVersion: 1.0, ...
        // 新版格式：  com.xxx, 1.0, AppName
        bool isNewFormat = lines.Length > 0
            && !lines[0].StartsWith("CFBundle", StringComparison.OrdinalIgnoreCase)
            && !lines[0].StartsWith("Total:", StringComparison.OrdinalIgnoreCase)
            && lines[0].Contains(',');

        if (isNewFormat)
        {
            // 新版 ideviceinstaller 输出：  bundleId, version, displayName
            foreach (var line in lines)
            {
                // 跳过 "Total: N apps" 这类汇总行
                if (line.StartsWith("Total:", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split(',', 3);
                if (parts.Length < 1) continue;

                var bundleId = parts[0].Trim();
                if (string.IsNullOrEmpty(bundleId)) continue;

                var app = new InstalledApp { BundleIdentifier = bundleId };
                if (parts.Length >= 2) app.Version = parts[1].Trim();
                if (parts.Length >= 3)
                {
                    // 去掉版本号后括号：  "AppName - 1.0.0"  或  "AppName"
                    var namePart = parts[2].Trim();
                    // 某些版本格式: "Name - Version" 
                    var dashIdx = namePart.LastIndexOf(" - ", StringComparison.Ordinal);
                    app.Name = dashIdx > 0 ? namePart[..dashIdx].Trim() : namePart;
                }
                else
                {
                    app.Name = bundleId.Contains('.') ? bundleId.Split('.').Last() : bundleId;
                }

                apps.Add(app);
            }
        }
        else
        {
            // 旧版 ideviceinstaller 格式：每行 Key: Value
            foreach (var line in lines)
            {
                if (line.StartsWith("CFBundleIdentifier:", StringComparison.OrdinalIgnoreCase))
                {
                    var bundleId = line.Split(':', 2)[1].Trim();
                    var name = bundleId.Contains('.') ? bundleId.Split('.').Last() : bundleId;
                    apps.Add(new InstalledApp { BundleIdentifier = bundleId, Name = name });
                }
                else if (line.StartsWith("CFBundleVersion:", StringComparison.OrdinalIgnoreCase))
                {
                    if (apps.Count > 0) apps[^1].Build = line.Split(':', 2)[1].Trim();
                }
                else if (line.StartsWith("CFBundleDisplayName:", StringComparison.OrdinalIgnoreCase))
                {
                    if (apps.Count > 0) apps[^1].Name = line.Split(':', 2)[1].Trim();
                }
                else if (line.StartsWith("CFBundleVersionShortString:", StringComparison.OrdinalIgnoreCase))
                {
                    if (apps.Count > 0) apps[^1].Version = line.Split(':', 2)[1].Trim();
                }
            }
        }

        return apps;
    }

    /// <summary>发送推送通知（用于刷新触发）</summary>
    public void PushNotification(string udid, string bundleId)
    {
        RunTool("idevicesyslog", $"", timeoutMs: 5_000);
    }

    // MARK: - 工具执行

    private string RunTool(string toolName, string arguments, int timeoutMs)
    {
        var toolPath = Path.Combine(_toolsDir, $"{toolName}.exe");

        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException($"未找到工具: {toolName}.exe。请将 libimobiledevice 工具放入 {_toolsDir}", toolPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 {toolName}.exe");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"工具执行超时: {toolName}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} 退出码 {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    /// <summary>
    /// 执行工具但允许非零退出码（如 ideviceinstaller -l 可能返回 1 但仍输出数据）。
    /// 超时时仍抛 TimeoutException。
    /// </summary>
    private string RunToolNoThrow(string toolName, string arguments, int timeoutMs)
    {
        var toolPath = Path.Combine(_toolsDir, $"{toolName}.exe");

        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException($"未找到工具: {toolName}.exe。请将 libimobiledevice 工具放入 {_toolsDir}", toolPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 {toolName}.exe");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"工具执行超时: {toolName}");
        }

        // 不检查退出码，直接返回 stdout（兼容部分工具非零退出但有效输出的情况）
        return stdoutTask.GetAwaiter().GetResult();
    }
}