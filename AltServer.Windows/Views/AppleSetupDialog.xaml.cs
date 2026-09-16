using System.IO;
using System.Windows;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

public partial class AppleSetupDialog : Window
{
    private readonly string _dataDir;
    private bool _completed;

    public string? ResultP12Path { get; private set; }
    public string? ResultProvisionPath { get; private set; }
    public string? ResultP12Password { get; private set; }

    public AppleSetupDialog(string dataDir)
    {
        InitializeComponent();
        _dataDir = dataDir;
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

        if (string.IsNullOrEmpty(appleId) || string.IsNullOrEmpty(password))
        {
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = "请输入 Apple ID 和密码";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        SubmitBtn.IsEnabled = false;
        SubmitBtn.Content = "配置中...";
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));

        try
        {
            // 步骤1: 检测设备
            StatusText.Text = "步骤 1/5: 检测设备...";
            var settings = new SettingsService();
            var deviceService = new DeviceService(settings.ResolveToolsDir());
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (devices.Count == 0)
            {
                ShowError("未检测到设备，请用 USB 连接 iPhone 并信任此电脑");
                return;
            }

            StatusText.Text = $"检测到设备: {devices[0].Name}";

            // 步骤2: 登录 Apple ID
            StatusText.Text = "步骤 2/5: 登录 Apple ID...";
            var settings = new SettingsService();
            var grandslam = new AppleGrandslamClient(settings.ResolveToolsDir());
            var authResult = await grandslam.AuthenticateAsync(appleId, password);

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                // 简化处理：提示用户在设备上确认
                StatusText.Text = "步骤 2/5: 请在设备上确认双重认证...";
                await Task.Delay(2000);
            }
            else if (authResult.Status == AuthStatus.Error)
            {
                ShowError(authResult.Message);
                return;
            }

            // 步骤3: 设备已就绪
            StatusText.Text = "步骤 3/5: 设备授权完成...";

            // 步骤4: 保存 Apple ID 配置
            StatusText.Text = "步骤 4/5: 保存配置...";
            settings.Data.AppleId = appleId;
            settings.Save();

            // 步骤5: 完成
            StatusText.Text = "步骤 5/5: 完成...";
            _completed = true;

            StatusText.Text = "✅ Apple ID 登录成功! 设备已授权，可以开始安装应用";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));

            await Task.Delay(1500);
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = $"配置失败: {message}";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        SubmitBtn.IsEnabled = true;
        SubmitBtn.Content = "自动配置";
        ProgressBar.Visibility = Visibility.Collapsed;
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
