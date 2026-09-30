using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltServer.Windows.Services;

/// <summary>
/// 设置持久化服务：JSON文件存储于 %AppData%/AltServer
/// v1.0.86: 支持多账号（Accounts）+ 设备↔账号绑定（DeviceAccounts），
/// 账号密码经 Windows DPAPI 加密存储，实现登录持久化。
/// </summary>
public class SettingsService
{
    private readonly string _settingsDir;
    private readonly string _settingsPath;

    public Settings Data { get; private set; }

    public class Settings
    {
        public string ToolsDir { get; set; } = "tools";
        // 全局默认签名文件（向后兼容，新版优先使用 DeviceConfigs[udid]）
        public string P12Path { get; set; } = string.Empty;
        public string P12Password { get; set; } = string.Empty;
        public string MobileProvisionPath { get; set; } = string.Empty;
        public bool AutoRefreshEnabled { get; set; } = true;
        public int RefreshIntervalHours { get; set; } = 6;
        public bool StartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public int ServerPort { get; set; } = 27000;
        public string? LastDeviceUdid { get; set; }
        public List<string> TrustedDevices { get; set; } = new();
        public List<Models.Source> Sources { get; set; } = new();
        public bool SetupCompleted { get; set; }
        public string? AppleId { get; set; }
        public string AnisetteUrl { get; set; } = string.Empty;

        /// <summary>每台设备独立的证书/描述文件配置，按 UDID 索引</summary>
        public Dictionary<string, DeviceSigningConfig> DeviceConfigs { get; set; } = new();

        /// <summary>多账号列表（v1.0.86）：每个账号独立的登录凭据与签名材料</summary>
        public List<AccountConfig> Accounts { get; set; } = new();

        /// <summary>设备 UDID → 绑定的 Apple ID（v1.0.86）</summary>
        public Dictionary<string, string> DeviceAccounts { get; set; } = new();

        /// <summary>当前激活账号（v1.0.86）</summary>
        public string? ActiveAccount { get; set; }

        /// <summary>获取指定设备的签名配置，不存在时回落到全局默认值</summary>
        public DeviceSigningConfig GetDeviceConfig(string udid)
        {
            if (DeviceConfigs.TryGetValue(udid, out var cfg) &&
                !string.IsNullOrEmpty(cfg.P12Path) &&
                File.Exists(cfg.P12Path))
                return cfg;

            // 回落到全局配置（兼容旧版）
            return new DeviceSigningConfig
            {
                P12Path = P12Path,
                P12Password = P12Password,
                MobileProvisionPath = MobileProvisionPath
            };
        }

        /// <summary>保存指定设备的签名配置，同时更新全局默认值</summary>
        public void SetDeviceConfig(string udid, string p12Path, string p12Password, string provisionPath)
        {
            DeviceConfigs[udid] = new DeviceSigningConfig
            {
                P12Path = p12Path,
                P12Password = p12Password,
                MobileProvisionPath = provisionPath
            };
            // 同时写入全局（保持向后兼容 + 作为新设备的默认值）
            P12Path = p12Path;
            P12Password = p12Password;
            MobileProvisionPath = provisionPath;

            // 同步更新该设备绑定账号的签名材料与有效期
            if (DeviceAccounts.TryGetValue(udid, out var appleId))
            {
                var acct = FindAccount(appleId);
                if (acct is not null)
                    acct.UpdateSigning(p12Path, p12Password, provisionPath);
            }
        }

        /// <summary>查找账号，找不到返回 null</summary>
        public AccountConfig? FindAccount(string? appleId)
        {
            if (string.IsNullOrEmpty(appleId)) return null;
            return Accounts.FirstOrDefault(a =>
                string.Equals(a.AppleId, appleId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>查找或创建账号</summary>
        public AccountConfig GetOrCreateAccount(string appleId)
        {
            var acct = FindAccount(appleId);
            if (acct is null)
            {
                acct = new AccountConfig { AppleId = appleId };
                Accounts.Add(acct);
            }
            return acct;
        }

        /// <summary>绑定设备到账号</summary>
        public void SetDeviceAccount(string udid, string appleId)
        {
            DeviceAccounts[udid] = appleId;
        }

        /// <summary>获取设备绑定的账号 Apple ID（未绑定时返回 null）</summary>
        public string? GetAccountForDevice(string udid)
        {
            return DeviceAccounts.TryGetValue(udid, out var appleId) ? appleId : null;
        }

        /// <summary>移除账号：同时解除其名下设备绑定；若是当前账号则切换到剩余第一个</summary>
        /// <returns>被解除绑定的设备 UDID 列表（签名材料保留，设备仍可用旧文件签名）</returns>
        public List<string> RemoveAccount(string appleId)
        {
            var unbound = new List<string>();
            Accounts.RemoveAll(a => string.Equals(a.AppleId, appleId, StringComparison.OrdinalIgnoreCase));

            foreach (var kv in DeviceAccounts.Where(kv =>
                         string.Equals(kv.Value, appleId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                DeviceAccounts.Remove(kv.Key);
                unbound.Add(kv.Key);
            }

            if (string.Equals(ActiveAccount, appleId, StringComparison.OrdinalIgnoreCase))
                ActiveAccount = Accounts.FirstOrDefault()?.AppleId;

            return unbound;
        }

        /// <summary>设置当前激活账号</summary>
        public void SetActiveAccount(string? appleId)
        {
            ActiveAccount = appleId;
        }

        /// <summary>全部账号中最早的描述文件过期时间（用于过期提醒）</summary>
        public DateTime? EarliestProfileExpiry()
        {
            DateTime? earliest = null;
            foreach (var acct in Accounts)
            {
                if (acct.ProfileExpiry.HasValue &&
                    (earliest is null || acct.ProfileExpiry < earliest))
                    earliest = acct.ProfileExpiry;
            }
            return earliest;
        }
    }

    /// <summary>
    /// 账号配置：Apple ID + DPAPI 加密密码 + 该账号名下的证书/描述文件。
    /// 免费账号证书与描述文件 7 天有效，ProfileExpiry 用于过期提醒与自动续期判断。
    /// </summary>
    public class AccountConfig
    {
        public string AppleId { get; set; } = string.Empty;

        /// <summary>DPAPI 加密后的密码（Base64）。仅存本机、当前用户可解。</summary>
        public string PasswordEncrypted { get; set; } = string.Empty;

        [JsonIgnore]
        public bool HasPassword => !string.IsNullOrEmpty(PasswordEncrypted);

        public DateTime? LastLoginUtc { get; set; }
        public string? TeamId { get; set; }

        /// <summary>该账号最近一次申请的证书/描述文件（作为新设备的默认签名材料）</summary>
        public string P12Path { get; set; } = string.Empty;
        public string P12Password { get; set; } = string.Empty;
        public string MobileProvisionPath { get; set; } = string.Empty;
        public DateTime? CertExpiry { get; set; }
        public DateTime? ProfileExpiry { get; set; }

        /// <summary>解密密码；解密失败（如换了 Windows 用户）返回 null</summary>
        public string? GetPassword() => SettingsService.Unprotect(PasswordEncrypted);

        public void SetPassword(string plainPassword)
        {
            PasswordEncrypted = SettingsService.Protect(plainPassword);
        }

        public void UpdateSigning(string p12Path, string p12Password, string provisionPath)
        {
            P12Path = p12Path;
            P12Password = p12Password;
            MobileProvisionPath = provisionPath;
            CertExpiry = SettingsService.GetP12Expiry(p12Path, p12Password);
            ProfileExpiry = SigningService.ParseProvisionExpiry(provisionPath);
        }
    }

    public class DeviceSigningConfig
    {
        public string P12Path { get; set; } = string.Empty;
        public string P12Password { get; set; } = string.Empty;
        public string MobileProvisionPath { get; set; } = string.Empty;
    }

    // MARK: - DPAPI 密码加密（登录持久化）

    private static readonly byte[] Entropy = "AltServer.Windows.AccountPassword.v1"u8.ToArray();

    /// <summary>DPAPI 加密（CurrentUser 范围，仅本机当前 Windows 用户可解密）</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>DPAPI 解密；失败返回 null（不抛异常）</summary>
    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取 P12 证书的 NotAfter 有效期；解析失败返回 null</summary>
    public static DateTime? GetP12Expiry(string? p12Path, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(p12Path) || !File.Exists(p12Path)) return null;
            var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                p12Path, password ?? string.Empty,
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            return cert.NotAfter.ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    public SettingsService()
    {
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AltServer");
        Directory.CreateDirectory(appDataDir);
        _settingsDir = appDataDir;
        _settingsPath = Path.Combine(_settingsDir, "settings.json");

        // 若旧 BaseDirectory 存在 settings.json 且 AppData 下不存在，自动迁移
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!File.Exists(_settingsPath) && File.Exists(legacyPath))
        {
            try { File.Copy(legacyPath, _settingsPath, overwrite: true); } catch { }
        }

        Data = Load();
        MigrateLegacy();
    }

    /// <summary>
    /// v1.0.86 迁移：旧版单账号（AppleId + 全局签名文件）→ Accounts / DeviceAccounts / ActiveAccount
    /// </summary>
    private void MigrateLegacy()
    {
        var changed = false;

        if (Data.Accounts.Count == 0 && !string.IsNullOrEmpty(Data.AppleId))
        {
            var acct = Data.GetOrCreateAccount(Data.AppleId);
            if (!string.IsNullOrEmpty(Data.P12Path))
            {
                acct.UpdateSigning(Data.P12Path, Data.P12Password, Data.MobileProvisionPath);
            }
            LogService.Info($"[迁移] 已将账号 {Data.AppleId} 迁移至多账号管理");
            changed = true;
        }

        // 旧版设备配置：为每台已配置设备补建账号绑定
        foreach (var udid in Data.DeviceConfigs.Keys.ToList())
        {
            if (!Data.DeviceAccounts.ContainsKey(udid) && !string.IsNullOrEmpty(Data.AppleId))
            {
                Data.DeviceAccounts[udid] = Data.AppleId;
                changed = true;
            }
        }

        if (Data.ActiveAccount is null && Data.Accounts.Count > 0)
        {
            Data.ActiveAccount = Data.Accounts[0].AppleId;
            changed = true;
        }

        if (changed) Save();
    }

    private Settings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new Settings();

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Reload()
    {
        Data = Load();
    }

    public void Save()
    {
        try
        {
            // 兼容旧字段：AppleId 始终镜像当前激活账号
            if (Data.Accounts.Count > 0)
                Data.AppleId = Data.ActiveAccount ?? Data.Accounts[0].AppleId;

            var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
        }
    }

    /// <summary>解析实际的工具目录（支持绝对路径和相对exe路径）</summary>
    public string ResolveToolsDir()
    {
        if (Path.IsPathRooted(Data.ToolsDir))
            return Data.ToolsDir;

        var baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, Data.ToolsDir));
    }

    /// <summary>生成开机自启动注册表项</summary>
    public void ApplyStartWithWindows(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);

            if (key is null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "AltServer.exe");
                key.SetValue("AltServer", $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue("AltServer", false);
            }

            Data.StartWithWindows = enable;
            Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"设置开机自启动失败: {ex.Message}");
        }
    }
}
