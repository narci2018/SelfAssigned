using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using AltServer.Windows.Services;
using AltServer.Windows.ViewModels;
using Microsoft.Win32;

namespace AltServer.Windows.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private NotifyIcon? _trayIcon;
    private bool _forceExit;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();

        // 注意：DataContext 由 App.xaml.cs 在构造函数返回后才赋值，
        // 所以 ViewModel 相关操作必须放在 Loaded 事件里（此时 DataContext 已就绪）
        Loaded += (_, _) =>
        {
            // 订阅工具缺失事件：工具不全时弹出明确提示，不静默失败
            ViewModel.MissingToolsDetected += OnMissingTools;

            // 订阅签名过期提醒：托盘气泡（24小时内最多一次，由 ViewModel 限流）
            ViewModel.ExpiryReminderDetected += OnExpiryReminder;

            // 先检测工具完整性（工具缺失时会弹框提示）
            ViewModel.VerifyTools();

            ViewModel.ScanSigningConfig();
            if (string.IsNullOrEmpty(ViewModel.P12Path) || !System.IO.File.Exists(ViewModel.P12Path))
            {
                ShowSetupWizard();
            }
            else
            {
                ViewModel.CheckExpiryAndRemind();
            }
        };
    }

    private void OnMissingTools(string[] missing, string toolsDir)
    {
        // 确保在 UI 线程执行
        Dispatcher.BeginInvoke(() =>
        {
            var missingNames = string.Join("\n  • ", missing.Select(System.IO.Path.GetFileNameWithoutExtension));
            var msg =
                $"检测到以下必需工具缺失，AltServer 无法正常工作：\n\n  • {missingNames}\n\n" +
                $"工具应放置于：\n  {toolsDir}\n\n" +
                $"请通过以下方式获取工具：\n" +
                $"  1. 运行 scripts\\download-windows-tools.ps1 自动下载\n" +
                $"  2. 手动下载 libimobiledevice-win32：\n" +
                $"     https://github.com/libimobiledevice-win32/imobiledevice-net/releases\n" +
                $"  3. 手动下载 zsign：\n" +
                $"     https://github.com/zhlynn/zsign/releases\n\n" +
                $"下载后将 exe 文件放入上述工具目录，然后点击 [重新检测工具] 按钮。";

            System.Windows.MessageBox.Show(
                msg,
                "缺少必需工具",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        });
    }

    private void ShowSetupWizard()
    {
        var dialog = new AppleSetupDialog(AppContext.BaseDirectory)
        {
            Owner = this
        };

        dialog.ShowDialog();

        if (dialog.ResultAppleId is not null)
        {
            ViewModel.AppleId = dialog.ResultAppleId;
        }

        if (dialog.ResultP12Path is not null)
        {
            ViewModel.P12Path = dialog.ResultP12Path;
            ViewModel.P12Password = dialog.ResultP12Password ?? "temp123";
        }

        if (dialog.ResultProvisionPath is not null)
        {
            ViewModel.MobileProvisionPath = dialog.ResultProvisionPath;
        }

        ViewModel.ScanSigningConfig();
        ViewModel.RefreshAccountsView();
        ViewModel.UpdateDeviceAccountLabels();
        ViewModel.CheckExpiryAndRemind();
    }

    // MARK: - 账号管理（v1.0.86）

    private void OnSetActiveAccount(object sender, RoutedEventArgs e)
    {
        var acct = ViewModel.SelectedAccount;
        if (acct is null)
        {
            Services.LogService.Warning("请先在账号列表中选择要启用的账号");
            return;
        }
        ViewModel.SetActiveAccount(acct.AppleId);
    }

    private void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        var acct = ViewModel.SelectedAccount;
        if (acct is null)
        {
            Services.LogService.Warning("请先在账号列表中选择要移除的账号");
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定移除账号 {acct.AppleId} 吗？\n\n其名下设备将被解绑（签名文件保留在磁盘，设备仍可用旧文件签名）。",
            "移除账号",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        ViewModel.RemoveAccount(acct.AppleId);
    }

    // MARK: - IPA 库（v1.0.86）

    private void OnAddIpa(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入 IPA 库的文件",
            Filter = "IPA文件 (*.ipa)|*.ipa|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;

        var added = ViewModel.AddIpaToLibrary(dlg.FileNames);
        if (added == 0)
        {
            Services.LogService.Warning("未添加任何 IPA 文件");
        }
    }

    private void OnRemoveIpa(object sender, RoutedEventArgs e)
    {
        var item = ViewModel.SelectedIpa;
        if (item is null)
        {
            Services.LogService.Warning("请先在 IPA 库中选择要移除的文件");
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定从 IPA 库移除「{item.DisplayName}」吗？\n库内文件将被一并删除。",
            "移除 IPA",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        ViewModel.RemoveIpaFromLibrary(item);
    }

    private async void OnInstallFromLibrary(object sender, RoutedEventArgs e)
    {
        var item = ViewModel.SelectedIpa;
        if (item is null)
        {
            Services.LogService.Warning("请先在 IPA 库中选择要安装的 IPA");
            return;
        }
        if (ViewModel.SelectedDevice is null)
        {
            Services.LogService.Warning("请先在左侧选择目标设备");
            return;
        }

        var path = ViewModel.GetLibraryIpaPath(item);
        if (path is null)
        {
            Services.LogService.Error($"IPA 文件已丢失: {item.FileName}，请先移除后重新添加");
            return;
        }

        IsEnabled = false;
        try
        {
            await ViewModel.SignAndInstallAsync(path);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnIpaLibraryDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        OnInstallFromLibrary(sender, e);
    }

    // MARK: - 过期提醒（v1.0.86）

    private void OnExpiryReminder(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _trayIcon?.ShowBalloonTip(8000, "AltServer 签名过期提醒", message, ToolTipIcon.Warning);
            }
            catch { }
        });
    }

    // MARK: - 系统托盘

    private void InitializeTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "AltServer - iOS应用侧载服务器",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? System.Drawing.SystemIcons.Application,
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("-");
        menu.Items.Add("刷新签名", null, async (_, _) => await ViewModel.RefreshAllAsync());
        menu.Items.Add("退出 AltServer", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        Closing += OnClosingOverride;
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ViewModel.IsVisible = true;
    }

    private void OnMinimizeToTray(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    internal void MinimizeToTray()
    {
        var settings = new SettingsService();
        if (!settings.Data.MinimizeToTray)
        {
            WindowState = WindowState.Minimized;
            ViewModel.IsVisible = false;
            return;
        }

        Hide();
        ViewModel.IsVisible = false;
    }

    private void OnClosingOverride(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceExit)
        {
            e.Cancel = true;
            MinimizeToTray();
        }
    }

    private void ExitApplication()
    {
        _forceExit = true;
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // MARK: - 事件处理

    private async void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        await Task.Run(() => vm.RefreshDevices());
    }

    private void OnRefreshTools(object sender, RoutedEventArgs e)
    {
        var allOk = ViewModel.VerifyTools();
        if (allOk)
        {
            System.Windows.MessageBox.Show(
                $"所有工具已就绪！\n\n工具目录：{ViewModel.ToolsStatus.Replace("工具完整 ✓ ", "")}",
                "工具检测",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        // 工具缺失时由 MissingToolsDetected 事件自动弹框
    }

    private void OnOpenAppleLogin(object sender, RoutedEventArgs e)
    {
        ShowSetupWizard();
    }

    private async void OnInstallIpa(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择IPA文件",
            Filter = "IPA文件 (*.ipa)|*.ipa|所有文件 (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        if (ViewModel.SelectedDevice is null)
        {
            Services.LogService.Warning("请先在左侧选择目标设备");
            return;
        }

        IsEnabled = false;
        try
        {
            await ViewModel.SignAndInstallAsync(dlg.FileName);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnPairDevice(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDevice is null)
        {
            Services.LogService.Warning("请先在左侧选择目标设备");
            return;
        }

        await ViewModel.PairDeviceAsync();
    }

    private void OnRefreshSigning(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshAllAsync();
    }

    // MARK: - 投屏与照片（v1.0.87）

    // MARK: - 仿爱思助手导航与视图切换

    private void UpdateViewVisibility(string nav)
    {
        ViewModel.CurrentNavView = nav;

        ViewDeviceInfo.Visibility = nav == "device_info" ? Visibility.Visible : Visibility.Collapsed;
        ViewApps.Visibility = nav == "apps" ? Visibility.Visible : Visibility.Collapsed;
        ViewSignInstall.Visibility = nav == "sign_install" ? Visibility.Visible : Visibility.Collapsed;
        ViewLogs.Visibility = nav == "logs" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavToDeviceInfo(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateViewVisibility("device_info");
        _ = ViewModel.RefreshSelectedDeviceDetailAsync();
    }

    private void OnNavToPhotos(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewModel.OpenPhotosWindow();
    }

    private void OnNavToApps(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateViewVisibility("apps");
        ViewModel.LoadDeviceApp();
    }

    private void OnNavToMirror(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewModel.OpenMirrorWindow();
    }

    private void OnNavToFiles(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewModel.OpenFileSystemWindow();
    }

    private void OnNavToSignInstall(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateViewVisibility("sign_install");
    }

    private void OnNavToLogs(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UpdateViewVisibility("logs");
    }

    private void OnRefreshDeviceInfo(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshSelectedDeviceDetailAsync(forceRefresh: true);
    }

    private void OnRefreshApps(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadDeviceApp(forceRefresh: true);
    }

    private async void OnUninstallAppClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string bundleId && !string.IsNullOrEmpty(bundleId))
        {
            var app = ViewModel.InstalledApps.FirstOrDefault(a => a.BundleIdentifier == bundleId);
            var appName = app?.Name ?? bundleId;

            var confirm = System.Windows.MessageBox.Show(
                $"确定要从设备上卸载应用「{appName}」({bundleId}) 吗？此操作无法撤销。",
                "确认卸载",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            var udid = ViewModel.SelectedDevice?.Udid;
            if (string.IsNullOrEmpty(udid))
            {
                System.Windows.MessageBox.Show("未选中设备", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                btn.IsEnabled = false;
                btn.Content = "卸载中...";
                var toolsDir = new SettingsService().ResolveToolsDir();
                var devService = new DeviceService(toolsDir);
                await Task.Run(() => devService.UninstallApp(udid, bundleId));
                System.Windows.MessageBox.Show($"应用「{appName}」已成功卸载！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                ViewModel.LoadDeviceApp();
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "卸载";
            }
        }
    }

    private void OnJumpToSignInstall(object sender, RoutedEventArgs e)
    {
        UpdateViewVisibility("sign_install");
    }

    private void OnMirrorPreview(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenMirrorWindow();
    }

    private void OnPhotos(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenPhotosWindow();
    }

    private void OnDeviceDetail(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenDeviceDetailWindow();
    }

    private void OnFileSystem(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenFileSystemWindow();
    }

    private void OnCopyAllLogs(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, ViewModel.LogEntries);
        if (string.IsNullOrEmpty(text)) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                LogService.Info("日志已复制到剪贴板");
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                LogService.Warning($"复制日志失败: {ex.Message}");
                return;
            }
        }
        LogService.Warning("复制日志失败: 剪贴板被其他程序占用");
    }


    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        ViewModel.LogEntries.Clear();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized)
        {
            MinimizeToTray();
        }
    }
}