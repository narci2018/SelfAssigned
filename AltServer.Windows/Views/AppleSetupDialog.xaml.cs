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
            ShowError("请输入 Apple ID 和密码");
            return;
        }

        if (!appleId.Contains("@"))
        {
            ShowError("请输入有效的 Apple ID (邮箱格式)");
            return;
        }

        try
        {
            SubmitBtn.IsEnabled = false;
            ProgressBar.Visibility = Visibility.Visible;

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

            // 步骤2: 登录 Apple ID (使用 GSA SRP 协议)
            StatusText.Text = "步骤 2/5: 登录 Apple ID (SRP 认证)...";
            var gsaClient = new AppleGsaClient(settings.ResolveToolsDir(), _dataDir);
            var authResult = await gsaClient.AuthenticateAsync(appleId, password);

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                // 提示用户在设备上确认双重认证
                StatusText.Text = "步骤 2/5: 请在设备上确认双重认证...";
                await Task.Delay(2000);
            }
            else if (authResult.Status == AuthStatus.Error)
            {
                ShowError(authResult.Message);
                return;
            }

            // 步骤3: 注册设备
            StatusText.Text = "步骤 3/5: 注册设备到 Apple Developer...";
            var registered = await gsaClient.RegisterDeviceAsync(
                devices[0].Udid,
                devices[0].Name);

            if (!registered)
            {
                ShowError("设备注册失败");
                return;
            }

            // 步骤4: 创建证书
            StatusText.Text = "步骤 4/5: 创建代码签名证书...";
            var bundleId = $"com.altserver.{DateTime.Now:yyyyMMdd}";
            var certResult = await gsaClient.CreateCertificateAsync(devices[0].Udid, bundleId);

            if (certResult == null)
            {
                ShowError("证书创建失败");
                return;
            }

            // 步骤5: 保存配置
            StatusText.Text = "步骤 5/5: 保存配置...";
            settings.Data.AppleId = appleId;
            settings.Data.P12Path = certResult.Value.P12Path;
            settings.Data.P12Password = certResult.Value.Password;
            settings.Data.MobileProvisionPath = certResult.Value.ProvisionPath;
            settings.Save();

            ResultP12Path = certResult.Value.P12Path;
            ResultProvisionPath = certResult.Value.ProvisionPath;
            ResultP12Password = certResult.Value.Password;
            _completed = true;

            StatusText.Text = "✅ 配置完成! 证书和配置文件已自动设置";
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
