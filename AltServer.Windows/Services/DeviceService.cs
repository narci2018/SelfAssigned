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
                var name = RunTool("ideviceinfo", $"-u {udid} -k DeviceName", timeoutMs: 10_000).Trim();
                if (IsValidInfoValue(name)) device.Name = name;

                var model = RunTool("ideviceinfo", $"-u {udid} -k ModelNumber", timeoutMs: 10_000).Trim();
                if (IsValidInfoValue(model)) device.Model = model;

                var product = RunTool("ideviceinfo", $"-u {udid} -k ProductType", timeoutMs: 10_000).Trim();
                if (IsValidInfoValue(product)) device.ProductType = product;

                var os = RunTool("ideviceinfo", $"-u {udid} -k ProductVersion", timeoutMs: 10_000).Trim();
                if (IsValidInfoValue(os)) device.OsVersion = os;

                var serial = RunTool("ideviceinfo", $"-u {udid} -k SerialNumber", timeoutMs: 10_000).Trim();
                if (IsValidInfoValue(serial)) device.Serial = serial;

                device.IsPaired = IsPaired(udid);
            }
            catch
            {
                // 如果常规查询失败（如尚未配对），尝试无需配对的简单模式(-s)读取型号与系统版本
                try
                {
                    var product = RunToolNoThrow("ideviceinfo", $"-u {udid} -s -k ProductType", timeoutMs: 5_000).Trim();
                    if (IsValidInfoValue(product)) device.ProductType = product;

                    var os = RunToolNoThrow("ideviceinfo", $"-u {udid} -s -k ProductVersion", timeoutMs: 5_000).Trim();
                    if (IsValidInfoValue(os)) device.OsVersion = os;
                }
                catch { }
            }

            // 无论如何保证有一个非空的识别名称
            if (string.IsNullOrWhiteSpace(device.Name) || !IsValidInfoValue(device.Name))
            {
                device.Name = !string.IsNullOrWhiteSpace(device.ProductType) && IsValidInfoValue(device.ProductType)
                    ? device.ProductType
                    : device.Udid[..Math.Min(8, device.Udid.Length)];
            }

            devices.Add(device);
        }

        return devices;
    }

    /// <summary>检查输出是否为合法的设备属性值（过滤掉底层报错信息）</summary>
    private static bool IsValidInfoValue(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return false;
        if (val.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return false;
        if (val.Contains("Could not connect", StringComparison.OrdinalIgnoreCase)) return false;
        if (val.Contains("lockdown", StringComparison.OrdinalIgnoreCase)) return false;
        if (val.Contains("HostID", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>删除指定设备的本地配对记录文件，强制触发全新配对握手（解决 Invalid HostID -21）</summary>
    public static void DeletePairRecord(string udid)
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple", "Lockdown", $"{udid}.plist"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apple Computer", "Lockdown", $"{udid}.plist"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "libimobiledevice", $"{udid}.plist"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "libimobiledevice", $"{udid}.plist")
        };

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    LogService.Info($"[Pair] 已清理旧配对记录文件: {path}");
                }
            }
            catch (Exception ex)
            {
                LogService.Warning($"[Pair] 清理配对文件失败 {path}: {ex.Message}");
            }
        }
    }

    /// <summary>配对成功后刷新并补全设备详细信息（设备名、型号、系统版本）</summary>
    public void RefreshDeviceInfo(Device device)
    {
        try
        {
            var name = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k DeviceName", timeoutMs: 8_000).Trim();
            if (IsValidInfoValue(name)) device.Name = name;

            var model = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k ModelNumber", timeoutMs: 8_000).Trim();
            if (IsValidInfoValue(model)) device.Model = model;

            var product = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k ProductType", timeoutMs: 8_000).Trim();
            if (IsValidInfoValue(product)) device.ProductType = product;

            var os = RunToolNoThrow("ideviceinfo", $"-u {device.Udid} -k ProductVersion", timeoutMs: 8_000).Trim();
            if (IsValidInfoValue(os)) device.OsVersion = os;

            device.IsPaired = IsPaired(device.Udid);
        }
        catch { }
    }

    /// <summary>检查设备是否已配对且会话有效</summary>
    public bool IsPaired(string udid)
    {
        try
        {
            var output = RunToolNoThrow("idevicepair", $"-u {udid} validate", timeoutMs: 8_000);
            return output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>配对设备（清理失效记录，并在设备上点击“信任此电脑”）</summary>
    public bool Pair(string udid)
    {
        try
        {
            EnsureLockdownDirectoryAccess();

            // 1. 若当前会话已经完全通过 validate 检验，直接返回成功
            if (IsPaired(udid))
            {
                LogService.Info($"[Pair] 设备 {udid[..Math.Min(8, udid.Length)]} 配对会话有效");
                return true;
            }

            // 2. 当前配对无效或失效（如 Invalid HostID），彻底删除本地旧配对记录以迫使设备重新协商
            DeletePairRecord(udid);

            // 3. 循环发起配对，等待用户在设备上点击“信任”并输入密码（最多等待 30 秒）
            LogService.Info($"[Pair] 正在请求配对，请在设备屏幕上点击「信任此电脑」并输入锁屏密码...");

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000)
            {
                var output = RunToolNoThrow("idevicepair", $"-u {udid} pair", timeoutMs: 8_000);

                if (output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    // 配对握手成功后稍候 1 秒，使用 validate 再次确认 lockdownd session 是否通畅
                    System.Threading.Thread.Sleep(1000);
                    if (IsPaired(udid))
                    {
                        var recordPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple", "Lockdown", $"{udid}.plist");
                        var exists = File.Exists(recordPath);
                        LogService.Success($"[Pair] 设备配对成功！证书记录: {(exists ? recordPath : "已保存")}");
                        return true;
                    }
                }

                if (output.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warning("[Pair] 用户在设备上点击了「不信任」");
                    return false;
                }

                // 设备锁屏或等待用户点击“信任”，稍候 2 秒重试
                System.Threading.Thread.Sleep(2000);
            }

            return IsPaired(udid);
        }
        catch (Exception ex)
        {
            LogService.Warning($"配对异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>确保系统级配对目录存在且拥有读写访问权</summary>
    private static void EnsureLockdownDirectoryAccess()
    {
        try
        {
            var lockdownDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple", "Lockdown");
            if (!Directory.Exists(lockdownDir))
            {
                Directory.CreateDirectory(lockdownDir);
            }
        }
        catch { }
    }

    /// <summary>安装IPA应用，具备 lockdownd 连接重试与自动恢复机制</summary>
    public void InstallIpa(string udid, string ipaPath)
    {
        // 安装前：预热 lockdownd 连接——先用轻量级 ideviceinfo 查询唤醒守护进程
        // 旧设备（iPad Air 2 等）的 lockdownd 在签名期间可能进入休眠，需要先激活
        WarmUpLockdownConnection(udid);

        try
        {
            // 安装前若发现配对会话失效，先执行自动重配对
            if (!IsPaired(udid))
            {
                LogService.Warning($"[Install] 检测到设备配对状态失效，正在尝试自动重新握手...");
                Pair(udid);
                // 配对后必须等待 lockdownd 重新加载证书
                System.Threading.Thread.Sleep(3000);
                WarmUpLockdownConnection(udid);
            }

            // 直接尝试安装（优先带 -u 指定设备）
            RunTool("ideviceinstaller", $"-u {udid} -i \"{ipaPath}\"", timeoutMs: 300_000);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("lockdownd", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("Could not connect", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("HostID", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Warning($"[Install] 首次连接 lockdownd 遇到波动，尝试清理旧配对并重新握手...");

            // 自动恢复：先清理本地失效的旧配对文件，再重新 pair 生成合法密钥对
            try
            {
                DeletePairRecord(udid);
                RunToolNoThrow("idevicepair", $"-u {udid} unpair", timeoutMs: 5_000);
                System.Threading.Thread.Sleep(1000);

                var paired = Pair(udid);
                LogService.Info($"[Install] 重新配对: {(paired ? "SUCCESS" : "FAILED")}");

                if (!paired)
                {
                    throw new InvalidOperationException("重新配对失败，无法安装。请在设备上点击「信任此电脑」并输入密码。");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch { }

            // 关键：配对握手后必须休眠足够时间，给设备端 lockdownd 守护进程重启和重载证书的时间
            // 旧设备（iPad5,1 等）需要更长的等待时间
            System.Threading.Thread.Sleep(4000);

            // 预热：确认 lockdownd 已就绪后再安装
            if (!WarmUpLockdownConnection(udid))
            {
                // lockdownd 预热失败，再等 3 秒重试一次
                LogService.Warning("[Install] lockdownd 预热失败，等待后重试...");
                System.Threading.Thread.Sleep(3000);
                WarmUpLockdownConnection(udid);
            }

            // 重试安装（始终带 -u 精确指定目标设备，避免 usbmuxd 误选设备）
            try
            {
                LogService.Info("[Install] 尝试默认单设备通道安装...");
                RunTool("ideviceinstaller", $"-u {udid} -i \"{ipaPath}\"", timeoutMs: 300_000);
                return;
            }
            catch (Exception retryEx1)
            {
                // 最后一次尝试：不带 -u（兼容某些 ideviceinstaller 版本的 bug）
                try
                {
                    LogService.Info("[Install] 尝试不指定设备的兜底安装...");
                    System.Threading.Thread.Sleep(2000);
                    RunTool("ideviceinstaller", $"-i \"{ipaPath}\"", timeoutMs: 300_000);
                    return;
                }
                catch (Exception retryEx2)
                {
                    // 诊断：抓取 ideviceinfo -d 详细调试信息以供排查
                    var diag = RunToolNoThrow("ideviceinfo", $"-u {udid} -d", timeoutMs: 8_000);
                    var diagLines = diag.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var diagSummary = diagLines.Length > 0 ? string.Join(" | ", diagLines.Take(3)) : "无响应";

                    throw new InvalidOperationException(
                        "无法连接到设备守护进程（lockdownd）。\n" +
                        $"设备状态诊断: {diagSummary}\n" +
                        "排查建议：\n" +
                        "  1. 请检查设备屏幕已点亮并在主屏幕（必须解锁且不能锁屏）\n" +
                        "  2. 设备若弹出「信任此电脑」，请点击「信任」并【必须在设备上输入锁屏密码】\n" +
                        "  3. 请检查设备系统时间是否准确（时间偏差过大会导致 SSL 握手拒绝）\n" +
                        "  4. 请彻底退出可能占用设备通信的后台软件（如 iTunes、爱思助手、3uTools）\n" +
                        "  5. 尝试拔掉 USB 数据线，等待 3 秒后重新插上电脑\n" +
                        $"（原始错误: {retryEx2.Message}）", retryEx2);
                }
            }
        }
    }

    /// <summary>
    /// 预热 lockdownd 连接：使用轻量级 ideviceinfo 查询唤醒设备端守护进程。
    /// 旧设备在长时间未通信后 lockdownd 会话可能关闭，需要先发起一次查询激活。
    /// 返回 true 表示连接可用。
    /// </summary>
    private bool WarmUpLockdownConnection(string udid)
    {
        try
        {
            var result = RunToolNoThrow("ideviceinfo", $"-u {udid} -k ProductType", timeoutMs: 10_000).Trim();
            return IsValidInfoValue(result);
        }
        catch
        {
            return false;
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

    private ProcessStartInfo CreateToolStartInfo(string toolPath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _toolsDir
        };

        // 优先从工具目录搜索关联 DLL（imobiledevice/usbmuxd/plist/crypto/ssl）
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.EnvironmentVariables["PATH"] = $"{_toolsDir};{pathEnv}";

        // 降低 OpenSSL 安全检查级别：旧设备（如 iOS 15 的 iPad mini 4 / iPad Air 2）的 lockdownd
        // 证书可能采用旧式加密套件或短密钥，OpenSSL 3.x 会直接拒绝握手
        psi.EnvironmentVariables["OPENSSL_CIPHER_LIST"] = "ALL:@SECLEVEL=0";
        psi.EnvironmentVariables["OPENSSL_SECLEVEL"] = "0";

        // 禁用 OpenSSL 默认配置文件加载：某些 Windows 发行版的 openssl.cnf 会强制加载 provider
        // 或设置安全策略，导致与旧设备握手失败。设为空值可跳过配置加载。
        psi.EnvironmentVariables["OPENSSL_CONF"] = "";

        // 允许 OpenSSL 3.x 加载 legacy provider（支持旧设备使用的 RC4、MD5 等算法）
        psi.EnvironmentVariables["OPENSSL_MODULES"] = _toolsDir;
        psi.EnvironmentVariables["OPENSSL_LEGACY"] = "1";

        return psi;
    }

    private string RunTool(string toolName, string arguments, int timeoutMs)
    {
        var toolPath = Path.Combine(_toolsDir, $"{toolName}.exe");

        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException($"未找到工具: {toolName}.exe。请将 libimobiledevice 工具放入 {_toolsDir}", toolPath);
        }

        var psi = CreateToolStartInfo(toolPath, arguments);

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

        var psi = CreateToolStartInfo(toolPath, arguments);

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

        // 优先返回 stdout，若为空（如工具报错写在 stderr）则返回 stderr
        return !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
    }
}