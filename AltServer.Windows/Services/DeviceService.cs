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

    /// <summary>列出所有连接到本机的iOS设备，自动区分 USB 和 Wi-Fi 连接</summary>
    public List<Device> ListDevices()
    {
        var devices = new List<Device>();

        var udids = RunTool("idevice_id", "-l", timeoutMs: 15_000)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 检测通过 Wi-Fi 连接的设备（idevice_id -n 仅列出网络设备）
        var wifiUdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var wifiOutput = RunTool("idevice_id", "-n", timeoutMs: 8_000);
            foreach (var line in wifiOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                wifiUdids.Add(line);
        }
        catch { /* 旧版工具不支持 -n，忽略 */ }

        foreach (var udid in udids)
        {
            var device = new Device
            {
                Udid = udid,
                Connection = wifiUdids.Contains(udid) ? ConnectionType.WiFi : ConnectionType.Usb
            };

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
                // 如果常规查询失败（如尚未配对），尝试无需配对的简单模式(-s)读取型号与系统版本
                try
                {
                    var product = RunToolNoThrow("ideviceinfo", $"-u {udid} -s -k ProductType", timeoutMs: 5_000).Trim();
                    if (!string.IsNullOrWhiteSpace(product)) device.ProductType = product;

                    var os = RunToolNoThrow("ideviceinfo", $"-u {udid} -s -k ProductVersion", timeoutMs: 5_000).Trim();
                    if (!string.IsNullOrWhiteSpace(os)) device.OsVersion = os;
                }
                catch { }
            }

            // 无论如何保证有一个非空的识别名称
            if (string.IsNullOrWhiteSpace(device.Name))
            {
                device.Name = device.DisplayName;
            }

            devices.Add(device);
        }

        return devices;
    }

    /// <summary>配对成功后刷新并补全设备详细信息（设备名、型号、系统版本）</summary>
    public void RefreshDeviceInfo(Device device)
    {
        try
        {
            var name = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k DeviceName", timeoutMs: 8_000).Trim();
            if (!string.IsNullOrWhiteSpace(name)) device.Name = name;

            var model = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k ModelNumber", timeoutMs: 8_000).Trim();
            if (!string.IsNullOrWhiteSpace(model)) device.Model = model;

            var product = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k ProductType", timeoutMs: 8_000).Trim();
            if (!string.IsNullOrWhiteSpace(product)) device.ProductType = product;

            var os = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k ProductVersion", timeoutMs: 8_000).Trim();
            if (!string.IsNullOrWhiteSpace(os)) device.OsVersion = os;

            device.IsPaired = IsPaired(device.Udid);
        }
        catch { }
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

    /// <summary>安装IPA应用，具备 lockdownd 连接重试与自动恢复机制</summary>
    public void InstallIpa(string udid, string ipaPath)
    {
        try
        {
            // 直接尝试安装（不提前破坏已有配对会话）
            RunTool("ideviceinstaller", $"-u {udid} -i \"{ipaPath}\"", timeoutMs: 300_000);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("lockdownd", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("Could not connect", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warning($"[Install] 首次连接 lockdownd 遇到波动，尝试自动重新配对并重试...");

            // 自动恢复：执行一次重新配对握手
            try
            {
                var pairOut = RunToolNoThrow("idevicepair", $"-u {udid} pair", timeoutMs: 15_000);
                LogService.Info($"[Install] 重新配对: {pairOut.Trim().Split('\n')[0]}");
            }
            catch { }

            // 关键：配对握手后必须休眠 2.5 秒，给设备端 lockdownd 守护进程重启和重载证书的时间
            System.Threading.Thread.Sleep(2500);

            // 再次尝试安装
            try
            {
                RunTool("ideviceinstaller", $"-u {udid} -i \"{ipaPath}\"", timeoutMs: 300_000);
                return;
            }
            catch (Exception retryEx)
            {
                throw new InvalidOperationException(
                    "无法连接到设备守护进程（lockdownd）。\n" +
                    "排查建议：\n" +
                    "  1. 请检查设备屏幕已点亮并在主屏幕（必须解锁且不能锁屏）\n" +
                    "  2. 设备若弹出「信任此电脑」，请点击「信任」并【必须在设备上输入锁屏密码】\n" +
                    "  3. 请彻底退出可能占用设备通信的后台软件（如 iTunes、爱思助手、3uTools）\n" +
                    "  4. 尝试拔掉 USB 数据线，等待 3 秒后重新插上电脑\n" +
                    $"（原始错误: {retryEx.Message}）", retryEx);
            }
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