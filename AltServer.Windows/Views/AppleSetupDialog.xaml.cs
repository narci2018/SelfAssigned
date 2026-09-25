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

    public AppleSetupDialog(string dataDir)
    {
        InitializeComponent();
        _dataDir = dataDir;

        try
        {
            var settings = new SettingsService();
            if (!string.IsNullOrEmpty(settings.Data.AppleId))
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
                    ShowError(result.Message ?? "双重认证验证码错误，请重试");
                    return;
                }

                StatusText.Text = "双重认证成功！";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
                LogService.Info("[Setup] 双重认证成功，继续后续配置");

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
                ShowError($"{authResult.Message}\n请检查：\n1. Apple ID 与密码是否正确\n2. 网络连接是否畅通");
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

                    settings.Data.AppleId = _appleId;
                    settings.Data.AnisetteUrl = AnisetteUrlBox.Text.Trim();
                    // 将证书/描述文件存入该设备专属配置（多设备互不覆盖）
                    settings.Data.SetDeviceConfig(dev.Udid, provResult.P12Path, "temp123", provResult.ProvisionPath);
                    settings.Data.SetupCompleted = true;
                    settings.Save();

                    _completed = true;
                    StatusText.Text = "全自动签名配置完成！";
                    await Task.Delay(1000);
                    Close();
                    return;
                }
                else
                {
                    LogService.Warning($"[Setup] 自动配置提示: {provResult.ErrorMessage}");
                    ShowError($"自动签名配置失败: {provResult.ErrorMessage}");
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
