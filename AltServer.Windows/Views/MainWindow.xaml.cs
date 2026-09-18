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

        // 首次启动弹出配置向导
        Loaded += (_, _) =>
        {
            ViewModel.ScanSigningConfig();
            if (string.IsNullOrEmpty(ViewModel.P12Path) || !System.IO.File.Exists(ViewModel.P12Path))
            {
                ShowSetupWizard();
            }
        };
    }

    private void ShowSetupWizard()
    {
        var dialog = new AppleSetupDialog(AppContext.BaseDirectory)
        {
            Owner = this
        };

        dialog.ShowDialog();

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
        ViewModel.VerifyTools();
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