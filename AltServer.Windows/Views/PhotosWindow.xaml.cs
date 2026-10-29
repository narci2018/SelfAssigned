using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AltServer.Windows.Models;
using AltServer.Windows.Services;
using MessageBox = System.Windows.MessageBox;

namespace AltServer.Windows.Views;

/// <summary>
/// 仿爱思助手照片管理窗口（v1.0.90）：支持左侧相簿分类树导航、个人收藏、相机录像、原图批量导出导入
/// </summary>
public partial class PhotosWindow : Window
{
    private readonly Device _device;
    private readonly PhotosService _photos = new();
    private List<PhotosService.AlbumCategory> _albums = new();
    private List<PhotosService.MediaFile> _currentFiles = new();
    private List<DisplayItem> _displayItems = new();
    private CancellationTokenSource? _thumbCts;
    private bool _busy;

    public PhotosWindow(Device device)
    {
        InitializeComponent();
        _device = device;
        DeviceText.Text = $"{device.DisplayName} · {device.Udid}";
        Loaded += async (_, _) => await LoadAlbumsAsync();
        Closed += (_, _) => _thumbCts?.Cancel();
    }

    public sealed class DisplayItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
        public bool IsVideo { get; init; }
        public string IsVideoText => IsVideo ? "视频" : "图片";
        public string PlaceholderIcon => IsVideo ? "🎬" : "🖼";
        public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;
        public PhotosService.MediaFile File { get; init; } = new();

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail != value)
                {
                    _thumbnail = value;
                    OnPropertyChanged(nameof(Thumbnail));
                    OnPropertyChanged(nameof(ThumbnailVisibility));
                }
            }
        }

        public Visibility ThumbnailVisibility => _thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
    }

    private async Task LoadAlbumsAsync()
    {
        if (_busy) return;
        _busy = true;
        SetUiEnabled(false);
        StatusText.Text = "正在解析相册结构与媒体索引...（可能需要几秒）";

        try
        {
            var albums = await Task.Run(() => _photos.LoadAlbums(_device.Udid));
            _albums = albums;

            AlbumsList.ItemsSource = albums;

            var totalCount = albums.FirstOrDefault(a => a.Name is "图库" or "相机胶卷")?.Count
                ?? albums.Sum(a => a.Count);
            AlbumSummaryText.Text = $"共 {albums.Count} 个分类 · {totalCount} 个文件";

            // 默认选中第一个相簿（通常为图库）
            if (albums.Count > 0)
            {
                AlbumsList.SelectedIndex = 0;
            }
            else
            {
                FilesList.ItemsSource = null;
                ThumbnailList.ItemsSource = null;
                StatusText.Text = "设备相册中暂无照片或视频。";
            }

            LogService.Info($"[照片] 已成功解析 {_device.DisplayName} 相册库: {albums.Count} 个分类, 汇总 {totalCount} 个文件");
        }
        catch (Exception ex)
        {
            var hint = "（请确保 iPhone/iPad 屏幕已解锁、并在屏幕上点击了「信任此电脑」；若刚插拔数据线请稍候重试）";
            StatusText.Text = $"读取失败: {ex.Message} {hint}";
            LogService.Error($"[照片] 读取相册失败: {ex.Message}");
        }
        finally
        {
            _busy = false;
            SetUiEnabled(true);
        }
    }

    private void OnAlbumSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AlbumsList.SelectedItem is not PhotosService.AlbumCategory album) return;

        CurrentAlbumTitle.Text = album.Name;
        _currentFiles = album.Files;

        _displayItems = album.Files.Select(f => new DisplayItem
        {
            Name = f.Name,
            SizeText = f.SizeText,
            ModifiedText = f.Modified == DateTime.MinValue ? "—" : f.Modified.ToString("yyyy-MM-dd HH:mm"),
            IsVideo = f.IsVideo,
            File = f
        }).ToList();

        ThumbnailList.ItemsSource = _displayItems;
        FilesList.ItemsSource = _displayItems;

        StatusText.Text = $"{album.Name}: 共 {album.Files.Count} 个项目。双击照片可放大预览；按住 Ctrl/Shift 可多选。";

        // 启动后台异步加载缩略图
        StartThumbnailLoader(_displayItems);
    }

    private void StartThumbnailLoader(List<DisplayItem> items)
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        _ = Task.Run(async () =>
        {
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (item.Thumbnail != null) continue;

                try
                {
                    if (!item.IsVideo)
                    {
                        var bytes = _photos.ReadFileData(_device.Udid, item.File.AfcPath);
                        if (bytes != null && bytes.Length > 0)
                        {
                            var bitmap = new BitmapImage();
                            using (var ms = new MemoryStream(bytes))
                            {
                                bitmap.BeginInit();
                                bitmap.DecodePixelWidth = 160;
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = ms;
                                bitmap.EndInit();
                            }
                            bitmap.Freeze();

                            Dispatcher.Invoke(() => item.Thumbnail = bitmap);
                        }
                    }
                }
                catch { }

                await Task.Delay(25, ct);
            }
        }, ct);
    }

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        if (ThumbnailScroller == null || FilesList == null) return;

        bool isThumb = ThumbnailModeRadio.IsChecked == true;
        ThumbnailScroller.Visibility = isThumb ? Visibility.Visible : Visibility.Collapsed;
        FilesList.Visibility = isThumb ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetUiEnabled(bool enabled)
    {
        ExportSelectedBtn.IsEnabled = enabled;
        ExportAllBtn.IsEnabled = enabled;
        ImportBtn.IsEnabled = enabled;
        DeleteSelectedBtn.IsEnabled = enabled;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAlbumsAsync();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (ThumbnailModeRadio.IsChecked == true)
            ThumbnailList.SelectAll();
        else
            FilesList.SelectAll();
    }

    private List<PhotosService.MediaFile> GetSelectedFiles()
    {
        if (ThumbnailModeRadio.IsChecked == true)
        {
            return ThumbnailList.SelectedItems.Cast<DisplayItem>().Select(d => d.File).ToList();
        }
        return FilesList.SelectedItems.Cast<DisplayItem>().Select(d => d.File).ToList();
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenEnlargedViewer();
    }

    private void OnViewEnlarged(object sender, RoutedEventArgs e)
    {
        OpenEnlargedViewer();
    }

    private void OpenEnlargedViewer()
    {
        var selectedItem = (ThumbnailModeRadio.IsChecked == true
            ? ThumbnailList.SelectedItem
            : FilesList.SelectedItem) as DisplayItem;

        if (selectedItem == null && _displayItems.Count > 0)
            selectedItem = _displayItems[0];

        if (selectedItem == null) return;

        var index = _currentFiles.IndexOf(selectedItem.File);
        if (index < 0) index = 0;

        var viewer = new PhotoViewerWindow(_device.Udid, _photos, _currentFiles, index)
        {
            Owner = this
        };

        viewer.FileDeleted += deletedFile =>
        {
            var match = _displayItems.FirstOrDefault(d => d.File.AfcPath == deletedFile.AfcPath);
            if (match != null)
            {
                _displayItems.Remove(match);
                ThumbnailList.ItemsSource = null;
                ThumbnailList.ItemsSource = _displayItems;
                FilesList.ItemsSource = null;
                FilesList.ItemsSource = _displayItems;
                StatusText.Text = $"已删除 {deletedFile.Name}，当前剩余 {_displayItems.Count} 个文件。";
            }
        };

        viewer.ShowDialog();
    }

    private async void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        var files = GetSelectedFiles();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "请先在列表中选中要删除的照片或视频。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"确定要从设备中彻底删除选中的 {files.Count} 个照片/视频吗？\n\n此操作不可逆！",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        SetUiEnabled(false);
        ProgressBar.Value = 0;

        var progress = new Progress<PhotosService.ProgressInfo>(p =>
        {
            ProgressBar.Value = (double)p.Current / p.Total * 100;
            StatusText.Text = $"正在删除 ({p.Current}/{p.Total}): {p.CurrentFile}";
        });

        try
        {
            await Task.Run(() => _photos.DeleteFiles(_device.Udid, files, progress));
            StatusText.Text = $"成功删除 {files.Count} 个文件！";
            MessageBox.Show(this, $"成功从设备删除 {files.Count} 个文件！", "删除完成", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadAlbumsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除中断: {ex.Message}";
            MessageBox.Show(this, $"删除失败: {ex.Message}", "删除错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            SetUiEnabled(true);
            ProgressBar.Value = 0;
        }
    }

    private async void OnExportSelected(object sender, RoutedEventArgs e)
    {
        var files = GetSelectedFiles();
        if (files.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "请先在列表中选择要导出的文件（按住 Ctrl/Shift 可多选）。", "导出",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        await ExportAsync(files);
    }

    private async void OnExportAll(object sender, RoutedEventArgs e)
    {
        if (_currentFiles.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "当前相册分类中没有文件可导出。", "导出",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        await ExportAsync(_currentFiles);
    }

    private async Task ExportAsync(List<PhotosService.MediaFile> files)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"选择保存 {files.Count} 个照片/视频的本地目标文件夹",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var destDir = dialog.SelectedPath;
        _busy = true;
        SetUiEnabled(false);
        ProgressBar.Value = 0;

        var progress = new Progress<PhotosService.ProgressInfo>(p =>
        {
            ProgressBar.Value = (double)p.Current / p.Total * 100;
            StatusText.Text = $"正在导出 ({p.Current}/{p.Total}): {p.CurrentFile}";
        });

        try
        {
            await Task.Run(() => _photos.ExportFiles(_device.Udid, files, destDir, progress));
            StatusText.Text = $"导出完成！共 {files.Count} 个文件已保存至: {destDir}";
            LogService.Info($"[照片] 成功导出 {files.Count} 个文件到 {destDir}");

            var open = System.Windows.MessageBox.Show(this,
                $"已成功导出 {files.Count} 个文件至：\n{destDir}\n\n是否立即在资源管理器中打开该文件夹？",
                "导出成功", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);

            if (open == System.Windows.MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = destDir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导出失败: {ex.Message}";
            LogService.Error($"[照片] 导出中断: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"导出失败: {ex.Message}", "导出错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            SetUiEnabled(true);
            ProgressBar.Value = 0;
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入到设备的图片或视频（支持多选）",
            Filter = "媒体文件 (*.jpg;*.jpeg;*.png;*.heic;*.mov;*.mp4)|*.jpg;*.jpeg;*.png;*.heic;*.mov;*.mp4|所有文件 (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

        var sourceFiles = dlg.FileNames.ToList();
        _busy = true;
        SetUiEnabled(false);
        ProgressBar.Value = 0;

        var progress = new Progress<PhotosService.ProgressInfo>(p =>
        {
            ProgressBar.Value = (double)p.Current / p.Total * 100;
            StatusText.Text = $"正在导入 ({p.Current}/{p.Total}): {p.CurrentFile}";
        });

        try
        {
            var imported = await Task.Run(() => _photos.ImportFiles(_device.Udid, sourceFiles, progress));
            StatusText.Text = $"成功导入 {imported.Count} 个文件至设备相机胶卷！";
            LogService.Info($"[照片] 成功导入 {imported.Count} 个文件至设备 {_device.DisplayName}");

            System.Windows.MessageBox.Show(this,
                $"成功导入 {imported.Count} 个文件到相机胶卷！\n\n提示：iOS 设备可能需要进入「相册」App 刷新或重启设备后才会更新系统缩略图。",
                "导入成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

            await LoadAlbumsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"导入失败: {ex.Message}";
            LogService.Error($"[照片] 导入中断: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"导入失败: {ex.Message}", "导入错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            SetUiEnabled(true);
            ProgressBar.Value = 0;
        }
    }
}
