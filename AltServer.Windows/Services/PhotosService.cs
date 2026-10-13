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

    /// <summary>列出相机胶卷全部媒体文件，动态探测 DCIM / 1xxAPPLE 目录，避免硬编码路径报错</summary>
    public List<MediaFile> ListCameraRoll(string udid)
    {
        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        var result = new List<MediaFile>();

        // 1. 尝试从 AFC 根目录探测实际存在的照片目录
        var rootEntries = afc.TryListDirectory("/");
        var candidateRoots = new List<string>();

        // 检查根目录下的 DCIM 目录（忽略大小写）
        foreach (var entry in rootEntries.Where(e => e.IsDirectory))
        {
            if (string.Equals(entry.Name, "DCIM", StringComparison.OrdinalIgnoreCase))
            {
                candidateRoots.Add($"/{entry.Name}");
            }
        }

        // 若根目录未找到，尝试默认路径
        if (candidateRoots.Count == 0)
        {
            candidateRoots.Add(DcimRoot);
            candidateRoots.Add("/dcim");
            candidateRoots.Add("DCIM");
        }

        var scannedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dcimPath in candidateRoots)
        {
            if (scannedDirs.Contains(dcimPath)) continue;
            scannedDirs.Add(dcimPath);

            var dcimEntries = afc.TryListDirectory(dcimPath);
            if (dcimEntries.Length == 0) continue;

            // DCIM 根目录散落的媒体文件
            foreach (var entry in dcimEntries.Where(e => !e.IsDirectory))
            {
                if (IsMediaFile(entry.Name)) result.Add(ToMediaFile(entry, dcimPath));
            }

            // DCIM 下的子目录（如 100APPLE, 101APPLE 等包含 APPLE 的相册卷，或者所有包含媒体的子目录）
            foreach (var dir in dcimEntries.Where(e => e.IsDirectory))
            {
                var dirPath = $"{dcimPath.TrimEnd('/')}/{dir.Name}";
                var subFiles = afc.TryListDirectory(dirPath);
                foreach (var file in subFiles.Where(f => !f.IsDirectory))
                {
                    if (IsMediaFile(file.Name)) result.Add(ToMediaFile(file, dirPath));
                }
            }

            // 如果在该 DCIM 目录下成功找到了文件或找到了卷目录，就不再扫描后续候选路径
            if (result.Count > 0) break;
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
        var rootEntries = afc.TryListDirectory("/");
        var dcimName = rootEntries
            .Where(e => e.IsDirectory && string.Equals(e.Name, "DCIM", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .FirstOrDefault() ?? "DCIM";

        var dcimPath = $"/{dcimName}";
        var entries = afc.TryListDirectory(dcimPath);
        if (entries.Length == 0 && dcimPath != DcimRoot)
        {
            entries = afc.TryListDirectory(DcimRoot);
            if (entries.Length > 0) dcimPath = DcimRoot;
        }

        var existing = entries
            .Where(e => e.IsDirectory && e.Name.EndsWith("APPLE", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .OrderBy(n => n)
            .FirstOrDefault();

        var dirName = existing ?? "100APPLE";
        var path = $"{dcimPath}/{dirName}";
        if (existing is null)
        {
            try { afc.MakeDirectory(dcimPath); } catch { }
            try { afc.MakeDirectory(path); } catch { }
        }
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
