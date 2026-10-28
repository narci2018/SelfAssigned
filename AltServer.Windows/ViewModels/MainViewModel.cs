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
using AltServer.Windows.Views;

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
    private readonly IpaLibraryService _ipaLibrary;
    private readonly PhotosService _photos = new();
    private readonly Dictionary<string, Window> _featureWindows = new();

    private bool _isServerRunning;
    private bool _isRefreshing;
    private Device? _selectedDevice;
    private string _toolsStatus = string.Empty;
    private string _signingStatus = string.Empty;
    private string _deviceName = string.Empty;
    private string _osVersion = string.Empty;

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _devicePollTimer;
    private readonly DispatcherTimer _expiryCheckTimer;

    /// <summary>签名/描述文件过期提醒：剩余天数 ≤ 该值时提醒（托盘气泡 + 日志）</summary>
    public const int ExpiryRemindWithinDays = 3;

    /// <summary>检测到签名即将过期/已过期时触发（参数为提醒文案），UI 层用于托盘气泡</summary>
    public event Action<string>? ExpiryReminderDetected;

    private DateTime _lastRemindUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastAutoProvisionAttemptUtc = new();

    /// <summary>自动续期时使用的默认 App ID（与登录向导一致）</summary>
    private const string FallbackProvisionBundleId = "com.selfassigned.altserver";

    public MainViewModel()
    {
        _settings = new SettingsService();
        _devices = new DeviceService(_settings.ResolveToolsDir());
        _signing = new SigningService(_settings.ResolveToolsDir());
        _discovery = new SigningDiscoveryService();
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltServer");
        Directory.CreateDirectory(appDataDir);
        _registry = new InstalledAppRegistry(appDataDir);
        _ipaLibrary = new IpaLibraryService();

        // 为 libimobiledevice 原生库（照片/投屏 P/Invoke）注册 DLL 搜索路径
        DeviceNative.EnsureToolsDirInSearchPath(_settings.ResolveToolsDir());

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

        // 过期提醒巡检：每小时检查一次（托盘气泡 24 小时内只弹一次）
        _expiryCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _expiryCheckTimer.Tick += (_, _) => CheckExpiryAndRemind();

        RefreshAccountsView();
        RefreshIpaLibraryView();
    }

    public ObservableCollection<Device> Devices { get; }
    public ObservableCollection<InstalledApp> InstalledApps { get; }
    public ObservableCollection<string> LogEntries { get; }
    public ObservableCollection<CertificateItem> AvailableCertificates { get; } = new();
    public ObservableCollection<ProvisionItem> AvailableProvisions { get; } = new();

    /// <summary>多账号列表（v1.0.86）</summary>
    public ObservableCollection<AccountDisplayItem> Accounts { get; } = new();

    /// <summary>IPA 库（v1.0.86）</summary>
    public ObservableCollection<IpaLibraryItem> IpaLibrary { get; } = new();

    private AccountDisplayItem? _selectedAccount;
    public AccountDisplayItem? SelectedAccount
    {
        get => _selectedAccount;
        set => SetField(ref _selectedAccount, value);
    }

    private IpaLibraryItem? _selectedIpa;
    public IpaLibraryItem? SelectedIpa
    {
        get => _selectedIpa;
        set => SetField(ref _selectedIpa, value);
    }

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
                // 切换设备时加载该设备的专属证书配置到全局字段（供 UI 绑定显示）
                if (value is not null)
                {
                    var cfg = _settings.Data.GetDeviceConfig(value.Udid);
                    if (!string.IsNullOrEmpty(cfg.P12Path))
                    {
                        _settings.Data.P12Path = cfg.P12Path;
                        _settings.Data.P12Password = cfg.P12Password;
                        _settings.Data.MobileProvisionPath = cfg.MobileProvisionPath;
                        OnPropertyChanged(nameof(P12Path));
                        OnPropertyChanged(nameof(MobileProvisionPath));
                    }
                }
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

    // MARK: - 多账号管理（v1.0.86）

    public string AccountSummaryText =>
        Accounts.Count == 0
            ? "未登录 Apple ID (点击右上角登录)"
            : $"共 {Accounts.Count} 个账号，当前: {_settings.Data.ActiveAccount ?? "未设置"}";

    /// <summary>从设置重建账号列表（界面展示层）</summary>
    public void RefreshAccountsView()
    {
        var selected = SelectedAccount?.AppleId;
        Accounts.Clear();
        foreach (var acct in _settings.Data.Accounts)
        {
            var boundDevices = _settings.Data.DeviceAccounts.Count(kv =>
                string.Equals(kv.Value, acct.AppleId, StringComparison.OrdinalIgnoreCase));
            var daysRemaining = 0;
            string expiryText;
            if (acct.ProfileExpiry.HasValue)
            {
                daysRemaining = (int)Math.Floor((acct.ProfileExpiry.Value - DateTime.Now).TotalDays);
                expiryText = daysRemaining > 0
                    ? $"描述文件剩余 {daysRemaining} 天"
                    : "描述文件已过期";
            }
            else
            {
                expiryText = "无签名材料";
            }

            Accounts.Add(new AccountDisplayItem
            {
                AppleId = acct.AppleId,
                IsCurrent = string.Equals(_settings.Data.ActiveAccount, acct.AppleId, StringComparison.OrdinalIgnoreCase),
                BoundDevices = boundDevices,
                HasPassword = acct.HasPassword,
                ExpiryText = expiryText,
                DaysRemaining = daysRemaining
            });
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.AppleId == selected) ?? Accounts.FirstOrDefault();
        OnPropertyChanged(nameof(AccountSummaryText));
        OnPropertyChanged(nameof(AppleIdDisplay));
    }

    /// <summary>设备列表上的"绑定账号"标签同步</summary>
    public void UpdateDeviceAccountLabels()
    {
        foreach (var d in Devices)
        {
            d.AccountName = _settings.Data.GetAccountForDevice(d.Udid);
        }
    }

    /// <summary>切换当前激活账号</summary>
    public void SetActiveAccount(string appleId)
    {
        _settings.Data.SetActiveAccount(appleId);
        _settings.Save();
        LogService.Info($"当前账号已切换为 {appleId}");
        RefreshAccountsView();
        UpdateDeviceAccountLabels();
    }

    /// <summary>移除账号（解除设备绑定，签名文件保留在磁盘）</summary>
    public void RemoveAccount(string appleId)
    {
        var unbound = _settings.Data.RemoveAccount(appleId);
        _settings.Save();
        LogService.Info(unbound.Count > 0
            ? $"已移除账号 {appleId}，并解除 {unbound.Count} 台设备的绑定（签名文件保留）"
            : $"已移除账号 {appleId}");
        RefreshAccountsView();
        UpdateDeviceAccountLabels();
    }

    // MARK: - IPA 库管理（v1.0.86）

    public void RefreshIpaLibraryView()
    {
        IpaLibrary.Clear();
        foreach (var item in _ipaLibrary.Items.OrderByDescending(i => i.AddedAt))
        {
            IpaLibrary.Add(item);
        }
    }

    /// <summary>批量添加 IPA 到库，返回成功条数</summary>
    public int AddIpaToLibrary(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var file in files)
        {
            var item = _ipaLibrary.Add(file);
            if (item is not null)
            {
                added++;
                var bundleTag = string.IsNullOrEmpty(item.BundleId) ? "" : $" · {item.BundleId}";
                LogService.Success($"已添加到 IPA 库: {item.DisplayName} ({item.SizeText}){bundleTag}");
            }
        }
        RefreshIpaLibraryView();
        return added;
    }

    /// <summary>从库中移除 IPA（同时删除库内文件）</summary>
    public bool RemoveIpaFromLibrary(IpaLibraryItem item)
    {
        var ok = _ipaLibrary.Remove(item.Id);
        if (ok)
        {
            LogService.Info($"已从 IPA 库移除: {item.DisplayName}");
            RefreshIpaLibraryView();
        }
        return ok;
    }

    /// <summary>获取库内 IPA 的可安装路径；文件丢失时返回 null</summary>
    public string? GetLibraryIpaPath(IpaLibraryItem item)
    {
        var path = _ipaLibrary.GetPath(item);
        return File.Exists(path) ? path : null;
    }

    // MARK: - 过期提醒（v1.0.86）

    /// <summary>全局最早过期时间：账号描述文件 ∪ 各设备描述文件 ∪ 已安装应用记录</summary>
    public DateTime? ComputeEarliestExpiry()
    {
        DateTime? earliest = _settings.Data.EarliestProfileExpiry();

        foreach (var entry in _registry.GetInstalledApps(null))
        {
            if (entry.Expiry.HasValue && (earliest is null || entry.Expiry < earliest))
                earliest = entry.Expiry;
        }

        foreach (var cfg in _settings.Data.DeviceConfigs.Values)
        {
            var expiry = SigningService.ParseProvisionExpiry(cfg.MobileProvisionPath);
            if (expiry.HasValue && (earliest is null || expiry < earliest))
                earliest = expiry;
        }

        return earliest;
    }

    /// <summary>状态栏"最早签名过期"摘要</summary>
    public string ExpirySummaryText
    {
        get
        {
            var earliest = ComputeEarliestExpiry();
            if (earliest is null) return "签名有效期: 未配置";
            var days = (int)Math.Floor((earliest.Value - DateTime.Now).TotalDays);
            return days > 0
                ? $"最早签名过期: {earliest:yyyy-MM-dd} (剩余 {days} 天)"
                : $"签名已过期 ({earliest:yyyy-MM-dd})";
        }
    }

    /// <summary>过期巡检：写日志 + 触发托盘气泡（24 小时内最多弹一次）</summary>
    public void CheckExpiryAndRemind()
    {
        OnPropertyChanged(nameof(ExpirySummaryText));
        OnPropertyChanged(nameof(AccountSummaryText));

        var earliest = ComputeEarliestExpiry();
        if (earliest is null) return;

        var days = (int)Math.Floor((earliest.Value - DateTime.Now).TotalDays);
        if (days > ExpiryRemindWithinDays) return;

        var message = days > 0
            ? $"签名/描述文件将于 {days} 天后过期（{earliest:yyyy-MM-dd}）。已记住密码的账号会在设备连接后自动续期；否则请点击「登录 / 添加账号」重新配置。"
            : $"签名/描述文件已过期（{earliest:yyyy-MM-dd}），应用将无法打开。请点击「登录 / 添加账号」重新配置。";

        LogService.Warning(message);

        if ((DateTime.UtcNow - _lastRemindUtc).TotalHours < 24) return;
        _lastRemindUtc = DateTime.UtcNow;
        ExpiryReminderDetected?.Invoke(message);
    }

    /// <summary>
    /// 自动续期（登录持久化核心）：设备描述文件过期/将过期时，
    /// 用该设备绑定账号的已存密码重新走 GSA 认证 + 自动申请证书描述文件。
    /// 账号开启 2FA 时无法全自动，会给出手动登录提示。
    /// </summary>
    public async Task<bool> EnsureFreshProvisionAsync(Device device)
    {
        var udid = device.Udid;
        var cfg = _settings.Data.GetDeviceConfig(udid);
        var expiry = SigningService.ParseProvisionExpiry(cfg.MobileProvisionPath);
        if (expiry.HasValue && expiry.Value > DateTime.Now.AddDays(1.5))
        {
            return true; // 描述文件仍有效
        }

        // 1 小时内失败过的设备不再反复尝试，避免刷爆日志/触发 Apple 限流
        if (_lastAutoProvisionAttemptUtc.TryGetValue(udid, out var lastAttempt) &&
            (DateTime.UtcNow - lastAttempt).TotalMinutes < 60)
        {
            return false;
        }
        _lastAutoProvisionAttemptUtc[udid] = DateTime.UtcNow;

        var appleId = _settings.Data.GetAccountForDevice(udid) ?? _settings.Data.ActiveAccount;
        var account = _settings.Data.FindAccount(appleId);
        var password = account?.GetPassword();
        if (account is null || string.IsNullOrEmpty(password))
        {
            LogService.Warning(
                $"设备 {device.DisplayName} 的描述文件即将过期，但没有该账号的已存密码，无法自动续期。\n" +
                "请点击「登录 / 添加账号」并勾选记住密码，下次即可自动续期。");
            return false;
        }

        try
        {
            LogService.Info($"描述文件已过期/即将过期，正在用保存的账号 {account.AppleId} 自动续期...");

            var dataDir = AppContext.BaseDirectory;
            var gsa = new AppleGsaClient(_settings.ResolveToolsDir(), dataDir, _settings.Data.AnisetteUrl);
            var auth = await gsa.AuthenticateAsync(account.AppleId, password);
            if (auth.Status == AuthStatus.Error)
            {
                LogService.Error($"自动续期登录失败: {auth.Message}");
                return false;
            }
            if (auth.Status == AuthStatus.Requires2FA)
            {
                LogService.Warning(
                    $"账号 {account.AppleId} 开启了双重认证(2FA)，自动续期需要输入验证码。\n" +
                    "请在界面点击「登录 / 添加账号」完成一次手动登录（只需输密码+验证码，无需重新申请证书）。");
                CheckExpiryAndRemind();
                return false;
            }

            var deviceName = string.IsNullOrWhiteSpace(device.Name)
                ? $"MyDevice-{udid.Replace("-", "")[^8..]}"
                : device.Name;
            var provision = new AppleProvisionService(gsa, dataDir);
            var result = await provision.AutoProvisionAsync(
                FallbackProvisionBundleId, "AltServer", deviceName, udid);

            if (!result.Success || string.IsNullOrEmpty(result.P12Path) || string.IsNullOrEmpty(result.ProvisionPath))
            {
                LogService.Error($"自动续期失败: {result.ErrorMessage}");
                return false;
            }

            _settings.Data.SetDeviceConfig(udid, result.P12Path, "temp123", result.ProvisionPath);
            account.LastLoginUtc = DateTime.UtcNow;
            _settings.Save();
            _lastAutoProvisionAttemptUtc.Remove(udid);

            var newExpiry = SigningService.ParseProvisionExpiry(result.ProvisionPath);
            LogService.Success($"描述文件已自动续期: {device.DisplayName}（有效期至 {newExpiry:yyyy-MM-dd HH:mm}）");
            RefreshAccountsView();
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"自动续期异常: {ex.Message}");
            return false;
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
        _expiryCheckTimer.Start();

        RefreshAccountsView();
        RefreshIpaLibraryView();
        UpdateDeviceAccountLabels();

        // 启动 10 秒后做一次过期巡检（含托盘气泡提醒）；回调切回 UI 线程
        _ = Task.Delay(10000).ContinueWith(_ =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null) dispatcher.BeginInvoke(() => CheckExpiryAndRemind());
        });

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
        _expiryCheckTimer.Stop();
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
                    // 同步"绑定账号"标签（多账号管理）
                    device.AccountName = _settings.Data.GetAccountForDevice(device.Udid);

                    var existing = Devices.FirstOrDefault(d => d.Udid == device.Udid);
                    if (existing is null)
                    {
                        Devices.Add(device);
                        var bound = _settings.Data.GetAccountForDevice(device.Udid);
                        LogService.Info($"检测到设备: {device.Name} ({device.Udid[..Math.Min(8, device.Udid.Length)]}...)" +
                                        (bound is null ? "" : $" [账号: {bound}]"));

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
                        existing.AccountName = device.AccountName;
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
                _devices.RefreshDeviceInfo(device);
                LogService.Success($"设备 {device.DisplayName} 配对成功");
            }
            else
            {
                LogService.Warning($"设备 {device.DisplayName} 配对失败，请确认手机/iPad上已点击“信任”并输入锁屏密码");
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

            // 取当前设备专属的证书/描述文件（多设备时各用各的，不互相覆盖）
            var devCfg = _settings.Data.GetDeviceConfig(device.Udid);
            var p12Password = SigningService.ResolveP12Password(devCfg.P12Path, devCfg.P12Password);
            if (!string.IsNullOrEmpty(p12Password) && devCfg.P12Password != p12Password)
            {
                _settings.Data.SetDeviceConfig(device.Udid, devCfg.P12Path, p12Password, devCfg.MobileProvisionPath);
                _settings.Save();
                devCfg = _settings.Data.GetDeviceConfig(device.Udid);
            }

            var options = new SigningService.SigningOptions
            {
                P12Path = devCfg.P12Path,
                P12Password = p12Password,
                MobileProvisionPath = devCfg.MobileProvisionPath
            };

            var signedIpa = await _signing.SignIpaAsync(ipaPath, outputIpa, options);
            LogService.Success($"签名完成: {Path.GetFileName(signedIpa)}");

            // 检查设备连接方式：Wi-Fi 连接无法用 ideviceinstaller 安装 IPA
            if (device.Connection == Models.ConnectionType.WiFi)
            {
                LogService.Error(
                    $"无法安装到 {(string.IsNullOrEmpty(device.Name) ? device.Udid[..8] : device.Name)}：\n" +
                    "该设备通过 Wi-Fi 连接，ideviceinstaller 不支持 Wi-Fi 模式安装 IPA。\n" +
                    "请用 USB 数据线连接 iPad，或在「设置 → 通用 → VPN与设备管理 → 开发者模式」中关闭「通过网络连接」。");
                return false;
            }

            // 检查描述文件是否包含目标设备 UDID（免费开发者账号描述文件只包含注册的设备）
            var provisionedUdids = SigningService.ParseProvisionDeviceUdids(_settings.Data.MobileProvisionPath);
            if (provisionedUdids.Count > 0 && !provisionedUdids.Contains(device.Udid))
            {
                var deviceLabel = string.IsNullOrEmpty(device.Name) ? device.Udid[..12] : device.Name;
                LogService.Error(
                    $"描述文件不包含此设备（{deviceLabel}），无法安装。\n" +
                    $"当前描述文件已注册 {provisionedUdids.Count} 台设备，但不含此 {(device.ProductType.StartsWith("iPad") ? "iPad" : "设备")}。\n" +
                    "请点击「登录 / 切换账号」重新生成包含此设备的描述文件，然后再安装。");
                return false;
            }

            LogService.Info($"正在安装到 {device.DisplayName}...");
            await Task.Run(() => _devices.InstallIpa(device.Udid, signedIpa));
            LogService.Success($"安装成功: {Path.GetFileName(ipaPath)} → {device.DisplayName}");

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

            // 若本次安装的 IPA 来自库，回填 BundleId 与最近签名时间
            var libItem = _ipaLibrary.Items.FirstOrDefault(i =>
                string.Equals(_ipaLibrary.GetPath(i), ipaPath, StringComparison.OrdinalIgnoreCase));
            if (libItem is not null)
            {
                _ipaLibrary.MarkSigned(libItem.Id, realBundleId);
            }

            LoadDeviceApp();
            CheckExpiryAndRemind();
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

                var hasApps = _registry.GetInstalledApps(device.Udid).Count > 0;
                if (hasApps)
                {
                    // 登录持久化：描述文件过期/将过期时，用已保存的账号密码自动续期后再重签
                    await EnsureFreshProvisionAsync(device);
                }

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

                        // 取该设备的专属证书配置（多设备时各用各的，避免互相覆盖）
                        var devRefreshCfg = _settings.Data.GetDeviceConfig(device.Udid);
                        var p12Path = devRefreshCfg.P12Path;
                        var provisionPath = devRefreshCfg.MobileProvisionPath;
                        var p12Password = SigningService.ResolveP12Password(p12Path, devRefreshCfg.P12Password);
                        var options = new SigningService.SigningOptions
                        {
                            P12Path = p12Path,
                            P12Password = p12Password,
                            MobileProvisionPath = provisionPath
                        };

                        var signedIpa = await _signing.SignIpaAsync(app.CachedIpaPath, outputIpa, options);
                        LogService.Info($"  签名完成，正在安装...");

                        await Task.Run(() => _devices.InstallIpa(device.Udid, signedIpa));
                        // 刷新成功后同步更新注册表中的证书/描述文件路径
                        _registry.UpdatePaths(app.BundleId, device.Udid, p12Path, p12Password, provisionPath);
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
            CheckExpiryAndRemind();
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

    // MARK: - 投屏预览与照片管理（v1.0.87）

    /// <summary>打开实时投屏预览窗口（USB 截图流）</summary>
    public void OpenMirrorWindow()
    {
        if (!EnsureSelectedDevice()) return;
        OpenFeatureWindow("mirror", () => new MirrorWindow(SelectedDevice!),
            "投屏预览窗口已打开。若画面空白，请解锁设备屏幕后等待自动重试。");
    }

    /// <summary>打开照片与视频管理窗口</summary>
    public void OpenPhotosWindow()
    {
        if (!EnsureSelectedDevice()) return;
        OpenFeatureWindow("photos", () => new PhotosWindow(SelectedDevice!),
            null);
    }

    /// <summary>打开设备详细信息面板（仿爱思助手“我的设备”）</summary>
    public void OpenDeviceDetailWindow()
    {
        if (!EnsureSelectedDevice()) return;
        OpenFeatureWindow("device_detail", () => new DeviceDetailWindow(SelectedDevice!, _devices),
            null);
    }

    /// <summary>打开虚拟 U 盘 / 文件系统管理窗口</summary>
    public void OpenFileSystemWindow()
    {
        if (!EnsureSelectedDevice()) return;
        OpenFeatureWindow("filesystem", () => new FileSystemWindow(SelectedDevice!),
            null);
    }

    private bool EnsureSelectedDevice()
    {
        if (SelectedDevice is not null) return true;
        LogService.Warning("请先在左侧选择目标设备");
        return false;
    }

    /// <summary>按 (功能, 设备) 维度管理窗口单例；已打开则置前</summary>
    private void OpenFeatureWindow(string feature, Func<Window> create, string? notice)
    {
        var key = $"{feature}:{SelectedDevice!.Udid}";

        if (_featureWindows.TryGetValue(key, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }
        if (_featureWindows.ContainsKey(key)) _featureWindows.Remove(key);

        var window = create();
        window.Closed += (_, _) => _featureWindows.Remove(key);

        var owner = System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w is MainWindow);
        if (owner is not null) window.Owner = owner;
        window.Show();
        _featureWindows[key] = window;

        if (!string.IsNullOrEmpty(notice)) LogService.Info(notice);
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

/// <summary>账号管理列表项（v1.0.86，界面展示层）</summary>
public class AccountDisplayItem
{
    public string AppleId { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public int BoundDevices { get; set; }
    public bool HasPassword { get; set; }
    public string ExpiryText { get; set; } = string.Empty;
    public int DaysRemaining { get; set; }

    public string Display => IsCurrent ? $"{AppleId}（当前）" : AppleId;
    public string Detail
    {
        get
        {
            var parts = new List<string> { ExpiryText, $"绑定 {BoundDevices} 台设备" };
            if (HasPassword) parts.Add("已记住密码");
            else parts.Add("未记住密码");
            return string.Join(" · ", parts);
        }
    }
}