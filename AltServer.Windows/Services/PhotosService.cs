using System.IO;

namespace AltServer.Windows.Services;

/// <summary>
/// 照片与视频管理（v1.0.87）：基于 AFC（com.apple.afc）访问设备媒体分区 /DCIM，
/// 实现浏览、批量导出（设备→PC）与导入（PC→设备）。
/// </summary>
public class PhotosService
{
    public const string DcimRoot = "/DCIM";

    public sealed class MediaFile
    {
        public string AfcPath { get; init; } = "";
        public string Name { get; init; } = "";
        public long SizeBytes { get; init; }
        public DateTime Modified { get; init; }
        public bool IsVideo => MediaVideoExtensions.Contains(Path.GetExtension(Name).ToLowerInvariant());

        public string SizeText =>
            SizeBytes >= 1L << 20 ? $"{SizeBytes / (double)(1L << 20):F1} MB"
            : $"{SizeBytes / (double)(1L << 10):F0} KB";
    }

    public sealed class ProgressInfo
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string CurrentFile { get; set; } = "";
        public long BytesDone { get; set; }
    }

    private static readonly string[] MediaFileExtensions =
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".dng", ".tif", ".tiff",
        ".mov", ".mp4", ".m4v"
    };
    private static readonly string[] MediaVideoExtensions = { ".mov", ".mp4", ".m4v" };

    /// <summary>列出相机胶卷全部媒体文件（/DCIM/*APPLE 目录下的图片与视频），按名称倒序（最新在前）</summary>
    public List<MediaFile> ListCameraRoll(string udid)
    {
        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        var result = new List<MediaFile>();
        var rootEntries = afc.ListDirectory(DcimRoot);

        // DCIM 下的 1xxAPPLE 卷目录 + 根目录散落的媒体文件
        foreach (var entry in rootEntries.Where(e => !e.IsDirectory))
        {
            if (IsMediaFile(entry.Name)) result.Add(ToMediaFile(entry, DcimRoot));
        }

        foreach (var dir in rootEntries.Where(e => e.IsDirectory && e.Name.EndsWith("APPLE", StringComparison.OrdinalIgnoreCase)))
        {
            var dirPath = $"{DcimRoot}/{dir.Name}";
            foreach (var file in afc.ListDirectory(dirPath).Where(f => !f.IsDirectory))
            {
                if (IsMediaFile(file.Name)) result.Add(ToMediaFile(file, dirPath));
            }
        }

        return result.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static MediaFile ToMediaFile(DeviceNative.AfcClient.DirEntry entry, string dirPath)
        => new()
        {
            AfcPath = $"{dirPath}/{entry.Name}",
            Name = entry.Name,
            SizeBytes = entry.SizeBytes,
            Modified = entry.Modified
        };

    private static bool IsMediaFile(string name)
        => MediaFileExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>批量导出（设备→PC），流式落盘不占内存。目标同名文件自动追加序号。</summary>
    public void ExportFiles(string udid, List<MediaFile> files, string destDir,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report(new ProgressInfo { Current = i + 1, Total = files.Count, CurrentFile = file.Name });

            var destPath = UniqueDestinationPath(Path.Combine(destDir, file.Name));
            afc.StreamReadFile(file.AfcPath, destPath, ct);
        }
    }

    /// <summary>批量导入（PC→设备）：写入 /DCIM 的 1xxAPPLE 卷目录，自动命名 IMG_xxxx/VID_xxxx</summary>
    public List<string> ImportFiles(string udid, List<string> sourceFiles,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        var targetDir = EnsureImportDirectory(afc);

        // 找到该卷目录中已用的最大 IMG_/VID_ 编号
        var usedNumbers = new HashSet<int>();
        foreach (var entry in afc.ListDirectory(targetDir))
        {
            var stem = Path.GetFileNameWithoutExtension(entry.Name);
            var idx = stem.LastIndexOf('_');
            if (idx >= 0 && int.TryParse(stem[(idx + 1)..], out var num)) usedNumbers.Add(num);
        }
        var nextNumber = usedNumbers.Count == 0 ? 1 : usedNumbers.Max() + 1;

        var imported = new List<string>();
        for (var i = 0; i < sourceFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = sourceFiles[i];
            progress?.Report(new ProgressInfo { Current = i + 1, Total = sourceFiles.Count, CurrentFile = Path.GetFileName(src) });

            var ext = Path.GetExtension(src).ToUpperInvariant();
            var prefix = IsVideoExtension(ext) ? "VID" : "IMG";
            var destName = $"{prefix}_{nextNumber:D4}{ext}";
            nextNumber++;

            afc.WriteFile($"{targetDir}/{destName}", File.ReadAllBytes(src), ct);
            imported.Add(destName);
        }

        return imported;
    }

    private static bool IsVideoExtension(string ext) => MediaVideoExtensions.Contains(ext.ToLowerInvariant());

    /// <summary>定位/创建导入目录：优先复用已有的 1xxAPPLE，否则创建 100APPLE</summary>
    private static string EnsureImportDirectory(DeviceNative.AfcClient afc)
    {
        var entries = afc.ListDirectory(DcimRoot);
        var existing = entries
            .Where(e => e.IsDirectory && e.Name.EndsWith("APPLE", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .OrderBy(n => n)
            .FirstOrDefault();

        var dirName = existing ?? "100APPLE";
        var path = $"{DcimRoot}/{dirName}";
        if (existing is null) afc.MakeDirectory(path);
        return path;
    }

    private static string UniqueDestinationPath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
