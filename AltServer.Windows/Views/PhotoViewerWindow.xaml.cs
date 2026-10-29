using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AltServer.Windows.Services;
using MessageBox = System.Windows.MessageBox;

namespace AltServer.Windows.Views;

/// <summary>
/// 高清图片放大查看与画廊浏览器（支持放大/缩小/适应屏幕/上一张/下一张/直接删除/导出）
/// </summary>
public partial class PhotoViewerWindow : Window
{
    private readonly string _udid;
    private readonly PhotosService _photos;
    private readonly List<PhotosService.MediaFile> _files;
    private int _currentIndex;
    private double _currentZoom = 1.0;

    public event Action<PhotosService.MediaFile>? FileDeleted;

    public PhotoViewerWindow(string udid, PhotosService photos, List<PhotosService.MediaFile> files, int initialIndex)
    {
        InitializeComponent();
        _udid = udid;
        _photos = photos;
        _files = files ?? new();
        _currentIndex = Math.Clamp(initialIndex, 0, Math.Max(0, _files.Count - 1));

        Loaded += async (_, _) => await LoadCurrentImageAsync();
    }

    private async Task LoadCurrentImageAsync()
    {
        if (_currentIndex < 0 || _currentIndex >= _files.Count)
        {
            FileNameText.Text = "无图片";
            IndexText.Text = "";
            FileInfoText.Text = "";
            LargeImage.Source = null;
            LoadingText.Text = "相册为空";
            LoadingText.Visibility = Visibility.Visible;
            return;
        }

        var file = _files[_currentIndex];
        FileNameText.Text = file.Name;
        IndexText.Text = $" ({_currentIndex + 1} / {_files.Count})";
        FileInfoText.Text = $" · {file.SizeText} · {file.Modified:yyyy-MM-dd HH:mm}";
        LoadingText.Text = $"正在从设备读取 {file.Name}...";
        LoadingText.Visibility = Visibility.Visible;
        LargeImage.Source = null;

        // 重置缩放比例
        ResetZoom();

        try
        {
            var bytes = await Task.Run(() => _photos.ReadFileData(_udid, file.AfcPath));
            if (bytes == null || bytes.Length == 0)
            {
                LoadingText.Text = "无法读取该文件数据";
                return;
            }

            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
            }
            bitmap.Freeze();

            LargeImage.Source = bitmap;
            LoadingText.Visibility = Visibility.Collapsed;
            FileInfoText.Text = $" · {bitmap.PixelWidth} × {bitmap.PixelHeight} · {file.SizeText}";
        }
        catch (Exception ex)
        {
            LogService.Warning($"[照片浏览器] 加载大图失败 {file.Name}: {ex.Message}");
            LoadingText.Text = file.IsVideo
                ? "🎬 该项目为视频文件，暂不支持直接播放，请点击右上角「导出」至电脑查看。"
                : $"加载失败: {ex.Message}";
        }
    }

    private void ResetZoom()
    {
        _currentZoom = 1.0;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        ImageScale.ScaleX = _currentZoom;
        ImageScale.ScaleY = _currentZoom;
        ZoomPercentText.Text = $"{_currentZoom * 100:F0}%";
    }

    private async void OnPrevClick(object sender, RoutedEventArgs e) => await NavigateDelta(-1);
    private async void OnNextClick(object sender, RoutedEventArgs e) => await NavigateDelta(1);

    private async Task NavigateDelta(int delta)
    {
        if (_files.Count == 0) return;
        var next = _currentIndex + delta;
        if (next < 0) next = _files.Count - 1;
        else if (next >= _files.Count) next = 0;
        _currentIndex = next;
        await LoadCurrentImageAsync();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        _currentZoom = Math.Min(5.0, _currentZoom * 1.25);
        ApplyZoom();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        _currentZoom = Math.Max(0.2, _currentZoom / 1.25);
        ApplyZoom();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        _currentZoom = 1.0;
        ApplyZoom();
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    private void OnScrollViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Delta > 0)
                _currentZoom = Math.Min(5.0, _currentZoom * 1.15);
            else if (e.Delta < 0)
                _currentZoom = Math.Max(0.2, _currentZoom / 1.15);
            ApplyZoom();
            e.Handled = true;
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _files.Count) return;
        var file = _files[_currentIndex];

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            FileName = file.Name,
            Filter = "原图文件 (*.*)|*.*",
            Title = $"导出 {file.Name} 到电脑"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                var bytes = await Task.Run(() => _photos.ReadFileData(_udid, file.AfcPath));
                if (bytes != null && bytes.Length > 0)
                {
                    await File.WriteAllBytesAsync(sfd.FileName, bytes);
                    MessageBox.Show(this, $"成功保存至: {sfd.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"导出失败: {ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _files.Count) return;
        var file = _files[_currentIndex];

        var confirm = MessageBox.Show(this,
            $"确定要从设备中彻底删除照片「{file.Name}」吗？\n\n此操作不可逆！",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await Task.Run(() => _photos.DeleteFiles(_udid, new[] { file }));
            FileDeleted?.Invoke(file);
            _files.RemoveAt(_currentIndex);

            if (_currentIndex >= _files.Count)
                _currentIndex = Math.Max(0, _files.Count - 1);

            await LoadCurrentImageAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"删除失败: {ex.Message}", "删除错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            await NavigateDelta(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            await NavigateDelta(1);
            e.Handled = true;
        }
        else if (e.Key is Key.OemPlus or Key.Add)
        {
            OnZoomInClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key is Key.OemMinus or Key.Subtract)
        {
            OnZoomOutClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
