using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AltServer.Windows.Models;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

/// <summary>
/// 实时投屏预览窗口（v1.0.87）：通过 screenshotr 服务高频截取设备画面并渲染。
/// 这是演示/监控级的截图流预览（约 2-5 FPS），非 60FPS 真投屏。
/// </summary>
public partial class MirrorWindow : Window
{
    private readonly Device _device;
    private DeviceNative.IdeviceSession? _session;
    private DeviceNative.ScreenshotrClient? _client;

    private CancellationTokenSource? _cts;
    private byte[]? _lastPng;
    private readonly Stopwatch _fpsWatch = new();
    private int _frames;
    private double _fps;
    private int _lastPixelWidth;
    private int _lastPixelHeight;
    private bool _sessionBroken;

    public MirrorWindow(Device device)
    {
        InitializeComponent();
        _device = device;
        Title = $"实时投屏预览 - {device.DisplayName}";
        TopmostBox.IsChecked = false;
        Loaded += OnLoaded;
        Closed += OnWindowClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => CaptureLoopAsync(_cts.Token));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _client?.Dispose();
        _session?.Dispose();
        _client = null;
        _session = null;
    }

    /// <summary>确保截图客户端可用；设备中途断开/会话失效时自动重建一次</summary>
    private DeviceNative.ScreenshotrClient EnsureClient()
    {
        if (_client is not null && !_sessionBroken) return _client;

        _client?.Dispose();
        _session?.Dispose();
        _session = new DeviceNative.IdeviceSession(_device.Udid);
        _client = _session.CreateScreenshotrClient();
        _sessionBroken = false;
        return _client;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (AutoRefreshBox.IsChecked != true)
            {
                await Task.Delay(120, ct);
                continue;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var client = EnsureClient();
                var png = await Task.Run(client.TakeScreenshot, ct);
                _sessionBroken = false;
                RenderFrame(png);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 截图失败常见原因：设备锁屏、USB 断开、配对失效
                _sessionBroken = true;
                Dispatcher.BeginInvoke(() =>
                {
                    PlaceholderText.Text = "画面暂不可用";
                    var tip = ex.Message.Contains("-27")
                        ? "（iOS 需要设备开启「开发者模式」并挂载 DeveloperDiskImage 调试镜像才能启用原生截屏服务）"
                        : "（请确认设备已解锁、已在手机上点击「信任此电脑」，将自动重试）";
                    StatusText.Text = $"截屏失败: {ex.Message} {tip}";
                });
                await Task.Delay(2500, ct);
            }

            // 目标帧间隔 250ms（约 4 FPS），截图本身耗时已计入
            var remaining = 250 - (int)sw.ElapsedMilliseconds;
            if (remaining > 0) await Task.Delay(remaining, ct);
        }
    }

    private void RenderFrame(byte[] png)
    {
        var bitmap = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        _lastPng = png;
        _lastPixelWidth = bitmap.PixelWidth;
        _lastPixelHeight = bitmap.PixelHeight;

        // FPS 统计（每秒窗口）
        _frames++;
        if (!_fpsWatch.IsRunning) _fpsWatch.Start();
        if (_fpsWatch.ElapsedMilliseconds >= 1000)
        {
            _fps = _frames * 1000.0 / _fpsWatch.ElapsedMilliseconds;
            _frames = 0;
            _fpsWatch.Restart();
        }

        Dispatcher.BeginInvoke(() =>
        {
            PlaceholderText.Visibility = Visibility.Collapsed;
            ScreenImage.Source = bitmap;
            StatusText.Text = $"{_device.DisplayName} · {_lastPixelWidth}x{_lastPixelHeight} · {_fps:F1} FPS · 截图流预览（约2-5FPS）";
        });
    }

    private void OnAutoRefreshChanged(object sender, RoutedEventArgs e) { }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostBox.IsChecked == true;
    }

    private async void OnRefreshOnce(object sender, RoutedEventArgs e)
    {
        try
        {
            var client = EnsureClient();
            var png = await Task.Run(client.TakeScreenshot);
            _sessionBroken = false;
            RenderFrame(png);
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Contains("-27")
                ? "iOS 限制：底层截屏服务 (screenshotr) 需要设备开启「开发者模式」并挂载 DeveloperDiskImage 调试镜像才能启动。\n若未配置开发者调试环境，建议使用 iOS 控制中心的「屏幕镜像」(AirPlay) 进行投屏。"
                : "请确认设备屏幕已解锁、已在屏幕上点击「信任此电脑」且 USB 连接稳定。";
            System.Windows.MessageBox.Show(this, $"截屏失败: {ex.Message}\n\n{detail}", "投屏预览",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnSaveScreenshot(object sender, RoutedEventArgs e)
    {
        if (_lastPng is null)
        {
            System.Windows.MessageBox.Show(this, "还没有可用画面，请等待截图成功后再保存。", "保存截图",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存设备截图",
            Filter = "PNG 图片|*.png",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _lastPng);
            LogService.Success($"[投屏] 截图已保存: {dialog.FileName}");
            StatusText.Text = $"截图已保存: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"保存失败: {ex.Message}", "保存截图", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OnLaunchAirPlayer(object sender, RoutedEventArgs e)
    {
        try
        {
            var airPlayerInstaller = @"C:\Program Files\i4Tools9\extrastools\i4AirPlayer3\i4AirPlayer_v6.2.10_Setup_x64_700016.exe";
            if (File.Exists(airPlayerInstaller))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = airPlayerInstaller,
                    UseShellExecute = true
                });
                LogService.Info("已启动爱思 AirPlayer 投屏组件");
                return;
            }

            var candidates = new[]
            {
                @"C:\Program Files\i4AirPlayer\i4AirPlayer.exe",
                @"C:\Program Files (x86)\i4AirPlayer\i4AirPlayer.exe"
            };

            var exe = candidates.FirstOrDefault(File.Exists);
            if (exe != null)
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                LogService.Info("已启动爱思 AirPlayer");
            }
            else
            {
                System.Windows.MessageBox.Show("未在系统中检测到 AirPlayer，请直接通过爱思助手启动投屏功能。", "投屏组件", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            LogService.Warning($"启动 AirPlayer 失败: {ex.Message}");
        }
    }
}
