using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AltServer.Windows.Models;
using AltServer.Windows.Services;

namespace AltServer.Windows.ViewModels;

/// <summary>
/// 主界面ViewModel：管理设备、日志、签名配置和HTTP服务器
/// </summary>
public class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly DeviceService _devices;
    private readonly SigningService _signing;
    private readonly SigningDiscoveryService _discovery;
    private readonly InstalledAppRegistry _registry;
    private readonly HttpServer _server;

    private bool _isServerRunning;
    private bool _isRefreshing;
    private Device? _selectedDevice;
    private string _toolsStatus = string.Empty;
    private string _signingStatus = string.Empty;
    private string _deviceName = string.Empty;
    private string _osVersion = string.Empty;

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _devicePollTimer;

    public MainViewModel()
    {
        _settings = new SettingsService();
        _devices = new DeviceService(_settings.ResolveToolsDir());
        _signing = new SigningService(_settings.ResolveToolsDir());
        _discovery = new SigningDiscoveryService();
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltServer");
        Directory.CreateDirectory(appDataDir);
        _registry = new InstalledAppRegistry(appDataDir);

        Devices = new ObservableCollection<Device>();
        InstalledApps = new ObservableCollection<InstalledApp>();
        LogEntries = new ObservableCollection<string>();
        LogEntries.CollectionChanged += (_, _) =>
        {
            LogText = string.Join(Environment.NewLine, LogEntries);
        };

        _server = new HttpServer(_settings.Data.ServerPort);
        RegisterServerHandlers();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(_settings.Data.RefreshIntervalHours) };
        _refreshTimer.Tick += async (_, _) =>
        {
            // 检查是否有即将过期的应用
            var expiring = _registry.GetExpiringApps(withinDays: 3);
            if (expiring.Count > 0)
            {
                LogService.Info($"检测到 {expiring.Count} 个应用即将过期，自动续签...");
                await RefreshAllAsync();
            }
        };

        _devicePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _devicePollTimer.Tick += (_, _) => PollDevices();
    }

    public ObservableCollection<Device> Devices { get; }
    public ObservableCollection<InstalledApp> InstalledApps { get; }
    public ObservableCollection<string> LogEntries { get; }
    public ObservableCollection<CertificateItem> AvailableCertificates { get; } = new();
    public ObservableCollection<ProvisionItem> AvailableProvisions { get; } = new();

    private string _logText = string.Empty;
    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    // MARK: - 绑定属性

    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set => SetField(ref _isServerRunning, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetField(ref _isRefreshing, value);
    }

    public Device? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetField(ref _selectedDevice, value))
            {
                LoadDeviceApp();
            }
        }
    }

    public string ToolsStatus
    {
        get => _toolsStatus;
        private set => SetField(ref _toolsStatus, value);
    }

    public string SigningStatus
    {
        get => _signingStatus;
        private set => SetField(ref _signingStatus, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetField(ref _deviceName, value);
    }

    public string OsVersion
    {
        get => _osVersion;
        private set => SetField(ref _osVersion, value);
    }

    public string AppleId
    {
        get => _settings.Data.AppleId;
        set
        {
            _settings.Data.AppleId = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AppleIdDisplay));
        }
    }

    public string AppleIdDisplay => string.IsNullOrEmpty(_settings.Data.AppleId) ? "未登录 Apple ID (点击右上角登录)" : _settings.Data.AppleId;

    public string P12Path
    {
        get => _settings.Data.P12Path;
        set { _settings.Data.P12Path = value; _settings.Save(); OnPropertyChanged(); }
    }

    public string P12Password
    {
        get => _settings.Data.P12Password;
        set { _settings.Data.P12Password = value; _settings.Save(); OnPropertyChanged(); }
    }

    public string MobileProvisionPath
    {
        get => _settings.Data.MobileProvisionPath;
        set
        {
            _settings.Data.MobileProvisionPath = value;

            // 显示配置文件过期时间
            var expiry = SigningService.ParseProvisionExpiry(value);
            if (expiry is not null)
            {
                ProvisionExpiryText = $"配置文件有效期至: {expiry:yyyy-MM-dd HH:mm} (剩余{Math.Max(0, (int)(expiry.Value - DateTime.Now).TotalDays)}天)";
                ProvisionRemainingDays = Math.Max(0, (int)(expiry.Value - DateTime.Now).TotalDays);
            }
            else
            {
                ProvisionExpiryText = string.Empty;
                ProvisionRemainingDays = 0;
            }

            _settings.Save();
            OnPropertyChanged();
        }
    }

    private string _provisionExpiryText = string.Empty;
    public string ProvisionExpiryText
    {
        get => _provisionExpiryText;
        private set => SetField(ref _provisionExpiryText, value);
    }

    private int _provisionRemainingDays;
    public int ProvisionRemainingDays
    {
        get => _provisionRemainingDays;
        private set => SetField(ref _provisionRemainingDays, value);
    }

    public string ServerPort => _settings.Data.ServerPort.ToString();

    public bool AutoRefreshEnabled
    {
        get => _settings.Data.AutoRefreshEnabled;
        set
        {
            _settings.Data.AutoRefreshEnabled = value;
            _settings.Save();

            if (value) _refreshTimer.Start();
            else _refreshTimer.Stop();

            OnPropertyChanged();
        }
    }

    public string Version => $"v{AppVersion.Version}";

    private CertificateItem? _selectedCertificate;
    public CertificateItem? SelectedCertificate
    {
        get => _selectedCertificate;
        set
        {
            if (SetField(ref _selectedCertificate, value) && value is not null)
            {
                P12Path = value.Path;
            }
        }
    }

    private ProvisionItem? _selectedProvision;
    public ProvisionItem? SelectedProvision
    {
        get => _selectedProvision;
        set
        {
            if (SetField(ref _selectedProvision, value) && value is not null)
            {
                MobileProvisionPath = value.Path;
            }
        }
    }

    // MARK: - 智能扫描

    public void ScanSigningConfig()
    {
        LogService.Info("正在扫描签名配置...");

        AvailableCertificates.Clear();
        AvailableProvisions.Clear();

        // 扫描证书
        var certs = _discovery.DiscoverCertificates();
        var p12s = _discovery.DiscoverP12Files();
        var allCerts = certs.Concat(p12s).ToList();

        // 去重（按 Subject 或 Path）
        var seen = new HashSet<string>();
        foreach (var c in allCerts)
        {
            var key = string.IsNullOrEmpty(c.Thumbprint) || c.Thumbprint.Length > 40 ? c.Source : c.Thumbprint;
            if (!seen.Add(key)) continue;
            AvailableCertificates.Add(new CertificateItem
            {
                DisplayName = $"{c.Subject} ({c.NotAfter:yyyy-MM-dd}) — {Path.GetFileName(c.Source)}",
                Path = c.Source,
                Thumbprint = c.Thumbprint,
                NotAfter = c.NotAfter
            });
        }

        // 扫描配置文件
        var provisions = _discovery.DiscoverProvisionProfiles();
        var seenPaths = new HashSet<string>();
        foreach (var p in provisions)
        {
            if (!seenPaths.Add(p.Path)) continue;
            var expiry = p.Expiry < DateTime.MaxValue ? $" 有效至 {p.Expiry:yyyy-MM-dd}" : "";
            AvailableProvisions.Add(new ProvisionItem
            {
                DisplayName = $"{p.Name}{expiry} — {Path.GetFileName(p.Source)}",
                Path = p.Path,
                Expiry = p.Expiry
            });
        }

        _settings.Reload();
        LogService.Info($"扫描完成: 找到 {AvailableCertificates.Count} 个证书, {AvailableProvisions.Count} 个配置文件");

        // 1. 证书自动选取：优先保留已配置且有效的文件；若未配置或失效，则自动选取最新有效证书
        CertificateItem? matchedCert = null;
        if (!string.IsNullOrEmpty(P12Path) && File.Exists(P12Path))
        {
            matchedCert = AvailableCertificates.FirstOrDefault(c =>
                c.Path == P12Path || c.Thumbprint == P12Path);
        }

        if (matchedCert is not null)
        {
            SelectedCertificate = matchedCert;
        }
        else if (AvailableCertificates.Count > 0)
        {
            var bestCert = AvailableCertificates
                .OrderByDescending(c => c.NotAfter)
                .ThenByDescending(c => File.Exists(c.Path) ? File.GetLastWriteTime(c.Path) : DateTime.MinValue)
                .FirstOrDefault();
            if (bestCert is not null)
            {
                SelectedCertificate = bestCert;
                LogService.Info($"自动配置代码签名证书: {bestCert.DisplayName}");
            }
        }

        // 2. 描述文件自动选取：优先保留已配置且有效的文件；若未配置或失效，则自动选取最新文件
        ProvisionItem? matchedProv = null;
        if (!string.IsNullOrEmpty(MobileProvisionPath) && File.Exists(MobileProvisionPath))
        {
            matchedProv = AvailableProvisions.FirstOrDefault(p => p.Path == MobileProvisionPath);
        }

        if (matchedProv is not null)
        {
            SelectedProvision = matchedProv;
        }
        else if (AvailableProvisions.Count > 0)
        {
            var bestProv = AvailableProvisions
                .OrderByDescending(p => p.Expiry)
                .ThenByDescending(p => File.Exists(p.Path) ? File.GetLastWriteTime(p.Path) : DateTime.MinValue)
                .FirstOrDefault();
            if (bestProv is not null)
            {
                SelectedProvision = bestProv;
                LogService.Info($"自动配置描述文件: {bestProv.DisplayName}");
            }
        }

        if (!string.IsNullOrEmpty(MobileProvisionPath) && File.Exists(MobileProvisionPath))
        {
            var expiry = SigningService.ParseProvisionExpiry(MobileProvisionPath);
            if (expiry is not null)
            {
                ProvisionExpiryText = $"有效期至: {expiry:yyyy-MM-dd HH:mm} (剩余 {Math.Max(0, (int)(expiry.Value - DateTime.Now).TotalDays)} 天)";
            }
            else
            {
                ProvisionExpiryText = "已配置签名文件";
            }
        }
        else
        {
            ProvisionExpiryText = "未配置证书与描述文件";
        }

        OnPropertyChanged(nameof(AppleIdDisplay));
    }

    // MARK: - 生命周期

    public void Start()
    {
        LogService.Initialize();
        LogService.LogReceived += (msg, level) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrEmpty(msg))
                {
                    LogEntries.Add(msg);
                    while (LogEntries.Count > 500)
                        LogEntries.RemoveAt(0);
                }
            });
        };

        // 注意：VerifyTools() 不在此调用，由 MainWindow.Loaded 触发
        // 以保证 MissingToolsDetected 事件订阅已完成，弹框可正常显示

        try
        {
            _server.Start();
            IsServerRunning = true;
        }
        catch (Exception ex)
        {
            LogService.Error($"HTTP服务器启动失败（端口 {_settings.Data.ServerPort} 可能被占用）: {ex.Message}");
            IsServerRunning = false;
        }

        if (_settings.Data.AutoRefreshEnabled)
        {
            _refreshTimer.Start();
        }

        _devicePollTimer.Start();

        // 开机自启动时最小化
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            MinimizeToTrayRequested?.Invoke();
        }

        LogService.Success($"AltServer Windows {AppVersion.Version} 已启动");
    }

    public void Stop()
    {
        _devicePollTimer.Stop();
        _refreshTimer.Stop();
        _server.Stop();
        IsServerRunning = false;
    }

    // MARK: - 工具检测

    /// <summary>当检测到工具缺失时触发，参数为缺失工具列表和工具目录路径</summary>
    public event Action<string[], string>? MissingToolsDetected;

    /// <summary>返回 true 表示所有工具齐全</summary>
    public bool VerifyTools()
    {
        var toolsDir = _settings.ResolveToolsDir();
        var required = new[] { "idevice_id.exe", "ideviceinfo.exe", "ideviceinstaller.exe", "idevicepair.exe", "zsign.exe" };

        var missing = required.Where(t => !File.Exists(Path.Combine(toolsDir, t))).ToArray();

        ToolsStatus = missing.Length == 0
            ? $"工具完整 ✓ ({Path.GetFileName(toolsDir)})"
            : $"缺少工具: {string.Join(", ", missing.Select(Path.GetFileNameWithoutExtension))}";

        if (missing.Length > 0)
        {
            LogService.Warning($"工具缺失，请检查目录 {toolsDir}：{string.Join(", ", missing)}");
            // 通知 UI 层弹出提示，而非静默失败
            MissingToolsDetected?.Invoke(missing, toolsDir);
            return false;
        }
        else
        {
            LogService.Info($"检测到完整工具集位于 {toolsDir}");
            return true;
        }
    }

    public event Action? MinimizeToTrayRequested;

    // MARK: - 设备管理

    private void PollDevices()
    {
        // 降低轮询频率：只有当界面可见时才主动刷新
        if (!IsVisible) return;

        try
        {
            var devices = _devices.ListDevices();

            // 更新已有设备
            foreach (var device in devices)
            {
                var existing = Devices.FirstOrDefault(d => d.Udid == device.Udid);
                if (existing is null)
                {
                    Devices.Add(device);
                    LogService.Info($"检测到设备: {device.Name} ({device.Udid[..Math.Min(8, device.Udid.Length)]}...)");

                    if (Devices.Count == 1)
                    {
                        SelectedDevice = device;
                    }
                }
                else
                {
                    existing.Name = device.Name;
                    existing.Model = device.Model;
                    existing.OsVersion = device.OsVersion;
                    existing.IsPaired = device.IsPaired;
                }
            }

            // 移除断开设备
            var connectedUdids = devices.Select(d => d.Udid).ToHashSet();
            var disconnected = Devices.Where(d => !connectedUdids.Contains(d.Udid)).ToArray();
            foreach (var device in disconnected)
            {
                Devices.Remove(device);
                LogService.Warning($"设备已断开: {device.Name}");
            }
        }
        catch (Exception ex)
        {
            // 工具缺失时静默处理，避免刷屏
            // ToolsStatus 已在 VerifyTools 中提示
            _ = ex;
        }
    }

    private List<Device> GetAllDevices()
    {
        try
        {
            return _devices.ListDevices();
        }
        catch (Exception ex)
        {
            LogService.Error($"获取设备列表失败: {ex.Message}");
            return Devices.ToList();
        }
    }

    public void RefreshDevices()
    {
        try
        {
            var devices = GetAllDevices();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => ReplaceDevices(devices));
            }
            else
            {
                ReplaceDevices(devices);
            }
            LogService.Info($"已刷新设备列表 ({devices.Count} 台)");
        }
        catch (Exception ex)
        {
            LogService.Error($"刷新设备失败: {ex.Message}");
        }
    }

    private void ReplaceDevices(List<Device> devices)
    {
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
    }

    public async Task<bool> PairDeviceAsync()
    {
        var device = SelectedDevice;
        if (device is null) return false;

        try
        {
            LogService.Info($"正在配对 {device.Name}... 请在手机上点击“信任此电脑”");
            var ok = await Task.Run(() => _devices.Pair(device.Udid));

            if (ok)
            {
                device.IsPaired = true;
                LogService.Success($"设备 {device.Name} 配对成功");
            }
            else
            {
                LogService.Warning($"设备 {device.Name} 配对失败，请确认手机上已点击“信任”");
            }

            return ok;
        }
        catch (Exception ex)
        {
            LogService.Error($"配对失败: {ex.Message}");
            return false;
        }
    }

    private void LoadDeviceApp()
    {
        var device = SelectedDevice;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => InstalledApps.Clear());
        }
        else
        {
            InstalledApps.Clear();
        }
        DeviceName = device?.Name ?? string.Empty;
        OsVersion = device?.OsVersion ?? string.Empty;

        if (device is null) return;

        // 第一步：立即从本地注册表加载（不依赖 ideviceinstaller，无延迟）
        var registryApps = _registry.GetInstalledApps(device.Udid);
        if (registryApps.Count > 0)
        {
            var registryInstalled = registryApps.Select(a => new InstalledApp
            {
                BundleIdentifier = a.BundleId,
                Name = a.AppName,
                Version = string.Empty,
                Build = string.Empty
            }).ToList();

            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() =>
                {
                    InstalledApps.Clear();
                    foreach (var app in registryInstalled) InstalledApps.Add(app);
                });
            }
            else
            {
                InstalledApps.Clear();
                foreach (var app in registryInstalled) InstalledApps.Add(app);
            }
        }

        // 第二步：异步用 ideviceinstaller 刷新（补充版本号等信息，超时不影响UI）
        Task.Run(() =>
        {
            try
            {
                var apps = _devices.ListInstalledApps(device.Udid);
                if (apps.Count > 0)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        InstalledApps.Clear();
                        foreach (var app in apps)
                            InstalledApps.Add(app);
                    });
                }
            }
            catch (TimeoutException)
            {
                // ideviceinstaller 超时很常见，本地注册表已先显示，此处静默忽略
                LogService.Warning($"获取 {device.Name} 设备应用列表超时（已显示本地已安装记录）");
            }
            catch (Exception ex)
            {
                // 其他错误也不覆盖已显示的注册表数据
                if (registryApps.Count == 0)
                {
                    LogService.Error($"获取 {device.Name} 已安装应用失败: {ex.Message}");
                }
            }
        });
    }

    // MARK: - IPA签名与安装

    public async Task<bool> SignAndInstallAsync(string ipaPath)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            LogService.Warning("请先选择目标设备");
            return false;
        }

        if (!device.IsPaired && !await PairDeviceAsync())
        {
            LogService.Error("无法连接到设备，请先配对");
            return false;
        }

        if (string.IsNullOrEmpty(_settings.Data.P12Path) || !File.Exists(_settings.Data.P12Path) ||
            string.IsNullOrEmpty(_settings.Data.MobileProvisionPath) || !File.Exists(_settings.Data.MobileProvisionPath))
        {
            ScanSigningConfig();
        }

        if (string.IsNullOrEmpty(_settings.Data.P12Path) || !File.Exists(_settings.Data.P12Path) ||
            string.IsNullOrEmpty(_settings.Data.MobileProvisionPath) || !File.Exists(_settings.Data.MobileProvisionPath))
        {
            LogService.Error("未检测到有效的签名证书或描述文件，请点击“登录 / 切换账号”自动配置");
            return false;
        }

        try
        {
            IsRefreshing = true;

            var outputIpa = Path.Combine(
                Path.GetTempPath(), "AltServer",
                $"{Path.GetFileNameWithoutExtension(ipaPath)}_signed_{DateTime.Now:yyyyMMdd_HHmmss}.ipa");

            LogService.Info($"开始签名: {Path.GetFileName(ipaPath)}");

            var p12Password = SigningService.ResolveP12Password(_settings.Data.P12Path, _settings.Data.P12Password);
            if (!string.IsNullOrEmpty(p12Password) && _settings.Data.P12Password != p12Password)
            {
                _settings.Data.P12Password = p12Password;
                _settings.Save();
            }

            var options = new SigningService.SigningOptions
            {
                P12Path = _settings.Data.P12Path,
                P12Password = p12Password,
                MobileProvisionPath = _settings.Data.MobileProvisionPath
            };

            var signedIpa = await _signing.SignIpaAsync(ipaPath, outputIpa, options);
            LogService.Success($"签名完成: {Path.GetFileName(signedIpa)}");

            LogService.Info($"正在安装到 {device.Name}...");
            await Task.Run(() => _devices.InstallIpa(device.Udid, signedIpa));
            LogService.Success($"安装成功: {Path.GetFileName(ipaPath)} → {device.Name}");

            // 注册到已安装应用列表（使用描述文件中的真实 Bundle ID）
            var realBundleId = SigningService.ParseProvisionBundleId(_settings.Data.MobileProvisionPath)
                               ?? System.IO.Path.GetFileNameWithoutExtension(ipaPath);
            var appName = System.IO.Path.GetFileNameWithoutExtension(ipaPath);
            _registry.Register(
                realBundleId,
                ipaPath,
                appName,
                device.Udid,
                _settings.Data.P12Path,
                _settings.Data.MobileProvisionPath,
                p12Password);

            LoadDeviceApp();
            return true;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;

            // 针对常见安装失败给出明确指导
            if (msg.Contains("lockdownd", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Could not connect", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Error(
                    "安装失败：无法连接到设备守护进程（lockdownd）。\n" +
                    "请检查：\n" +
                    "  1. 设备屏幕是否亮着且已解锁（不能处于锁屏状态）\n" +
                    "  2. 是否已在设备上点击「信任此电脑」\n" +
                    "  3. USB 线连接是否稳定（尝试重新插拔）\n" +
                    "  4. 如果是 iPad，确认描述文件已包含该设备 UDID\n" +
                    $"（原始错误: {msg}）");
            }
            else if (msg.Contains("ProfileInstall", StringComparison.OrdinalIgnoreCase) ||
                     msg.Contains("MismatchedApplicationIdentifier", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Error(
                    "安装失败：描述文件与设备不匹配。\n" +
                    "可能原因：当前描述文件是为其他设备（如 iPhone）生成的，不包含此 iPad 的 UDID。\n" +
                    "请点击「登录 / 切换账号」重新生成适合此设备的描述文件。\n" +
                    $"（原始错误: {msg}）");
            }
            else
            {
                LogService.Error($"签名/安装失败: {msg}");
            }
            return false;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // MARK: - 刷新

    public async Task RefreshAllAsync()
    {
        if (IsRefreshing) return;

        try
        {
            IsRefreshing = true;
            LogService.Info("开始自动刷新所有已安装应用...");

            var devices = GetAllDevices();
            int refreshed = 0, failed = 0;

            foreach (var device in devices)
            {
                LogService.Info($"刷新设备: {device.Name}");

                var apps = _registry.GetInstalledApps(device.Udid);
                LogService.Info($"  该设备有 {apps.Count} 个已安装应用");

                foreach (var app in apps)
                {
                    try
                    {
                        // 检查缓存的 IPA 是否存在
                        if (!File.Exists(app.CachedIpaPath))
                        {
                            LogService.Warning($"  跳过 {app.AppName}: 缓存 IPA 不存在 ({app.CachedIpaPath})");
                            continue;
                        }

                        // 检查是否即将过期（7天内）
                        if (app.Expiry.HasValue && app.Expiry.Value > DateTime.UtcNow.AddDays(7))
                        {
                            LogService.Info($"  跳过 {app.AppName}: 配置文件有效 (剩余 {app.RemainingDays()} 天)");
                            continue;
                        }

                        LogService.Info($"  重新签名: {app.AppName}...");

                        var outputIpa = Path.Combine(
                            Path.GetTempPath(), "AltServer",
                            $"{app.BundleId}_refreshed_{DateTime.Now:yyyyMMdd_HHmmss}.ipa");

                        var p12Path = app.P12Path ?? _settings.Data.P12Path;
                        var p12Password = SigningService.ResolveP12Password(p12Path, app.P12Password ?? _settings.Data.P12Password);
                        var options = new SigningService.SigningOptions
                        {
                            P12Path = p12Path,
                            P12Password = p12Password,
                            MobileProvisionPath = app.ProvisionPath ?? _settings.Data.MobileProvisionPath
                        };

                        var signedIpa = await _signing.SignIpaAsync(app.CachedIpaPath, outputIpa, options);
                        LogService.Info($"  签名完成，正在安装...");

                        await Task.Run(() => _devices.InstallIpa(device.Udid, signedIpa));
                        _registry.MarkRefreshed(app.BundleId, device.Udid);

                        LogService.Success($"  刷新成功: {app.AppName}");
                        refreshed++;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"  刷新失败 {app.AppName}: {ex.Message}");
                        failed++;
                    }
                }
            }

            LogService.Success($"刷新完成: 成功 {refreshed} 个, 失败 {failed} 个");
        }
        catch (Exception ex)
        {
            LogService.Error($"刷新失败: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>获取已安装应用的注册表信息</summary>
    public InstalledAppRegistry GetRegistry() => _registry;

    // MARK: - HTTP API 处理器

    private void RegisterServerHandlers()
    {
        _server.StatusHandler = () => HttpJsonResponse.Ok(new
        {
            version = AppVersion.Version,
            status = "running",
            devices = Devices.Count
        });

        _server.DevicesHandler = () =>
        {
            var list = GetAllDevices().Select(d => new
            {
                d.Udid,
                d.Name,
                d.Model,
                d.ProductType,
                d.OsVersion,
                d.IsPaired,
                connection = d.Connection.ToString()
            }).ToArray();

            return HttpJsonResponse.Ok(new { devices = list });
        };

        _server.InstallHandler = request =>
        {
            var json = request.GetBodyJson();
            if (!json.HasValue) return HttpJsonResponse.Error(400, "Missing JSON body");

            var ipaPath = JsonElement_GetString(json.Value, "ipaPath");
            var udid = JsonElement_GetString(json.Value, "udid");

            if (string.IsNullOrEmpty(ipaPath)) return HttpJsonResponse.Error(400, "Missing ipaPath");
            if (!File.Exists(ipaPath)) return HttpJsonResponse.Error(404, "IPA 文件不存在");

            try
            {
                var device = Devices.FirstOrDefault(d => d.Udid == udid)
                             ?? GetDeviceByUdid(udid);
                if (device is null) return HttpJsonResponse.Error(404, "找不到目标设备");

                Task.Run(async () => await SignAndInstallAsync(ipaPath));
                return HttpJsonResponse.Ok(new { status = "installing", message = "已开始安装" });
            }
            catch (Exception ex)
            {
                return HttpJsonResponse.Error(500, ex.Message);
            }
        };

        _server.UninstallHandler = request =>
        {
            var json = request.GetBodyJson();
            if (!json.HasValue) return HttpJsonResponse.Error(400, "Missing JSON body");

            var bundleId = JsonElement_GetString(json.Value, "bundleIdentifier");
            var udid = JsonElement_GetString(json.Value, "udid");

            if (string.IsNullOrEmpty(bundleId)) return HttpJsonResponse.Error(400, "Missing bundleIdentifier");

            try
            {
                var device = Devices.FirstOrDefault(d => d.Udid == udid) ?? GetDeviceByUdid(udid);
                if (device is null) return HttpJsonResponse.Error(404, "找不到目标设备");

                _devices.UninstallApp(device.Udid, bundleId);
                LoadDeviceApp();
                return HttpJsonResponse.Ok(new { status = "ok", message = $"已卸载 {bundleId}" });
            }
            catch (Exception ex)
            {
                return HttpJsonResponse.Error(500, ex.Message);
            }
        };

        _server.RefreshHandler = request =>
        {
            _ = Task.Run(async () => await RefreshAllAsync());
            return HttpJsonResponse.Ok(new { status = "refreshing" });
        };

        _server.AppsHandler = request =>
        {
            var json = request.GetBodyJson();
            var udid = json.HasValue ? JsonElement_GetString(json.Value, "udid") : null;

            var device = Devices.FirstOrDefault(d => d.Udid == udid);
            if (device is null && udid is not null)
            {
                try
                {
                    var all = GetAllDevices();
                    device = all.FirstOrDefault(d => d.Udid == udid);
                }
                catch { }
            }

            var apps = device is not null
                ? _devices.ListInstalledApps(device.Udid)
                : _devices.ListInstalledApps(SelectedDevice?.Udid ?? string.Empty);

            var list = apps.Select(a => new
            {
                a.BundleIdentifier,
                a.Name,
                a.Version,
                a.Build,
                a.InstallDate
            }).ToArray();

            return HttpJsonResponse.Ok(new { apps = list });
        };

        _server.CertificatesHandler = () =>
        {
            var certs = new object[]
            {
                new
                {
                    commonName = Path.GetFileName(_settings.Data.P12Path),
                    serialNumber = "local",
                    teamName = "",
                    isValid = !string.IsNullOrEmpty(_settings.Data.P12Path) && File.Exists(_settings.Data.P12Path),
                    expires = SigningService.ParseProvisionExpiry(_settings.Data.MobileProvisionPath)?.ToString("o")
                }
            };

            return HttpJsonResponse.Ok(new { certificates = certs });
        };
    }

    private static string JsonElement_GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private Device? GetDeviceByUdid(string udid)
    {
        if (string.IsNullOrEmpty(udid)) return null;

        try
        {
            return GetAllDevices().FirstOrDefault(d => d.Udid == udid);
        }
        catch
        {
            return null;
        }
    }

    public bool IsVisible { get; set; } = true;
}

/// <summary>INotifyPropertyChanged 基础类</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>签名配置项：证书</summary>
public class CertificateItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Thumbprint { get; set; } = string.Empty;
    public DateTime NotAfter { get; set; }
}

/// <summary>签名配置项：配置文件</summary>
public class ProvisionItem
{
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
}