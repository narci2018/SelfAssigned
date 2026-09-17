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

            var gsaClient = new AppleGsaClient(settings.ResolveToolsDir(), _dataDir, AnisetteUrlBox.Text.Trim());
            AuthResult authResult;

            try
            {
                authResult = await gsaClient.AuthenticateAsync(appleId, password);
            }
            catch (Exception ex)
            {
                LogService.Error($"[Setup] 认证异常: {ex}");
                ShowError($"认证异常: {ex.Message}\n\n请检查:\n1. Apple ID 和密码是否正确\n2. 网络连接是否正常\n3. 是否需要开启 VPN\n4. iTunes 和 iCloud 是否已安装");
                return;
            }

            LogService.Info($"[Setup] 认证结果: {authResult.Status}");

            if (authResult.Status == AuthStatus.Error)
            {
                ShowError($"认证失败: {authResult.Message}\n\n请检查日志获取详细信息");
                return;
            }

            if (authResult.Status == AuthStatus.Requires2FA)
            {
                StatusText.Text = "该 Apple ID 已开启双重认证 (2FA)。\n请在 iPhone/其他可信设备上点击\"允许\"完成验证，然后再次点击\"自动配置\"重试。";
                LogService.Info("[Setup] 需要双重认证，GSA 原生 2FA 不可用，请通过 idmsa/可信设备流程完成");
                SubmitBtn.IsEnabled = true;
                SubmitBtn.Content = "自动配置";
                ProgressBar.Visibility = Visibility.Collapsed;
                ProgressBar.IsIndeterminate = false;
                return;
            }

            // 步骤3: 保存配置
            StatusText.Text = "步骤 3/4: 保存配置...";
            LogService.Info("[Setup] 步骤3: 保存配置");
            settings.Data.AppleId = appleId;
            settings.Data.AnisetteUrl = AnisetteUrlBox.Text.Trim();
            settings.Save();

            // 步骤4: 完成
            StatusText.Text = "步骤 4/4: 完成...";
            _completed = true;

            StatusText.Text = "✅ Apple ID 登录成功!\n\n注意: 自动证书创建功能开发中，请手动提供 .p12 和 .mobileprovision 文件";
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
        SubmitBtn.Content = "自动配置";
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
        }
    }
}
