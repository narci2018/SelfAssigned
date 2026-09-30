using System.IO;
using System.Text;
using System.Text.Json;

namespace AltServer.Windows.Services;

/// <summary>IPA 库中的一个文件条目</summary>
public class IpaLibraryItem
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BundleId { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? LastSignedAt { get; set; }

    public string SizeText =>
        SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):F2} GB"
        : SizeBytes >= 1L << 20 ? $"{SizeBytes / (double)(1L << 20):F1} MB"
        : $"{SizeBytes / (double)(1L << 10):F0} KB";
}

/// <summary>
/// IPA 库管理（v1.0.86）：把用户选中的 IPA 收纳到 %AppData%/AltServer/ipa-library，
/// 持久化清单为 ipa-library.json，支持新增 / 移除 / 从库直接签名安装。
/// </summary>
public class IpaLibraryService
{
    private readonly string _libDir;
    private readonly string _indexPath;
    private List<IpaLibraryItem> _items = new();

    public IReadOnlyList<IpaLibraryItem> Items => _items;

    public IpaLibraryService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltServer");
        _libDir = Path.Combine(appDataDir, "ipa-library");
        _indexPath = Path.Combine(appDataDir, "ipa-library.json");
        Directory.CreateDirectory(_libDir);
        Load();
        PruneMissingFiles();
    }

    /// <summary>把外部 IPA 复制入库；同名文件自动追加序号。失败返回 null。</summary>
    public IpaLibraryItem? Add(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;

            var fileName = Path.GetFileName(sourcePath);
            var target = Path.Combine(_libDir, fileName);

            // 同名去重：name(1).ipa、name(2).ipa ...
            if (File.Exists(target))
            {
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    // 源文件本来就在库里，不重复复制
                    var existing = _items.FirstOrDefault(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null) return existing;
                }

                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                for (var n = 1; ; n++)
                {
                    target = Path.Combine(_libDir, $"{stem}({n}){ext}");
                    if (!File.Exists(target)) break;
                }
            }

            File.Copy(sourcePath, target, overwrite: false);

            var item = new IpaLibraryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = Path.GetFileName(target),
                DisplayName = Path.GetFileNameWithoutExtension(target),
                BundleId = TryReadBundleId(target) ?? string.Empty,
                SizeBytes = new FileInfo(target).Length,
                AddedAt = DateTime.Now
            };
            _items.Add(item);
            Save();
            return item;
        }
        catch (Exception ex)
        {
            LogService.Error($"添加 IPA 到库失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>从库中移除（同时删除库内文件）</summary>
    public bool Remove(string id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return false;

        try
        {
            var path = GetPath(item);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LogService.Warning($"删除 IPA 文件失败: {ex.Message}");
        }

        _items.Remove(item);
        Save();
        return true;
    }

    /// <summary>条目对应的库内文件完整路径</summary>
    public string GetPath(IpaLibraryItem item) => Path.Combine(_libDir, item.FileName);

    /// <summary>签名成功后回填 BundleId 与最近签名时间</summary>
    public void MarkSigned(string id, string? bundleId)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        item.LastSignedAt = DateTime.Now;
        if (!string.IsNullOrEmpty(bundleId) && string.IsNullOrEmpty(item.BundleId))
        {
            item.BundleId = bundleId;
        }
        Save();
    }

    /// <summary>启动时清理：清单里指向的文件已被手动删掉的条目直接移除</summary>
    private void PruneMissingFiles()
    {
        var removed = _items.Where(i => !File.Exists(GetPath(i))).ToList();
        if (removed.Count > 0)
        {
            foreach (var item in removed) _items.Remove(item);
            Save();
            LogService.Warning($"IPA 库清理：{removed.Count} 个文件已不存在于磁盘，已从列表移除");
        }
    }

    /// <summary>尽力从 IPA 内读取 Info.plist 的 Bundle Identifier（读不到返回 null，不影响入库）</summary>
    private static string? TryReadBundleId(string ipaPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(ipaPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".app/Info.plist", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            var bytes = ms.ToArray();

            // 优先按 XML plist 解析
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<key>CFBundleIdentifier</key>"))
            {
                var key = "<key>CFBundleIdentifier</key>";
                var idx = text.IndexOf(key, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var start = text.IndexOf("<string>", idx, StringComparison.Ordinal);
                    var end = text.IndexOf("</string>", idx, StringComparison.Ordinal);
                    if (start >= 0 && end > start)
                        return text[(start + 8)..end].Trim();
                }
            }

            // 二进制 plist：直接扫 ASCII key/value 相邻字符串（尽力而为）
            return ScanBinaryPlistStringAfter(bytes, "CFBundleIdentifier");
        }
        catch
        {
            return null;
        }
    }

    private static string? ScanBinaryPlistStringAfter(byte[] data, string key)
    {
        var keyBytes = Encoding.ASCII.GetBytes(key);
        for (var i = 0; i <= data.Length - keyBytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < keyBytes.Length; j++)
            {
                if (data[i + j] != keyBytes[j]) { match = false; break; }
            }
            if (!match) continue;

            // key 结束后找一段可打印 ASCII 作为 value
            var start = i + keyBytes.Length;
            while (start < data.Length && (data[start] < 0x20 || data[start] > 0x7E)) start++;
            var end = start;
            while (end < data.Length && data[end] >= 0x20 && data[end] <= 0x7E) end++;
            if (end > start && end - start < 256)
            {
                var candidate = Encoding.ASCII.GetString(data, start, end - start);
                if (candidate.Length > 0 && candidate.Contains('.')) return candidate;
            }
        }
        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;
            var json = File.ReadAllText(_indexPath);
            _items = JsonSerializer.Deserialize<List<IpaLibraryItem>>(json) ?? new();
        }
        catch
        {
            _items = new();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_indexPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存 IPA 库索引失败: {ex.Message}");
        }
    }
}
