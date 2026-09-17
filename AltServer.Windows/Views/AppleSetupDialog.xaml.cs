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

                await Task.Delay(800);
                ShowCertSelection();
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

            // Auth success, show certificate selection
            LogService.Info("[Setup] Auth success, showing cert selection");
            ShowCertSelection();
        }
        catch (Exception ex)
        {
            LogService.Error($"[Setup] Exception: {ex}");
            ShowError($"Unexpected error: {ex.Message}");
        }
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
        LoginPanel.Visibility = Visibility.Collapsed;
        CertPanel.Visibility = Visibility.Visible;
        SubmitBtn.Content = "Complete Config";
        StatusText.Visibility = Visibility.Collapsed;
        SubmitBtn.IsEnabled = true;
        SubmitBtn.Click += OnCompleteConfig;

        AuthStatusBorder.Visibility = Visibility.Visible;
        AuthStatusText.Text = "Apple ID login successful!";
        AuthStatusHint.Text = "Please provide code signing certificate (.p12) and provisioning profile (.mobileprovision), or configure later in settings";
    }

    private void OnBrowseP12(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
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
        var dlg = new OpenFileDialog
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
