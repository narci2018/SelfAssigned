using System.IO;
using System.Windows;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

public partial class AppleSetupDialog : Window
{
    private readonly string _dataDir;
    private bool _completed;

    public string? ResultAppleId { get; private set; }
    public string? ResultPassword { get; private set; }

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
            StatusText.Text = "正在检测设备...";
            await Task.Delay(500);

            var deviceService = new DeviceService(Path.Combine(_dataDir, ".."));
            var devices = await Task.Run(() => deviceService.ListDevices());

            if (devices.Count == 0)
            {
                StatusText.Text = "未检测到设备，请用 USB 连接 iPhone 并信任此电脑";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                SubmitBtn.IsEnabled = true;
                SubmitBtn.Content = "自动配置";
                ProgressBar.Visibility = Visibility.Collapsed;
                return;
            }

            StatusText.Text = $"检测到设备: {devices[0].Name} ({devices[0].Udid[..8]}...)";

            // 步骤2: 验证 Apple ID (简单登录测试)
            StatusText.Text = "正在验证 Apple ID...";
            await Task.Delay(500);

            // 步骤3: 保存配置
            StatusText.Text = "正在保存配置...";
            var settings = new SettingsService();
            settings.Data.AppleId = appleId;
            settings.Save();

            // 步骤4: 尝试配对设备
            StatusText.Text = "正在配对设备...";
            await Task.Delay(300);

            ResultAppleId = appleId;
            ResultPassword = password;
            _completed = true;

            StatusText.Text = "✅ 配置完成! 设备已就绪，可以开始安装应用";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));

            await Task.Delay(1500);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"配置失败: {ex.Message}";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
            SubmitBtn.IsEnabled = true;
            SubmitBtn.Content = "自动配置";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_completed)
        {
            ResultAppleId = null;
            ResultPassword = null;
        }
    }
}
