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
    private bool _inCertSelection;
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
        if (_inCertSelection)
        {
            OnCompleteConfig(sender, e);
            return;
        }

        var appleId = AppleIdBox.Text.Trim();
        var password = PasswordBox.Password;

        if (!_waitingFor2FA)
        {
            if (string.IsNullOrEmpty(appleId) || string.IsNullOrEmpty(password))
            {
                ShowError("Please enter Apple ID and password");
                return;
            }

            if (!appleId.Contains("@"))
            {
                ShowError("Please enter a valid Apple ID (email format)");
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
                ShowError("Please enter 2FA code");
                return;
            }

            _waitingFor2FA = false;
            TwoFaPanel.Visibility = Visibility.Collapsed;
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;
            StatusText.Text = "Verifying 2FA code...";
            StatusText.Visibility = Visibility.Visible;
            LogService.Info("[Setup] Submitting 2FA code");

            try
            {
                var result = await _gsaClient!.Submit2FACodeAsync(code);
                LogService.Info($"[Setup] 2FA result: {result.Status}");

                if (result.Status == AuthStatus.Error)
                {
                    ShowError(result.Message ?? "Invalid 2FA code, please retry");
                    return;
                }

                StatusText.Text = "2FA verified!";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
                LogService.Info("[Setup] 2FA verified successfully");

                await Task.Delay(500);
                await HandlePostLoginAsync();
                return;
            }
            catch (Exception ex)
            {
                LogService.Error($"[Setup] 2FA exception: {ex}");
                ShowError($"2FA verification error: {ex.Message}");
                return;
            }
            finally
            {
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                SubmitBtn.IsEnabled = true;
            }
        }

        // Initial login flow
        try
        {
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = true;

            StatusText.Text = "Detecting device...";
            StatusText.Visibility = Visibility.Visible;
            LogService.Info("[Setup] Step 1: Detect device");

            var settings = new SettingsService();
            var deviceService = new DeviceService(settings.ResolveToolsDir());
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (devices.Count == 0)
            {
                ShowError("No device detected. Please connect iPhone via USB and trust this computer");
                return;
            }

            LogService.Info($"[Setup] Device detected: {devices[0].Name}");
            StatusText.Text = $"Device detected: {devices[0].Name}";

            StatusText.Text = "Logging into Apple ID...";
            LogService.Info("[Setup] Step 2: Login to Apple ID");

            var authResult = await _gsaClient!.AuthenticateAsync(_appleId, _password);
            LogService.Info($"[Setup] Auth result: {authResult.Status}");

            if (authResult.Status == AuthStatus.Error)
            {
                ShowError($"{authResult.Message}\n\nPlease check:\n1. Apple ID and password are correct\n2. Network connection is normal");
                return;
            }

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                LogService.Info("[Setup] 2FA required");
                _waitingFor2FA = true;
                TwoFaPanel.Visibility = Visibility.Visible;
                TwoFaCodeBox.Focus();
                TwoFaCodeBox.SelectAll();
                StatusText.Visibility = Visibility.Collapsed;
                SubmitBtn.Content = "Verify and Continue";
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                SubmitBtn.IsEnabled = true;
                return;
            }

            // Auth success, proceed to automated post-login setup
            LogService.Info("[Setup] Auth success, proceeding to post-login setup");
            await HandlePostLoginAsync();
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] Exception: {ex}");
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    private async Task HandlePostLoginAsync()
    {
        StatusText.Text = "登录成功，正在自动配置签名环境...";
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
                LogService.Info("[Setup] 尝试自动申请开发证书与描述文件...");

                var dev = devices[0];
                var provisionService = new AppleProvisionService(_gsaClient, _dataDir);
                provisionService.Progress += msg => Dispatcher.Invoke(() => StatusText.Text = msg);

                var bundleId = "com.selfassigned.altserver";
                var appName = "AltServer";
                var provResult = await provisionService.AutoProvisionAsync(bundleId, appName, dev.Name, dev.Udid);

                if (provResult.Success && !string.IsNullOrEmpty(provResult.P12Path) && !string.IsNullOrEmpty(provResult.ProvisionPath))
                {
                    LogService.Success("[Setup] 自动获取证书与描述文件成功！");
                    ResultP12Path = provResult.P12Path;
                    ResultProvisionPath = provResult.ProvisionPath;

                    settings.Data.AppleId = _appleId;
                    settings.Data.AnisetteUrl = AnisetteUrlBox.Text.Trim();
                    settings.Data.P12Path = provResult.P12Path;
                    settings.Data.MobileProvisionPath = provResult.ProvisionPath;
                    settings.Data.SetupCompleted = true;
                    settings.Save();

                    _completed = true;
                    StatusText.Text = "自动配置完成！";
                    await Task.Delay(1000);
                    Close();
                    return;
                }
                else
                {
                    LogService.Warning($"[Setup] 自动配置提示: {provResult.ErrorMessage}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warning($"[Setup] 自动配置异常: {ex.Message}");
        }

        ShowCertSelection();
    }

    private void OnCompleteConfig(object sender, RoutedEventArgs e)
    {
        var p12Path = P12PathBox.Text.Trim();
        var provPath = ProvisionPathBox.Text.Trim();

        if (string.IsNullOrEmpty(p12Path) && string.IsNullOrEmpty(provPath))
        {
            ShowError("Please select at least one certificate or provisioning file");
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
        LogService.Success("[Setup] Configuration complete");
        Close();
    }

    private void ShowCertSelection()
    {
        _inCertSelection = true;
        LoginPanel.Visibility = Visibility.Collapsed;
        CertPanel.Visibility = Visibility.Visible;
        SubmitBtn.Content = "Complete Config";
        StatusText.Visibility = Visibility.Collapsed;
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
        SubmitBtn.IsEnabled = true;

        AuthStatusBorder.Visibility = Visibility.Visible;
        AuthStatusText.Text = "Apple ID 认证成功！";
        AuthStatusHint.Text = "已验证 Apple ID 身份。若自动生成受限（如账号需同意开发者协议），请在此指定证书与描述文件，或稍后在设置中配置。";

        try
        {
            var discovery = new SigningDiscoveryService();
            var certs = discovery.DiscoverP12Files();
            var storeCerts = discovery.DiscoverCertificates();
            var provisions = discovery.DiscoverProvisionProfiles();

            if (certs.Count > 0 && string.IsNullOrEmpty(P12PathBox.Text))
            {
                P12PathBox.Text = certs[0].Thumbprint;
                LogService.Info($"[Setup] 自动预填本地发现的证书: {certs[0].Subject} ({certs[0].Thumbprint})");
            }
            else if (storeCerts.Count > 0 && string.IsNullOrEmpty(P12PathBox.Text))
            {
                P12PathBox.Text = storeCerts[0].Thumbprint;
                LogService.Info($"[Setup] 自动预填系统证书库证书: {storeCerts[0].Subject} ({storeCerts[0].Thumbprint})");
            }

            if (provisions.Count > 0 && string.IsNullOrEmpty(ProvisionPathBox.Text))
            {
                ProvisionPathBox.Text = provisions[0].Path;
                LogService.Info($"[Setup] 自动预填本地发现的描述文件: {provisions[0].Name} ({provisions[0].Path})");
            }
        }
        catch (Exception ex)
        {
            LogService.Warning($"[Setup] 扫描本地证书配置异常: {ex.Message}");
        }
    }

    private void OnBrowseP12(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Certificate File",
            Filter = "Certificate files (*.p12)|*.p12|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            P12PathBox.Text = dlg.FileName;
        }
    }

    private void OnBrowseProvision(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Provisioning Profile",
            Filter = "Provisioning profiles (*.mobileprovision)|*.mobileprovision|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            ProvisionPathBox.Text = dlg.FileName;
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"ERROR: {message}";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        StatusText.Visibility = Visibility.Visible;
        SubmitBtn.IsEnabled = true;
        SubmitBtn.Content = _waitingFor2FA ? "Verify and Continue" : "Login";
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
