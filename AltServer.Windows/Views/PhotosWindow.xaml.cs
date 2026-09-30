using System.IO;
using System.Windows;
using AltServer.Windows.Models;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

/// <summary>
/// 照片与视频窗口（v1.0.87）：浏览设备相机胶卷、批量导出到电脑、从电脑导入。
/// </summary>
public partial class PhotosWindow : Window
{
    private readonly Device _device;
    private readonly PhotosService _photos = new();
    private List<PhotosService.MediaFile> _files = new();
    private bool _busy;

    public PhotosWindow(Device device)
    {
        InitializeComponent();
        _device = device;
        TitleText.Text = "照片与视频";
        DeviceText.Text = $"{device.DisplayName} · {device.Udid}";
        Loaded += async (_, _) => await LoadFilesAsync();
    }

    private sealed class DisplayItem
    {
        public string Name { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
        public string IsVideoText { get; init; } = "";
        public PhotosService.MediaFile File { get; init; } = new();
    }

    private async Task LoadFilesAsync()
    {
        if (_busy) return;
        _busy = true;
        SetUiEnabled(false);
        StatusText.Text = "正在读取设备相机胶卷...（文件多时可能需要十几秒）";
        try
        {
            var files = await Task.Run(() => _photos.ListCameraRoll(_device.Udid));
            _files = files;

            FilesList.ItemsSource = files.Select(f => new DisplayItem
            {
                Name = f.Name,
                SizeText = f.SizeText,
                ModifiedText = f.Modified == DateTime.MinValue ? "—" : f.Modified.ToString("yyyy-MM-dd HH:mm"),
                IsVideoText = f.IsVideo ? "视频" : "图片",
                File = f
            }).ToList();

            StatusText.Text = $"共 {files.Count} 个文件（图片/视频）。导出选中: 勾选列表项后点击「导出选中」；按住 Ctrl/Shift 可多选。";
            LogService.Info($"[照片] 已读取设备 {_device.DisplayName} 相册: {files.Count} 个文件");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取失败: {ex.Message}";
            LogService.Error($"[照片] 读取相册失败: {ex.Message}");
        }
        finally
        {
            _busy = false;
            SetUiEnabled(true);
        }
    }

    private void SetUiEnabled(bool enabled)
    {
        ExportSelectedBtn.IsEnabled = enabled;
        ExportAllBtn.IsEnabled = enabled;
        ImportBtn.IsEnabled = enabled;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadFilesAsync();

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
            System.Windows.MessageBox.Show(this, "请先在列表中选择要导出的文件（Ctrl/Shift 多选）。", "导出",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        await ExportAsync(files);
    }

    private async void OnExportAll(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "设备相册为空。", "导出", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        await ExportAsync(_files);
    }

    private async Task ExportAsync(List<PhotosService.MediaFile> files)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"选择导出目录（将保存 {files.Count} 个文件）",
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        await RunWithProgress($"导出 {files.Count} 个文件", async (progress, ct) =>
        {
            await Task.Run(() => _photos.ExportFiles(_device.Udid, files, dialog.SelectedPath, progress, ct));
            LogService.Success($"[照片] 已导出 {files.Count} 个文件到 {dialog.SelectedPath}");
        });
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入到设备的图片/视频",
            Filter = "媒体文件|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.gif;*.dng;*.mov;*.mp4;*.m4v|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var files = dialog.FileNames.ToList();

        await RunWithProgress($"导入 {files.Count} 个文件", async (progress, ct) =>
        {
            var imported = await Task.Run(() => _photos.ImportFiles(_device.Udid, files, progress, ct));
            LogService.Success($"[照片] 已导入 {imported.Count} 个文件到设备相机胶卷目录（iOS 相册可能需要重新打开/重启设备后显示）");
        });

        await LoadFilesAsync();
    }

    /// <summary>统一的进度执行器：禁用界面 → 进度条 → 异常处理 → 恢复</summary>
    private async Task RunWithProgress(string label, Func<IProgress<PhotosService.ProgressInfo>, CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        SetUiEnabled(false);

        var progress = new Progress<PhotosService.ProgressInfo>(p =>
        {
            ProgressBar.Value = p.Total == 0 ? 0 : p.Current * 100.0 / p.Total;
            StatusText.Text = $"{label}: {p.Current}/{p.Total} — {p.CurrentFile}";
        });

        try
        {
            await work(progress, CancellationToken.None);
            ProgressBar.Value = 100;
            StatusText.Text = $"{label} 完成";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{label} 失败: {ex.Message}";
            LogService.Error($"[照片] {label} 失败: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"{label} 失败:\n{ex.Message}", "照片与视频", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            SetUiEnabled(true);
        }
    }
}
