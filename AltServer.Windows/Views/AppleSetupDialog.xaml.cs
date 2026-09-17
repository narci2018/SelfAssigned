using Microsoft.Win32;
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
            _gsaClient = new AppleGsaClient(settings.ResolveToolsDir(), _dataDir, AnisetteUrlBox.Text.Trim());
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
            TwoFaPanel.Visibility = Visibility.Collapsed;
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;
            StatusText.Text = "正在验证双重认证...";
            StatusText.Visibility = Visibility.Visible;
            LogService.Info("[Setup] 提交 2FA 验证码");

            try
            {
                var result = await _gsaClient!.Submit2FACodeAsync(code);
                LogService.Info($"[Setup] 2FA 结果: {result.Status}");

                if (result.Status == AuthStatus.Error)
                {
                    ShowError(result.Message ?? "2FA 验证码错误，请重试");
                    return;
                }

                StatusText.Text = "2FA 验证成功！";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
                LogService.Info("[Setup] 2FA 验证成功");

                await Task.Delay(800);
                ShowCertSelection();
                return;
            }
            catch (Exception ex)
            {
                LogService.Error($"[Setup] 2FA 异常: {ex}");
                ShowError($"2FA 验证异常: {ex.Message}");
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

            StatusText.Text = "正在检测设备...";
            StatusText.Visibility = Visibility.Visible;
            LogService.Info("[Setup] 步骤1: 检测设备");

            var settings = new SettingsService();
            var deviceService = new DeviceService(settings.ResolveToolsDir());
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (devices.Count == 0)
            {
                ShowError("未检测到设备，请用 USB 连接 iPhone 并信任此电脑");
                return;
            }

            LogService.Info($"[Setup] 检测到设备: {devices[0].Name}");
            StatusText.Text = $"已检测到设备: {devices[0].Name}";

            StatusText.Text = "正在登录 Apple ID...";
            LogService.Info("[Setup] 步骤2: 登录 Apple ID");

            var authResult = await _gsaClient!.AuthenticateAsync(_appleId, _password);
            LogService.Info($"[Setup] 认证结果: {authResult.Status}");

            if (authResult.Status == AuthStatus.Error)
            {
                ShowError($"{authResult.Message}\n\n请检查:\n1. Apple ID 和密码是否正确\n2. 网络连接是否正常");
                return;
            }

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                LogService.Info("[Setup] 需要 2FA");
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

            // 认证成功，显示证书选择
            LogService.Info("[Setup] 认证成功，显示证书选择界面");
            ShowCertSelection();
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] 异常: {ex}");
            ShowError($"意外错误: {ex.Message}");
        }
    }

    private void OnCompleteConfig(object sender, RoutedEventArgs e)
    {
        var p12Path = P12PathBox.Text.Trim();
        var provPath = ProvisionPathBox.Text.Trim();

        if (string.IsNullOrEmpty(p12Path) && string.IsNullOrEmpty(provPath))
        {
            ShowError("请至少选择一个证书文件或配置文件");
            return;
        }

        var settings = new SettingsService();
        settings.Data.AppleId = _appleId;
        settings.Data.AnisetteUrl = AnisetteUrlBox.Text.Trim();

        if (!string.IsNullOrEmpty(p12Path))
        {
            settings.Data.P12Path = p12Path;
            ResultP12Path = p12Path;
        }
        if (!string.IsNullOrEmpty(provPath))
        {
            settings.Data.MobileProvisionPath = provPath;
            ResultProvisionPath = provPath;
        }

        settings.Data.SetupCompleted = true;
        settings.Save();

        _completed = true;
        LogService.Success("[Setup] 配置完成");
        Close();
    }

    private void ShowCertSelection()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        CertPanel.Visibility = Visibility.Visible;
        SubmitBtn.Content = "完成配置";
        StatusText.Visibility = Visibility.Collapsed;
        SubmitBtn.IsEnabled = true;

        AuthStatusBorder.Visibility = Visibility.Visible;
        AuthStatusText.Text = "✓ Apple ID 登录成功！";
        AuthStatusHint.Text = "请提供代码签名证书 (.p12) 和配置文件 (.mobileprovision)，或稍后在设置中配置";
    }

    private void OnBrowseP12(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择证书文件",
            Filter = "证书文件 (*.p12)|*.p12|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            P12PathBox.Text = dlg.FileName;
        }
    }

    private void OnBrowseProvision(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择配置文件",
            Filter = "配置文件 (*.mobileprovision)|*.mobileprovision|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            ProvisionPathBox.Text = dlg.FileName;
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"❌ {message}";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        StatusText.Visibility = Visibility.Visible;
        SubmitBtn.IsEnabled = true;
        SubmitBtn.Content = _waitingFor2FA ? "验证并继续" : "登录";
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
