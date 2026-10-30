using System.IO;
using System.Windows;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

public partial class AppleSetupDialog : Window
{
    private readonly string _dataDir;
    private bool _completed;
    private bool _waitingFor2FA;
    private AppleGsaClient? _gsaClient;
    private string _appleId = string.Empty;
    private string _password = string.Empty;

    public string? ResultP12Path { get; private set; }
    public string? ResultProvisionPath { get; private set; }
    public string? ResultP12Password { get; private set; }
    public string? ResultAppleId { get; private set; }

    private bool _rememberPassword = true;

    public AppleSetupDialog(string dataDir)
    {
        InitializeComponent();
        _dataDir = dataDir;

        try
        {
            var settings = new SettingsService();

            // 预填账号：优先当前设备绑定账号，其次当前激活账号
            var prefillAccount = settings.Data.ActiveAccount;
            try
            {
                var devices = new DeviceService(settings.ResolveToolsDir()).ListDevices();
                var boundId = devices.Count > 0 ? settings.Data.GetAccountForDevice(devices[0].Udid) : null;
                if (!string.IsNullOrEmpty(boundId)) prefillAccount = boundId;
            }
            catch { }

            var account = settings.Data.FindAccount(prefillAccount);
            if (account is not null)
            {
                AppleIdBox.Text = account.AppleId;
                var savedPwd = account.GetPassword();
                if (!string.IsNullOrEmpty(savedPwd))
                {
                    PasswordBox.Password = savedPwd;
                    StatusText.Text = $"已自动填入本机保存的账号 {account.AppleId}，点击登录即可（过期签名会自动续期）。";
                    StatusText.Visibility = Visibility.Visible;
                }
            }
            else if (!string.IsNullOrEmpty(settings.Data.AppleId))
            {
                AppleIdBox.Text = settings.Data.AppleId;
            }
            AnisetteUrlBox.Text = settings.Data.AnisetteUrl;
        }
        catch { }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _completed = false;
        Close();
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        var appleId = AppleIdBox.Text.Trim();
        var password = PasswordBox.Password;

        if (!_waitingFor2FA)
        {
            if (string.IsNullOrEmpty(appleId) || string.IsNullOrEmpty(password))
            {
                ShowError("请输入 Apple ID 账号与密码");
                return;
            }

            if (!appleId.Contains("@"))
            {
                ShowError("请输入有效的 Apple ID 邮箱格式");
                return;
            }

            _appleId = appleId;
            _password = password;
            _rememberPassword = RememberPasswordBox.IsChecked == true;

            var settings = new SettingsService();
            _gsaClient = new AppleGsaClient(settings.ResolveToolsDir(), _dataDir, AnisetteUrlBox.Text.Trim());
        }
        else
        {
            var code = TwoFaCodeBox.Text.Trim();
            if (string.IsNullOrEmpty(code))
            {
                ShowError("请输入双重认证验证码");
                return;
            }

            _waitingFor2FA = false;
            TwoFaPanel.Visibility = Visibility.Collapsed;
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;
            StatusText.Text = "正在验证双重认证验证码...";
            StatusText.Visibility = Visibility.Visible;
            LogService.Info("[Setup] 提交双重认证验证码");

            try
            {
                var result = await _gsaClient!.Submit2FACodeAsync(code);
                LogService.Info($"[Setup] 双重认证结果: {result.Status}");

                if (result.Status == AuthStatus.Error)
                {
                    if (_gsaClient?.AdsId != null) SaveCredentials();
                    ShowError(result.Message ?? "双重认证验证码错误，请重试");
                    return;
                }

                StatusText.Text = "双重认证成功！";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
                LogService.Info("[Setup] 双重认证成功，继续后续配置");
                SaveCredentials();

                await Task.Delay(500);
                await HandlePostLoginAsync();
                return;
            }
            catch (Exception ex)
            {
                LogService.Error($"[Setup] 双重认证异常: {ex}");
                ShowError($"双重认证验证失败: {ex.Message}");
                return;
            }
            finally
            {
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                SubmitBtn.IsEnabled = true;
            }
        }

        // 初始登录流程
        try
        {
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;

            StatusText.Text = "正在检测连接的 iOS 设备...";
            StatusText.Visibility = Visibility.Visible;
            LogService.Info("[Setup] 步骤 1: 检测设备");

            var settings = new SettingsService();
            var deviceService = new DeviceService(settings.ResolveToolsDir());
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (devices.Count == 0)
            {
                ShowError("未检测到连接的 iOS 设备，请通过 USB 连接 iPhone 并点击“信任此电脑”");
                return;
            }

            LogService.Info($"[Setup] 检测到设备: {devices[0].Name} ({devices[0].Udid})");
            StatusText.Text = $"已检测到设备: {devices[0].Name}";

            StatusText.Text = "正在登录 Apple ID...";
            LogService.Info("[Setup] 步骤 2: 登录 Apple ID");

            var authResult = await _gsaClient!.AuthenticateAsync(_appleId, _password);
            LogService.Info($"[Setup] 登录结果: {authResult.Status}");

            if (authResult.Status == AuthStatus.Error)
            {
                // authResult.Message 里已经带上 Apple 的原始返回；不要再笼统地
                // 让用户去怀疑自己输对了密码。
                // AdsId 非空 = spd 已成功解密 = 密码确实是对的，只是后面换 Xcode
                // 令牌那一步被苹果拒了。这种情况下照样落盘，勾选的「记住密码」
                // 才不会因为一次令牌失败而丢失。
                if (_gsaClient?.AdsId != null) SaveCredentials();
                ShowError(authResult.Message);
                return;
            }

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                LogService.Info("[Setup] 需要双重认证验证码");
                _waitingFor2FA = true;
                TwoFaPanel.Visibility = Visibility.Visible;
                TwoFaCodeBox.Focus();
                TwoFaCodeBox.SelectAll();
                StatusText.Visibility = Visibility.Collapsed;
                SubmitBtn.Content = "验证并继续";
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                SubmitBtn.IsEnabled = true;
                return;
            }

            // 登录成功，直接进入自动签名配置
            LogService.Info("[Setup] Apple ID 认证成功，进入自动配置");
            SaveCredentials();
            await HandlePostLoginAsync();
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] 异常: {ex}");
            ShowError($"登录遇到错误: {ex.Message}");
        }
    }

    private async Task HandlePostLoginAsync()
    {
        StatusText.Text = "Apple ID 认证成功，正在配置签名环境...";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
        StatusText.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;

        try
        {
            var settings = new SettingsService();
            var deviceService = new DeviceService(settings.ResolveToolsDir());
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (_gsaClient != null && devices.Count > 0)
            {
                StatusText.Text = "正在自动申请免费开发者证书与描述文件...";
                LogService.Info("[Setup] 自动向苹果开发者服务申请证书与描述文件...");

                var dev = devices[0];
                var provisionService = new AppleProvisionService(_gsaClient, _dataDir);
                provisionService.Progress += msg => Dispatcher.Invoke(() => StatusText.Text = msg);

                var bundleId = "com.selfassigned.altserver";
                var appName = "AltServer";
                // Apple 开发者门户要求 name 不能为空：当 ideviceinfo 未能获取设备名时用 fallback
                var deviceName = string.IsNullOrWhiteSpace(dev.Name)
                    ? $"MyDevice-{dev.Udid.Replace("-", "")[^8..]}"
                    : dev.Name;
                var provResult = await provisionService.AutoProvisionAsync(bundleId, appName, deviceName, dev.Udid);

                if (provResult.Success && !string.IsNullOrEmpty(provResult.P12Path) && !string.IsNullOrEmpty(provResult.ProvisionPath))
                {
                    LogService.Success("[Setup] 自动获取证书与描述文件成功！");
                    ResultP12Path = provResult.P12Path;
                    ResultProvisionPath = provResult.ProvisionPath;
                    ResultP12Password = "temp123";
                    ResultAppleId = _appleId;

                    // 多账号管理（v1.0.86）：登记/更新账号，记住密码（DPAPI 加密），绑定设备
                    var typedUrl = AnisetteUrlBox.Text.Trim();
                    if (!string.IsNullOrEmpty(typedUrl)) settings.Data.AnisetteUrl = typedUrl;
                    var account = settings.Data.GetOrCreateAccount(_appleId);
                    account.TeamId = provResult.TeamId;
                    account.LastLoginUtc = DateTime.UtcNow;
                    if (_rememberPassword)
                    {
                        account.SetPassword(_password);
                    }
                    settings.Data.SetDeviceAccount(dev.Udid, _appleId);
                    // 将证书/描述文件存入该设备专属配置（多设备互不覆盖；内部同步更新账号签名材料与有效期）
                    settings.Data.SetDeviceConfig(dev.Udid, provResult.P12Path, "temp123", provResult.ProvisionPath);
                    settings.Data.SetActiveAccount(_appleId);
                    settings.Data.SetupCompleted = true;
                    settings.Save();

                    LogService.Info($"[Setup] 账号 {_appleId} 已保存（记住密码: {_rememberPassword}），设备 {dev.DisplayName} 已绑定该账号");

                    _completed = true;
                    StatusText.Text = "全自动签名配置完成！";
                    await Task.Delay(1000);
                    Close();
                    return;
                }
                else
                {
                    LogService.Warning($"[Setup] 自动配置失败: {provResult.ErrorMessage}");
                    ShowError($"自动签名配置未完成。\n苹果返回: {provResult.ErrorMessage}\n\n" +
                              "运行日志（菜单「运行日志」）里有 [GSA] apptokens 一行的完整错误码，" +
                              "那才是本次失败的真正原因。");
                    return;
                }
            }
            else
            {
                ShowError("未检测到设备或 Apple ID 会话无效");
                return;
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] 自动配置异常: {ex}");
            ShowError($"自动配置异常: {ex.Message}");
        }
    }

    /// <summary>
    /// Apple ID 会话一旦建立就立刻落盘账号、Anisette 地址和勾选的密码。
    /// 以前只有整条自动配置全部跑完才 settings.Save()，apptokens 一失败连密码一起丢，
    /// 「记住密码」形同虚设，描述文件到期时也没有可用凭据做自动续期。
    /// </summary>
    private void SaveCredentials()
    {
        if (string.IsNullOrEmpty(_appleId)) return;
        try
        {
            var settings = new SettingsService();
            // 留空表示“用内置的 Mac Anisette 服务器”，不要把已保存的自定义地址抹掉
            var typedUrl = AnisetteUrlBox.Text.Trim();
            if (!string.IsNullOrEmpty(typedUrl)) settings.Data.AnisetteUrl = typedUrl;
            var account = settings.Data.GetOrCreateAccount(_appleId);
            if (_rememberPassword && !string.IsNullOrEmpty(_password))
                account.SetPassword(_password);
            account.LastLoginUtc = DateTime.UtcNow;
            settings.Save();
            LogService.Info($"[Setup] 账号 {_appleId} 已保存（记住密码: {_rememberPassword}），后续可自动续期");
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] 保存账号凭据失败: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"错误: {message}";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        StatusText.Visibility = Visibility.Visible;
        SubmitBtn.IsEnabled = true;
        SubmitBtn.Content = _waitingFor2FA ? "验证并继续" : "登录并自动配置";
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_completed)
        {
            ResultP12Path = null;
            ResultProvisionPath = null;
            ResultP12Password = null;
            ResultAppleId = null;
        }
    }
}
