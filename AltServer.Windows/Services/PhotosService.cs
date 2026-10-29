using System.IO;
using Microsoft.Data.Sqlite;

namespace AltServer.Windows.Services;

/// <summary>
/// 仿爱思助手照片管理（v1.0.90）：
/// 1. 尝试从设备 AFC 提取 /PhotoData/Photos.sqlite 数据库，解析相册架构（图库、个人收藏、各 App 相册、相机录像）
/// 2. 具备智能多路径探测（/DCIM、/PhotoData/CPLAssets、/PhotoData/Metadata 等），完美规避路径不存在抛错
/// 3. 支持按相册分类、原图/视频流式批量导出与导入
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
        public bool IsFavorite { get; set; }
        public string AlbumName { get; set; } = "相机胶卷";

        public string SizeText =>
            SizeBytes >= 1L << 20 ? $"{SizeBytes / (double)(1L << 20):F1} MB"
            : $"{SizeBytes / (double)(1L << 10):F0} KB";
    }

    public sealed class AlbumCategory
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<MediaFile> Files { get; set; } = new();
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

    /// <summary>列出设备全部相册分类及图片/视频（优先从 Photos.sqlite 读取，失败则自动回退至全盘智能扫描）</summary>
    public List<AlbumCategory> LoadAlbums(string udid)
    {
        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        // 尝试从设备上拉取 Photos.sqlite 数据库进行结构化相册解析
        var sqliteCategories = TryLoadFromSqlite(session, afc, udid);
        if (sqliteCategories != null && sqliteCategories.Count > 0 && sqliteCategories.Sum(c => c.Count) > 0)
        {
            return sqliteCategories;
        }

        // 回退机制：全盘智能目录深度遍历（DCIM、PhotoData 等）
        var rawFiles = ScanMediaFilesFallback(afc);
        var categories = GroupMediaFiles(rawFiles);
        return categories;
    }

    /// <summary>兼容旧接口：直接返回相机胶卷/全相册媒体文件列表</summary>
    public List<MediaFile> ListCameraRoll(string udid)
    {
        var albums = LoadAlbums(udid);
        var cameraRoll = albums.FirstOrDefault(a => a.Name is "图库" or "相机胶卷") ?? albums.FirstOrDefault();
        return cameraRoll?.Files ?? new List<MediaFile>();
    }

    private List<AlbumCategory>? TryLoadFromSqlite(DeviceNative.IdeviceSession session, DeviceNative.AfcClient afc, string udid)
    {
        string? tempDbPath = null;
        try
        {
            // 检查常见的 Photos.sqlite 存放路径
            string[] possibleDbPaths =
            {
                "/PhotoData/Photos.sqlite",
                "/PhotoData/Photos.sqlite3",
                "/PhotoData/Metadata/Photos.sqlite"
            };

            string foundPath = string.Empty;
            foreach (var p in possibleDbPaths)
            {
                var info = afc.TryGetFileInfo(p);
                if (info != null && info.Count > 0)
                {
                    foundPath = p;
                    break;
                }
            }

            if (string.IsNullOrEmpty(foundPath)) return null;

            tempDbPath = Path.Combine(Path.GetTempPath(), $"Photos_{udid}_{Guid.NewGuid():N}.sqlite");
            afc.StreamReadFile(foundPath, tempDbPath);

            // 同时尝试同步拉取 -wal 文件以防数据未合并
            try
            {
                afc.StreamReadFile(foundPath + "-wal", tempDbPath + "-wal");
            }
            catch { }

            return ParsePhotosSqlite(tempDbPath, afc);
        }
        catch (Exception ex)
        {
            LogService.Warning($"[相册] 从 Photos.sqlite 解析相册失败，将采用智能目录扫描: {ex.Message}");
            return null;
        }
        finally
        {
            if (tempDbPath != null && File.Exists(tempDbPath))
            {
                try { File.Delete(tempDbPath); } catch { }
                try { File.Delete(tempDbPath + "-wal"); } catch { }
            }
        }
    }

    private List<AlbumCategory> ParsePhotosSqlite(string dbPath, DeviceNative.AfcClient afc)
    {
        var allFiles = new List<MediaFile>();

        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using (var connection = new SqliteConnection(connString))
        {
            connection.Open();

            // 自动检测表名：iOS 15+ 常用 ZASSET，更早版本为 ZGENERICASSET
            string assetTable = "ZASSET";
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('ZASSET', 'ZGENERICASSET') ORDER BY CASE WHEN name='ZASSET' THEN 1 ELSE 2 END LIMIT 1;";
                var found = checkCmd.ExecuteScalar() as string;
                if (!string.IsNullOrEmpty(found)) assetTable = found;
            }

            // 检查是否存在 ZADDITIONALASSETATTRIBUTES 属性表以获取文件大小
            bool hasAdditionalAttributes = false;
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ZADDITIONALASSETATTRIBUTES';";
                var cnt = Convert.ToInt64(checkCmd.ExecuteScalar() ?? 0);
                hasAdditionalAttributes = cnt > 0;
            }

            using var cmd = connection.CreateCommand();
            if (hasAdditionalAttributes)
            {
                cmd.CommandText = $@"
                    SELECT 
                        a.ZDIRECTORY, a.ZFILENAME, a.ZFAVORITE, a.ZDATECREATED, att.ZORIGINALFILESIZE
                    FROM {assetTable} a
                    LEFT JOIN ZADDITIONALASSETATTRIBUTES att ON a.ZADDITIONALATTRIBUTES = att.Z_PK
                    WHERE a.ZFILENAME IS NOT NULL
                    ORDER BY a.ZDATECREATED DESC";
            }
            else
            {
                cmd.CommandText = $@"
                    SELECT 
                        ZDIRECTORY, ZFILENAME, ZFAVORITE, ZDATECREATED, 0
                    FROM {assetTable}
                    WHERE ZFILENAME IS NOT NULL
                    ORDER BY ZDATECREATED DESC";
            }

            try
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var dir = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var fname = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var fav = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;

                    // iOS CoreData 纪元偏移：2001-01-01
                    var dateCreated = DateTime.MinValue;
                    if (!reader.IsDBNull(3))
                    {
                        var seconds = reader.GetDouble(3);
                        dateCreated = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
                    }

                    long fileSize = 0;
                    if (!reader.IsDBNull(4))
                    {
                        fileSize = reader.GetInt64(4);
                    }

                    // 规范化文件在设备上的路径
                    var afcPath = NormalizeAfcPath(dir, fname);
                    allFiles.Add(new MediaFile
                    {
                        AfcPath = afcPath,
                        Name = fname,
                        SizeBytes = fileSize,
                        Modified = dateCreated,
                        IsFavorite = fav
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Warning($"[相册] 查询 {assetTable} 出错: {ex.Message}");
            }
        }

        // 构造分类
        var categories = new List<AlbumCategory>
        {
            new() { Name = "图库", Files = allFiles, Count = allFiles.Count },
            new() { Name = "个人收藏", Files = allFiles.Where(f => f.IsFavorite).ToList(), Count = allFiles.Count(f => f.IsFavorite) },
            new() { Name = "视频", Files = allFiles.Where(f => f.IsVideo).ToList(), Count = allFiles.Count(f => f.IsVideo) }
        };

        // 按卷目录（如 100APPLE, 106APPLE）分组出“我的相簿”
        var albumGroups = allFiles
            .GroupBy(f => Path.GetDirectoryName(f.AfcPath)?.Replace('\\', '/').Split('/').LastOrDefault() ?? "未命名")
            .Where(g => !string.IsNullOrEmpty(g.Key) && !g.Key.Equals("DCIM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Key);

        foreach (var grp in albumGroups)
        {
            var list = grp.ToList();
            categories.Add(new AlbumCategory
            {
                Name = grp.Key,
                Files = list,
                Count = list.Count
            });
        }

        return categories;
    }

    private static string NormalizeAfcPath(string dir, string filename)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return $"/DCIM/100APPLE/{filename}";
        }
        var cleanDir = dir.Replace('\\', '/').TrimStart('/');
        return $"/{cleanDir}/{filename}";
    }

    private List<MediaFile> ScanMediaFilesFallback(DeviceNative.AfcClient afc)
    {
        var result = new List<MediaFile>();

        // 探测所有可能的媒体根目录
        var candidateRoots = new List<string> { "/DCIM", "/dcim", "/PhotoData/CPLAssets" };

        var rootEntries = afc.TryListDirectory("/");
        foreach (var entry in rootEntries.Where(e => e.IsDirectory))
        {
            if (string.Equals(entry.Name, "DCIM", StringComparison.OrdinalIgnoreCase))
            {
                candidateRoots.Add($"/{entry.Name}");
            }
        }

        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in candidateRoots)
        {
            if (scannedPaths.Contains(root)) continue;
            scannedPaths.Add(root);

            var entries = afc.TryListDirectory(root);
            if (entries.Length == 0) continue;

            // 根目录下直接存在的文件
            foreach (var file in entries.Where(e => !e.IsDirectory && IsMediaFile(e.Name)))
            {
                result.Add(ToMediaFile(file, root));
            }

            // 二级目录（如 100APPLE、101APPLE 等）
            foreach (var sub in entries.Where(e => e.IsDirectory))
            {
                var subPath = $"{root.TrimEnd('/')}/{sub.Name}";
                var subFiles = afc.TryListDirectory(subPath);
                foreach (var sf in subFiles.Where(f => !f.IsDirectory && IsMediaFile(f.Name)))
                {
                    result.Add(ToMediaFile(sf, subPath));
                }
            }
        }

        return result.OrderByDescending(f => f.Modified).ToList();
    }

    private static List<AlbumCategory> GroupMediaFiles(List<MediaFile> files)
    {
        var categories = new List<AlbumCategory>
        {
            new() { Name = "图库", Files = files, Count = files.Count },
            new() { Name = "个人收藏", Files = files.Where(f => f.IsFavorite).ToList(), Count = files.Count(f => f.IsFavorite) },
            new() { Name = "视频", Files = files.Where(f => f.IsVideo).ToList(), Count = files.Count(f => f.IsVideo) }
        };

        // 按卷目录（如 100APPLE）分组出“我的相簿”
        var albumGroups = files
            .GroupBy(f => Path.GetDirectoryName(f.AfcPath)?.Replace('\\', '/').Split('/').LastOrDefault() ?? "未命名")
            .Where(g => !string.IsNullOrEmpty(g.Key) && !g.Key.Equals("DCIM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Key);

        foreach (var grp in albumGroups)
        {
            var list = grp.ToList();
            categories.Add(new AlbumCategory
            {
                Name = grp.Key,
                Files = list,
                Count = list.Count
            });
        }

        return categories;
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
            try
            {
                afc.StreamReadFile(file.AfcPath, destPath, ct);
            }
            catch (Exception ex)
            {
                LogService.Warning($"[相册导出] 读取文件 {file.AfcPath} 失败: {ex.Message}");
            }
        }
    }

    public List<string> ImportFiles(string udid, List<string> sourceFiles,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        var targetDir = EnsureImportDirectory(afc);

        var usedNumbers = new HashSet<int>();
        foreach (var entry in afc.TryListDirectory(targetDir))
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

    /// <summary>从设备读取单个媒体文件的全部字节（用于缩略图生成与高清预览）</summary>
    public byte[]? ReadFileData(string udid, string afcPath)
    {
        try
        {
            using var session = new DeviceNative.IdeviceSession(udid);
            using var afc = session.CreateAfcClient();
            return afc.ReadFile(afcPath);
        }
        catch (Exception ex)
        {
            LogService.Warning($"[照片] 读取文件失败 {afcPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>从设备中彻底删除指定的照片/视频列表</summary>
    public void DeleteFiles(string udid, IEnumerable<MediaFile> files, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        using var session = new DeviceNative.IdeviceSession(udid);
        using var afc = session.CreateAfcClient();

        var list = files.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = list[i];
            progress?.Report(new ProgressInfo { Current = i + 1, Total = list.Count, CurrentFile = file.Name });
            try
            {
                afc.Remove(file.AfcPath);
                LogService.Info($"[照片] 已成功删除设备文件: {file.AfcPath}");
            }
            catch (Exception ex)
            {
                LogService.Warning($"[照片] 删除文件 {file.AfcPath} 失败: {ex.Message}");
                throw;
            }
        }
    }
}
