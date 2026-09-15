using System.Windows;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

public partial class AppleSetupDialog : Window
{
    private int _currentStep = 1;
    private readonly AppleProvisionService _provisionService;
    private AuthResult? _authResult;
    private bool _completed;

    public string? ResultP12Path { get; private set; }
    public string? ResultProvisionPath { get; private set; }
    public string? ResultP12Password { get; private set; }

    public AppleSetupDialog(string dataDir)
    {
        InitializeComponent();
        _provisionService = new AppleProvisionService(dataDir);
        _provisionService.Progress += msg => Dispatcher.BeginInvoke(() => ProgressText.Text = msg);
        UpdateStepUI();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _completed = false;
        Close();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateStepUI();
        }
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        NextBtn.IsEnabled = false;

        try
        {
            switch (_currentStep)
            {
                case 1:
                    await HandleStep1Login();
                    break;
                case 2:
                    await HandleStep2FA();
                    break;
                case 3:
                    await HandleStep3Provision();
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            NextBtn.IsEnabled = true;
        }
    }

    private async Task HandleStep1Login()
    {
        var appleId = AppleIdBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrEmpty(appleId) || string.IsNullOrEmpty(password))
        {
            System.Windows.MessageBox.Show("请输入 Apple ID 和密码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NextBtn.Content = "登录中...";
        var result = await _provisionService.SignInAsync(appleId, password);
        _authResult = result;

        switch (result.Status)
        {
            case AuthStatus.Success:
                _currentStep = 3;
                UpdateStepUI();
                break;

            case AuthStatus.Requires2FA:
                _currentStep = 2;
                UpdateStepUI();
                break;

            case AuthStatus.AccountLocked:
                ShowError("Apple ID 已被锁定，请稍后再试或访问 iforgot.apple.com 解锁");
                break;

            case AuthStatus.TooManyAttempts:
                ShowError("验证码尝试次数过多，请稍后再试");
                break;

            default:
                ShowError(result.Message);
                break;
        }

        NextBtn.Content = "下一步";
    }

    private async Task HandleStep2FA()
    {
        var code = CodeBox.Text.Trim().Replace(" ", "");

        if (string.IsNullOrEmpty(code) || code.Length < 6)
        {
            System.Windows.MessageBox.Show("请输入 6 位验证码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NextBtn.Content = "验证中...";
        var result = await _provisionService.Submit2FACodeAsync(code);

        switch (result.Status)
        {
            case AuthStatus.Success:
                _currentStep = 3;
                UpdateStepUI();
                break;

            case AuthStatus.TooManyAttempts:
                ShowError("验证码尝试次数过多，请稍后再试");
                break;

            default:
                ShowError("验证码错误，请重试");
                break;
        }

        NextBtn.Content = "下一步";
    }

    private async Task HandleStep3Provision()
    {
        var appName = AppNameBox.Text.Trim();
        var bundleId = BundleIdBox.Text.Trim();
        var deviceName = DeviceNameBox.Text.Trim();

        if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(bundleId))
        {
            System.Windows.MessageBox.Show("请填写 App 名称和 Bundle ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 显示进度面板
        Step3Panel.Visibility = Visibility.Collapsed;
        Step4Panel.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Visible;
        SuccessBorder.Visibility = Visibility.Collapsed;
        ErrorBorder.Visibility = Visibility.Collapsed;
        SkipBtn.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Collapsed;
        NextBtn.Visibility = Visibility.Collapsed;

        // 获取设备 UDID (从已连接设备)
        var deviceUdid = "00000000-0000000000000000";
        try
        {
            var devices = new DeviceService(AppContext.BaseDirectory).ListDevices();
            if (devices.Count > 0)
            {
                deviceUdid = devices[0].Udid;
                deviceName = devices[0].Name;
            }
        }
        catch { }

        var result = await _provisionService.AutoProvisionAsync(bundleId, appName, deviceName, deviceUdid);

        ProgressBar.Visibility = Visibility.Collapsed;

        if (result.Success)
        {
            ResultP12Path = result.P12Path;
            ResultProvisionPath = result.ProvisionPath;
            ResultP12Password = "temp123";

            SuccessBorder.Visibility = Visibility.Visible;
            ResultText.Text = $"团队: {result.TeamName}\n" +
                              $"证书: {result.P12Path}\n" +
                              $"配置文件: {result.ProvisionPath}\n\n" +
                              "这些文件已保存到程序目录，下次启动时自动加载。";

            _completed = true;

            // 3秒后自动关闭
            await Task.Delay(3000);
            Close();
        }
        else
        {
            ErrorBorder.Visibility = Visibility.Visible;
            ErrorText.Text = result.ErrorMessage;
            BackBtn.Visibility = Visibility.Visible;
            NextBtn.Content = "重试";
            NextBtn.Visibility = Visibility.Visible;
        }
    }

    private void UpdateStepUI()
    {
        Step1Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackBtn.Visibility = _currentStep > 1 && _currentStep < 4 ? Visibility.Visible : Visibility.Collapsed;
        SkipBtn.Visibility = _currentStep < 3 ? Visibility.Visible : Visibility.Collapsed;

        if (_currentStep == 3) NextBtn.Content = "开始配置";
        else if (_currentStep == 2) NextBtn.Content = "验证";
        else NextBtn.Content = "下一步";
    }

    private void ShowError(string message)
    {
        Step4Panel.Visibility = Visibility.Visible;
        ProgressBar.Visibility = Visibility.Collapsed;
        SuccessBorder.Visibility = Visibility.Collapsed;
        ErrorBorder.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        BackBtn.Visibility = Visibility.Visible;
        NextBtn.Visibility = Visibility.Visible;
        NextBtn.Content = "重试";
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_completed)
        {
            ResultP12Path = null;
            ResultProvisionPath = null;
        }
    }
}
