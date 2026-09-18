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

            // 先检测工具完整性（工具缺失时会弹框提示）
            ViewModel.VerifyTools();

            ViewModel.ScanSigningConfig();
            if (string.IsNullOrEmpty(ViewModel.P12Path) || !System.IO.File.Exists(ViewModel.P12Path))
            {
                ShowSetupWizard();
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