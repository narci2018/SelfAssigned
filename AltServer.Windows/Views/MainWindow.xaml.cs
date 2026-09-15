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
            var settings = new SettingsService();
            if (!settings.Data.SetupCompleted && string.IsNullOrEmpty(settings.Data.P12Path))
            {
                ShowSetupWizard(settings);
            }
            else
            {
                ViewModel.ScanSigningConfig();
            }
        };
    }

    private void ShowSetupWizard(SettingsService settings)
    {
        var dialog = new AppleSetupDialog(AppContext.BaseDirectory)
        {
            Owner = this
        };

        dialog.ShowDialog();

        if (dialog.ResultAppleId is not null)
        {
            settings.Data.AppleId = dialog.ResultAppleId;
        }

        settings.Data.SetupCompleted = true;
        settings.Save();

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

    private void OnBrowseP12(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择证书文件 (.p12)",
            Filter = "证书文件 (*.p12)|*.p12|所有文件 (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            _p12LastSetPath = dlg.FileName;
            ViewModel.P12Path = dlg.FileName;
        }
    }

    private string? _p12LastSetPath;

    private void OnBrowseProvision(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择配置文件 (.mobileprovision)",
            Filter = "配置文件 (*.mobileprovision)|*.mobileprovision|所有文件 (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            ViewModel.MobileProvisionPath = dlg.FileName;
        }
    }

    private void OnP12PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.P12Password = P12PasswordBox.Password;
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
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
            LogService.Info("日志已复制到剪贴板");
        }
    }

    private void OnScanSigningConfig(object sender, RoutedEventArgs e)
    {
        ViewModel.ScanSigningConfig();
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