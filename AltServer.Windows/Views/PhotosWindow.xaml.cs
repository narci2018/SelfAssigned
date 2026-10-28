using System.IO;
using System.Windows;
using System.Windows.Controls;
using AltServer.Windows.Models;
using AltServer.Windows.Services;

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
    private bool _busy;

    public PhotosWindow(Device device)
    {
        InitializeComponent();
        _device = device;
        DeviceText.Text = $"{device.DisplayName} · {device.Udid}";
        Loaded += async (_, _) => await LoadAlbumsAsync();
    }

    public sealed class DisplayItem
    {
        public string Name { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
        public bool IsVideo { get; init; }
        public string IsVideoText => IsVideo ? "视频" : "图片";
        public PhotosService.MediaFile File { get; init; } = new();
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

        FilesList.ItemsSource = album.Files.Select(f => new DisplayItem
        {
            Name = f.Name,
            SizeText = f.SizeText,
            ModifiedText = f.Modified == DateTime.MinValue ? "—" : f.Modified.ToString("yyyy-MM-dd HH:mm"),
            IsVideo = f.IsVideo,
            File = f
        }).ToList();

        StatusText.Text = $"{album.Name}: 共 {album.Files.Count} 个项目。按住 Ctrl/Shift 可在列表中多选。";
    }

    private void SetUiEnabled(bool enabled)
    {
        ExportSelectedBtn.IsEnabled = enabled;
        ExportAllBtn.IsEnabled = enabled;
        ImportBtn.IsEnabled = enabled;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAlbumsAsync();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        FilesList.SelectAll();
    }

    private List<PhotosService.MediaFile> GetSelectedFiles()
        => FilesList.SelectedItems.Cast<DisplayItem>().Select(d => d.File).ToList();

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
