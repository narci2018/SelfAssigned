using System.IO;
using System.Text.Json;

namespace AltServer.Windows.Services;

/// <summary>
/// 已安装应用注册表：追踪通过 AltServer 安装的所有应用
/// 记录 IPA 路径、Bundle ID、签名信息，用于续签
/// </summary>
public class InstalledAppRegistry
{
    private readonly string _registryPath;
    private AppRegistry _registry;

    public InstalledAppRegistry(string dataDir)
    {
        _registryPath = Path.Combine(dataDir, "app-registry.json");
        _registry = Load();
    }

    /// <summary>记录已安装的应用</summary>
    public void Register(string bundleId, string ipaPath, string appName,
        string deviceUdid, string? p12Path, string? provisionPath, string? p12Password)
    {
        // 复制 IPA 到缓存目录
        var cacheDir = Path.Combine(Path.GetDirectoryName(_registryPath)!, "ipa-cache");
        Directory.CreateDirectory(cacheDir);

        var cachedIpa = Path.Combine(cacheDir, $"{bundleId}_{DateTime.Now:yyyyMMdd_HHmmss}.ipa");
        if (File.Exists(ipaPath) && ipaPath != cachedIpa)
        {
            File.Copy(ipaPath, cachedIpa, overwrite: true);
        }

        var entry = new InstalledAppEntry
        {
            BundleId = bundleId,
            AppName = appName,
            DeviceUdid = deviceUdid,
            OriginalIpaPath = ipaPath,
            CachedIpaPath = cachedIpa,
            P12Path = p12Path,
            ProvisionPath = provisionPath,
            P12Password = p12Password,
            InstalledAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
            Expiry = SigningService.ParseProvisionExpiry(provisionPath ?? "")
        };

        _registry.InstalledApps.RemoveAll(a => a.BundleId == bundleId && a.DeviceUdid == deviceUdid);
        _registry.InstalledApps.Add(entry);
        Save();
    }

    /// <summary>获取指定设备上所有已安装的应用</summary>
    public List<InstalledAppEntry> GetInstalledApps(string? deviceUdid = null)
    {
        if (deviceUdid is null)
            return _registry.InstalledApps.ToList();
        return _registry.InstalledApps.Where(a => a.DeviceUdid == deviceUdid).ToList();
    }

    /// <summary>获取需要续签的应用（即将过期或已过期）</summary>
    public List<InstalledAppEntry> GetExpiringApps(int withinDays = 3)
    {
        var threshold = DateTime.UtcNow.AddDays(withinDays);
        return _registry.InstalledApps.Where(a =>
        {
            if (a.Expiry is null) return false;
            return a.Expiry.Value <= threshold;
        }).ToList();
    }

    /// <summary>标记应用已续签</summary>
    public void MarkRefreshed(string bundleId, string deviceUdid, string? newProvisionPath = null)
    {
        var entry = _registry.InstalledApps.FirstOrDefault(
            a => a.BundleId == bundleId && a.DeviceUdid == deviceUdid);
        if (entry is not null)
        {
            entry.LastRefreshedAt = DateTime.UtcNow;
            if (newProvisionPath is not null)
            {
                entry.ProvisionPath = newProvisionPath;
                entry.Expiry = SigningService.ParseProvisionExpiry(newProvisionPath);
            }
            Save();
        }
    }

    /// <summary>更新注册表中的证书/描述文件路径（重新授权后同步最新路径）</summary>
    public void UpdatePaths(string bundleId, string deviceUdid, string p12Path, string? p12Password, string provisionPath)
    {
        var entry = _registry.InstalledApps.FirstOrDefault(
            a => a.BundleId == bundleId && a.DeviceUdid == deviceUdid);
        if (entry is not null)
        {
            entry.P12Path = p12Path;
            entry.P12Password = p12Password;
            entry.ProvisionPath = provisionPath;
            entry.Expiry = SigningService.ParseProvisionExpiry(provisionPath);
            Save();
        }
    }

    /// <summary>移除已卸载的应用记录</summary>
    public void Unregister(string bundleId, string deviceUdid)
    {
        _registry.InstalledApps.RemoveAll(a => a.BundleId == bundleId && a.DeviceUdid == deviceUdid);
        Save();
    }

    /// <summary>清理过期缓存的 IPA</summary>
    public void CleanupCache(int keepDays = 30)
    {
        var threshold = DateTime.Now.AddDays(-keepDays);
        var cacheDir = Path.Combine(Path.GetDirectoryName(_registryPath)!, "ipa-cache");
        if (!Directory.Exists(cacheDir)) return;

        foreach (var file in Directory.GetFiles(cacheDir, "*.ipa"))
        {
            if (File.GetLastWriteTime(file) < threshold)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    // MARK: - 持久化

    private AppRegistry Load()
    {
        try
        {
            if (!File.Exists(_registryPath)) return new AppRegistry();
            var json = File.ReadAllText(_registryPath);
            return JsonSerializer.Deserialize<AppRegistry>(json) ?? new AppRegistry();
        }
        catch
        {
            return new AppRegistry();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_registryPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存应用注册表失败: {ex.Message}");
        }
    }
}

// MARK: - 模型

public class AppRegistry
{
    public List<InstalledAppEntry> InstalledApps { get; set; } = new();
}

public class InstalledAppEntry
{
    public string BundleId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string DeviceUdid { get; set; } = string.Empty;
    public string OriginalIpaPath { get; set; } = string.Empty;
    public string CachedIpaPath { get; set; } = string.Empty;
    public string? P12Path { get; set; }
    public string? ProvisionPath { get; set; }
    public string? P12Password { get; set; }
    public DateTime InstalledAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
    public DateTime? Expiry { get; set; }

    public bool IsExpiringSoon(int withinDays = 3)
    {
        if (Expiry is null) return false;
        return Expiry.Value <= DateTime.UtcNow.AddDays(withinDays);
    }

    public int RemainingDays()
    {
        if (Expiry is null) return -1;
        return Math.Max(0, (int)(Expiry.Value - DateTime.UtcNow).TotalDays);
    }
}
