using System.IO;
using System.Windows;
using System.Windows.Input;
using AltServer.Windows.Models;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

/// <summary>
/// 虚拟 U 盘 / 文件系统窗口（对标爱思助手“我的设备”下的虚拟U盘与文件系统）
/// </summary>
public partial class FileSystemWindow : Window
{
    private readonly Device _device;
    private string _currentPath = "/";
    private readonly Stack<string> _history = new();
    private bool _busy;

    public FileSystemWindow(Device device)
    {
        InitializeComponent();
        _device = device;
        DeviceText.Text = $"{device.DisplayName} · {device.Udid}";
        Loaded += async (_, _) => await NavigateToAsync("/");
    }

    public sealed class FileItem
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public bool IsDirectory { get; init; }
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }

        public string Icon => IsDirectory ? "📁" : GetFileIcon(Name);
        public string TypeText => IsDirectory ? "文件夹" : Path.GetExtension(Name).ToUpperInvariant();
        public string SizeText => IsDirectory ? "--"
            : (SizeBytes >= 1L << 20 ? $"{SizeBytes / (double)(1L << 20):F1} MB"
            : $"{SizeBytes / (double)(1L << 10):F0} KB");
        public string ModifiedText => Modified == DateTime.MinValue ? "—" : Modified.ToString("yyyy-MM-dd HH:mm");

        private static string GetFileIcon(string filename)
        {
            var ext = Path.GetExtension(filename).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".png" or ".jpeg" or ".heic" => "🖼",
                ".mp4" or ".mov" or ".avi" or ".mkv" => "🎬",
                ".mp3" or ".m4a" or ".flac" or ".wav" => "🎵",
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "📦",
                ".txt" or ".pdf" or ".doc" or ".docx" or ".xlsx" => "📄",
                ".ipa" => "📱",
                _ => "📄"
            };
        }
    }

    private async Task NavigateToAsync(string path)
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = $"正在读取目录 {path} ...";

        try
        {
            var items = await Task.Run(() =>
            {
                using var session = new DeviceNative.IdeviceSession(_device.Udid);
                using var afc = session.CreateAfcClient();

                var entries = afc.TryListDirectory(path);
                return entries.Select(e => new FileItem
                {
                    Name = e.Name,
                    FullPath = path == "/" ? $"/{e.Name}" : $"{path.TrimEnd('/')}/{e.Name}",
                    IsDirectory = e.IsDirectory,
                    SizeBytes = e.SizeBytes,
                    Modified = e.Modified
                })
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            });

            _currentPath = path;
            CurrentPathText.Text = path;
            FilesList.ItemsSource = items;
            StatusText.Text = $"共 {items.Count} 项 (包含 {items.Count(i => i.IsDirectory)} 个文件夹)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开目录失败: {ex.Message}";
            LogService.Warning($"[文件系统] 打开 {path} 失败: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is not FileItem item) return;
        if (item.IsDirectory)
        {
            _history.Push(_currentPath);
            await NavigateToAsync(item.FullPath);
        }
    }

    private async void OnGoBack(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0)
        {
            var prev = _history.Pop();
            await NavigateToAsync(prev);
        }
        else if (_currentPath != "/")
        {
            var parent = Path.GetDirectoryName(_currentPath.TrimEnd('/'))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) parent = "/";
            await NavigateToAsync(parent);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await NavigateToAsync(_currentPath);

    private async void OnUploadFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要上传至虚拟U盘的文件（支持多选）",
            Multiselect = true
        };

        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

        _busy = true;
        ProgressBar.Value = 0;

        try
        {
            var files = dlg.FileNames;
            await Task.Run(() =>
            {
                using var session = new DeviceNative.IdeviceSession(_device.Udid);
                using var afc = session.CreateAfcClient();

                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];
                    var filename = Path.GetFileName(file);
                    var destAfcPath = _currentPath == "/" ? $"/{filename}" : $"{_currentPath.TrimEnd('/')}/{filename}";
                    var currentIdx = i + 1;
                    var total = files.Length;

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"正在上传 ({currentIdx}/{total}): {filename}";
                        ProgressBar.Value = (double)currentIdx / total * 100;
                    });

                    afc.WriteFile(destAfcPath, File.ReadAllBytes(file));
                }
            });

            StatusText.Text = $"上传完成！共 {dlg.FileNames.Length} 个文件已写入设备。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"上传出错: {ex.Message}";
            System.Windows.MessageBox.Show(this, $"上传文件失败: {ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            ProgressBar.Value = 0;
            await NavigateToAsync(_currentPath);
        }
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var folderName = Microsoft.VisualBasic.Interaction.InputBox("请输入新文件夹名称:", "新建文件夹", "新建文件夹");
        if (string.IsNullOrWhiteSpace(folderName)) return;

        try
        {
            var targetPath = _currentPath == "/" ? $"/{folderName.Trim()}" : $"{_currentPath.TrimEnd('/')}/{folderName.Trim()}";
            await Task.Run(() =>
            {
                using var session = new DeviceNative.IdeviceSession(_device.Udid);
                using var afc = session.CreateAfcClient();
                afc.MakeDirectory(targetPath);
            });

            await NavigateToAsync(_currentPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"创建文件夹失败: {ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async void OnExportSelected(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.Cast<FileItem>().Where(i => !i.IsDirectory).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "请在列表中勾选要导出的文件（按住 Ctrl 多选）。", "导出",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        using var fbd = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"选择保存 {selected.Count} 个文件的本地目标文件夹"
        };

        if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _busy = true;
        ProgressBar.Value = 0;

        try
        {
            await Task.Run(() =>
            {
                using var session = new DeviceNative.IdeviceSession(_device.Udid);
                using var afc = session.CreateAfcClient();

                for (int i = 0; i < selected.Count; i++)
                {
                    var item = selected[i];
                    var dest = Path.Combine(fbd.SelectedPath, item.Name);
                    var currentIdx = i + 1;
                    var total = selected.Count;

                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"正在导出 ({currentIdx}/{total}): {item.Name}";
                        ProgressBar.Value = (double)currentIdx / total * 100;
                    });

                    afc.StreamReadFile(item.FullPath, dest);
                }
            });

            StatusText.Text = $"已成功导出 {selected.Count} 个文件至本地！";
            var open = System.Windows.MessageBox.Show(this, $"导出成功！\n是否立即打开文件夹？", "导出完成",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);

            if (open == System.Windows.MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fbd.SelectedPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"导出失败: {ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            ProgressBar.Value = 0;
        }
    }

    private async void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.Cast<FileItem>().ToList();
        if (selected.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(this,
            $"确定要永久删除选中的 {selected.Count} 个项目吗？操作无法撤销。",
            "确认删除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await Task.Run(() =>
            {
                using var session = new DeviceNative.IdeviceSession(_device.Udid);
                using var afc = session.CreateAfcClient();

                foreach (var item in selected)
                {
                    afc.Remove(item.FullPath);
                }
            });

            await NavigateToAsync(_currentPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"删除失败: {ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
