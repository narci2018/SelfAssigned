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
                // 截图失败常见原因：设备锁屏、USB 断开、服务未授权(-27)
                _sessionBroken = true;
                Dispatcher.BeginInvoke(() =>
                {
                    if (ex.Message.Contains("-27"))
                    {
                        AirPlayGuidePanel.Visibility = Visibility.Visible;
                        StatusText.Text = "USB底层截屏受限(-27): iOS 17/18 需开启「开发者模式」或使用控制中心「屏幕镜像」。";
                    }
                    else
                    {
                        StatusText.Text = $"截屏重试中: {ex.Message}";
                    }
                });
                await Task.Delay(3000, ct);
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
            AirPlayGuidePanel.Visibility = Visibility.Collapsed;
            ScreenImage.Source = bitmap;
            StatusText.Text = $"{_device.DisplayName} · {_lastPixelWidth}x{_lastPixelHeight} · {_fps:F1} FPS";
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
            _sessionBroken = true;
            if (ex.Message.Contains("-27"))
            {
                AirPlayGuidePanel.Visibility = Visibility.Visible;
                StatusText.Text = "USB底层截屏受限(-27)，请点击窗口中「启动 AirPlay 投屏」进行无线投屏。";
            }
            else
            {
                StatusText.Text = $"刷新失败: {ex.Message}";
            }
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

    private void OnCopyScreenshot(object sender, RoutedEventArgs e)
    {
        if (_lastPng is null || _lastPng.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "当前暂无画面可复制。", "提示", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(_lastPng))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            System.Windows.Clipboard.SetImage(bitmap);
            StatusText.Text = "画面已复制到剪贴板！可直接粘贴(Ctrl+V)。";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"复制失败: {ex.Message}", "复制错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
