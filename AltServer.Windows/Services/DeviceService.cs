using System.Diagnostics;
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

    /// <summary>安装IPA应用</summary>
    public void InstallIpa(string udid, string ipaPath)
    {
        RunTool("ideviceinstaller", $"-u {udid} -i \"{ipaPath}\"", timeoutMs: 300_000);
    }

    /// <summary>卸载应用</summary>
    public void UninstallApp(string udid, string bundleId)
    {
        RunTool("ideviceinstaller", $"-u {udid} -U {bundleId}", timeoutMs: 120_000);
    }

    /// <summary>列出设备上已安装的应用</summary>
    public List<InstalledApp> ListInstalledApps(string udid)
    {
        var apps = new List<InstalledApp>();

        var output = RunTool("ideviceinstaller", $"-u {udid} -l", timeoutMs: 30_000);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
                if (apps.Count > 0)
                {
                    apps[^1].Build = line.Split(':', 2)[1].Trim();
                }
            }
            else if (line.StartsWith("CFBundleDisplayName:", StringComparison.OrdinalIgnoreCase))
            {
                if (apps.Count > 0)
                {
                    apps[^1].Name = line.Split(':', 2)[1].Trim();
                }
            }
            else if (line.StartsWith("CFBundleVersionShortString:", StringComparison.OrdinalIgnoreCase))
            {
                if (apps.Count > 0)
                {
                    apps[^1].Version = line.Split(':', 2)[1].Trim();
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
}