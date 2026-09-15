using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

        Devices = new ObservableCollection<Device>();
        InstalledApps = new ObservableCollection<InstalledApp>();
        LogEntries = new ObservableCollection<string>();

        _server = new HttpServer(_settings.Data.ServerPort);
        RegisterServerHandlers();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(_settings.Data.RefreshIntervalHours) };
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync();

        _devicePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _devicePollTimer.Tick += (_, _) => PollDevices();
    }

    public ObservableCollection<Device> Devices { get; }
    public ObservableCollection<InstalledApp> InstalledApps { get; }
    public ObservableCollection<string> LogEntries { get; }

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

    // MARK: - 生命周期

    public void Start()
    {
        LogService.Initialize();
        LogService.LogReceived += (msg, level) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrEmpty(msg))
                {
                    LogEntries.Add(msg);
                    while (LogEntries.Count > 500)
                        LogEntries.RemoveAt(0);
                }
            });
        };

        VerifyTools();

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

    private void VerifyTools()
    {
        var toolsDir = _settings.ResolveToolsDir();
        var required = new[] { "idevice_id.exe", "ideviceinfo.exe", "ideviceinstaller.exe", "idevicepair.exe", "zsign.exe" };

        var missing = required.Where(t => !File.Exists(Path.Combine(toolsDir, t))).ToArray();

        ToolsStatus = missing.Length == 0
            ? $"工具完整 ✓ ({Path.GetFileName(toolsDir)})"
            : $"缺少工具: {string.Join(", ", missing.Select(Path.GetFileNameWithoutExtension))} 请放置到 {toolsDir}";

        if (missing.Length > 0)
        {
            LogService.Warning(ToolsStatus);
        }
        else
        {
            LogService.Info($"检测到完整工具集位于 {toolsDir}");
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
            Devices.Clear();
            foreach (var d in devices) Devices.Add(d);
            LogService.Info($"已刷新设备列表 ({devices.Count} 台)");
        }
        catch (Exception ex)
        {
            LogService.Error($"刷新设备失败: {ex.Message}");
        }
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
        InstalledApps.Clear();
        DeviceName = device?.Name ?? string.Empty;
        OsVersion = device?.OsVersion ?? string.Empty;

        if (device is null) return;

        // 异步加载已安装应用，避免UI卡顿
        Task.Run(() =>
        {
            try
            {
                var apps = _devices.ListInstalledApps(device.Udid);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    InstalledApps.Clear();
                    foreach (var app in apps)
                        InstalledApps.Add(app);
                });
            }
            catch (Exception ex)
            {
                LogService.Error($"获取 {device.Name} 已安装应用失败: {ex.Message}");
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

        if (string.IsNullOrEmpty(_settings.Data.P12Path) || string.IsNullOrEmpty(_settings.Data.MobileProvisionPath))
        {
            LogService.Error("请在“签名配置”中设置 .p12 证书和 .mobileprovision 配置文件");
            return false;
        }

        try
        {
            IsRefreshing = true;

            var outputIpa = Path.Combine(
                Path.GetTempPath(), "AltServer",
                $"{Path.GetFileNameWithoutExtension(ipaPath)}_signed_{DateTime.Now:yyyyMMdd_HHmmss}.ipa");

            LogService.Info($"开始签名: {Path.GetFileName(ipaPath)}");

            var options = new SigningService.SigningOptions
            {
                P12Path = _settings.Data.P12Path,
                P12Password = _settings.Data.P12Password,
                MobileProvisionPath = _settings.Data.MobileProvisionPath
            };

            var signedIpa = await _signing.SignIpaAsync(ipaPath, outputIpa, options);
            LogService.Success($"签名完成: {Path.GetFileName(signedIpa)}");

            LogService.Info($"正在安装到 {device.Name}...");
            await Task.Run(() => _devices.InstallIpa(device.Udid, signedIpa));
            LogService.Success($"安装成功: {Path.GetFileName(ipaPath)} → {device.Name}");

            LoadDeviceApp();
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"签名/安装失败: {ex.Message}");
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
            foreach (var device in devices)
            {
                LogService.Info($"刷新设备: {device.Name}");

                var apps = _devices.ListInstalledApps(device.Udid);
                LogService.Info($"  检测到 {apps.Count} 个已安装应用");

                // 实际刷新逻辑：对每个AltStore管理的应用重新签名安装
                // TODO: 后续版本在设备上记录AltStore管理的应用列表，逐一重新签名
            }

            LogService.Success("刷新完成");
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
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonElement.ValueKind.String)
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