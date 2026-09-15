using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AltServer.Windows.ViewModels;
using AltServer.Windows.Views;

namespace AltServer.Windows;

public partial class App : System.Windows.Application
{
    private MainViewModel? _viewModel;
    private Mutex? _instanceMutex;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 单实例检测
        _instanceMutex = new Mutex(true, "AltServer.Windows.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("AltServer 已经在运行中。", "AltServer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _viewModel = new MainViewModel();

        var window = new MainWindow
        {
            DataContext = _viewModel
        };

        _viewModel.Start();

        // 开机自启动(--minimized参数)时最小化到托盘
        _viewModel.MinimizeToTrayRequested += () => window.MinimizeToTray();

        // 处理未捕获异常
        DispatcherUnhandledException += (_, args) =>
        {
            Services.LogService.Error("未处理的异常", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Services.LogService.Error("致命异常", ex);
            }
        };
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        try
        {
            _viewModel?.Stop();
        }
        catch
        {
            // 忽略退出时的异常
        }
    }
}