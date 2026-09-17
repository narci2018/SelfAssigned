using System.IO;
using System.Windows;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

public partial class AppleSetupDialog : Window
{
    private readonly string _dataDir;
    private bool _completed;
    private bool _waitingFor2FA;
    private AppleProvisionService? _provisionService;
    private string _appleId = string.Empty;
    private string _password = string.Empty;

    public string? ResultP12Path { get; private set; }
    public string? ResultProvisionPath { get; private set; }
    public string? ResultP12Password { get; private set; }

    public AppleSetupDialog(string dataDir)
    {
        InitializeComponent();
        _dataDir = dataDir;

        try
        {
            AnisetteUrlBox.Text = new SettingsService().Data.AnisetteUrl;
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
                ShowError("请输入 Apple ID 和密码");
                return;
            }

            if (!appleId.Contains("@"))
            {
                ShowError("请输入有效的 Apple ID (邮箱格式)");
                return;
            }

            _appleId = appleId;
            _password = password;
            var settings = new SettingsService();
            _provisionService = new AppleProvisionService(settings.ResolveToolsDir(), _dataDir, AnisetteUrlBox.Text.Trim());
        }
        else
        {
            var code = TwoFaCodeBox.Text.Trim();
            if (string.IsNullOrEmpty(code))
            {
                ShowError("请输入 2FA 验证码");
                return;
            }

            _waitingFor2FA = false;
            TwoFaLabel.Visibility = Visibility.Collapsed;
            TwoFaCodeBox.Visibility = Visibility.Collapsed;
            TwoFaHint.Visibility = Visibility.Collapsed;

            try
            {
                SubmitBtn.IsEnabled = false;
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.IsIndeterminate = true;
                StatusText.Text = "正在验证双重认证...";
                LogService.Info("[Setup] 提交 2FA 验证码");

                var result = await _provisionService!.Submit2FACodeAsync(code);
                LogService.Info($"[Setup] 2FA 验证结果: {result.Status}");

                if (result.Status == AuthStatus.Error)
                {
                    ShowError(result.Message ?? "2FA 验证码不正确，请重试");
                    return;
                }

                StatusText.Text = "2FA 验证成功，继续配置...";
                LogService.Info("[Setup] 2FA 验证成功，开始自动配置");
            }
            catch (Exception ex)
            {
                LogService.Error($"[Setup] 2FA 提交异常: {ex}");
                ShowError($"2FA 验证异常: {ex.Message}");
                return;
            }
            finally
            {
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
            }
        }

        try
        {
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;

            // 步骤1: 检测设备
            StatusText.Text = "步骤 1/4: 检测设备...";
            LogService.Info("[Setup] 步骤1: 检测设备");
            var settings = new SettingsService();
            var deviceService = new DeviceService(settings.ResolveToolsDir());
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (devices.Count == 0)
            {
                ShowError("未检测到设备，请用 USB 连接 iPhone 并信任此电脑");
                return;
            }

            StatusText.Text = $"检测到设备: {devices[0].Name} ({devices[0].Udid[..8]}...)";
            LogService.Info($"[Setup] 检测到设备: {devices[0].Name}");

            // 步骤2: 登录 Apple ID
            StatusText.Text = "步骤 2/4: 登录 Apple ID...";
            LogService.Info("[Setup] 步骤2: 登录 Apple ID");

            var authResult = await _provisionService!.SignInAsync(_appleId, _password);
            LogService.Info($"[Setup] 认证结果: {authResult.Status}");

            if (authResult.Status == AuthStatus.Error)
            {
                ShowError($"{authResult.Message}\n\n请检查:\n1. Apple ID 和密码是否正确\n2. 网络连接是否正常\n3. 是否需要开启 VPN");
                return;
            }

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                LogService.Info("[Setup] 需要双重认证，显示 2FA 输入框");
                _waitingFor2FA = true;
                TwoFaLabel.Visibility = Visibility.Visible;
                TwoFaCodeBox.Visibility = Visibility.Visible;
                TwoFaHint.Visibility = Visibility.Visible;
                StatusText.Text = "该 Apple ID 已开启双重认证 (2FA)。\n请在 iPhone/可信设备上点击\"允许\"，然后输入收到的 6 位验证码";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
                SubmitBtn.IsEnabled = true;
                SubmitBtn.Content = "验证并配置";
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                TwoFaCodeBox.Focus();
                TwoFaCodeBox.SelectAll();
                return;
            }

            if (authResult.Status == AuthStatus.RequiresNotification)
            {
                StatusText.Text = "请在 iPhone/可信设备上点击\"允许\"完成验证，然后点击\"继续\"";
                LogService.Info("[Setup] 需要设备通知批准");
                SubmitBtn.IsEnabled = true;
                SubmitBtn.Content = "继续";
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                return;
            }

            if (authResult.Status == AuthStatus.AccountLocked)
            {
                ShowError("该 Apple ID 已被锁定，请访问 iforgot.apple.com 解锁");
                return;
            }

            // 步骤3: 自动创建证书和配置文件
            StatusText.Text = "步骤 3/4: 自动配置签名...";
            LogService.Info("[Setup] 步骤3: 自动创建证书和配置文件");

            var device = devices[0];
            var bundleId = $"com.altserver.{_appleId.Replace("@", "_").Replace(".", "_")}";
            var appName = "AltServer App";

            var provisionResult = await _provisionService.AutoProvisionAsync(
                bundleId, appName, device.Name, device.Udid);

            if (!provisionResult.Success)
            {
                LogService.Error($"[Setup] 自动配置失败: {provisionResult.ErrorMessage}");
                ShowError($"自动配置失败: {provisionResult.ErrorMessage}\n\n请检查 Apple ID 权限后重试");
                return;
            }

            ResultP12Path = provisionResult.P12Path;
            ResultP12Password = "temp123";
            ResultProvisionPath = provisionResult.ProvisionPath;

            settings.Data.AppleId = _appleId;
            settings.Data.P12Path = ResultP12Path!;
            settings.Data.P12Password = ResultP12Password!;
            settings.Data.MobileProvisionPath = ResultProvisionPath!;
            settings.Data.SetupCompleted = true;
            settings.Save();

            // 步骤4: 完成
            StatusText.Text = "步骤 4/4: 完成...";
            LogService.Info("[Setup] 步骤4: 完成");

            _completed = true;
            StatusText.Text = $"✅ 配置成功!\n\n证书: {Path.GetFileName(ResultP12Path!)}\n配置文件: {Path.GetFileName(ResultProvisionPath!)}\n团队: {provisionResult.TeamName}";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
            LogService.Info("[Setup] 配置完成");

            await Task.Delay(2000);
            Close();
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] 未捕获异常: {ex}");
            ShowError($"意外错误: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"❌ {message}";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        SubmitBtn.IsEnabled = true;
        SubmitBtn.Content = _waitingFor2FA ? "验证并配置" : "自动配置";
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
        }
    }
}
