using System.IO;
using System.Text.Json;

namespace AltServer.Windows.Services;

/// <summary>
/// 设置持久化服务：JSON文件存储于 %AppData%/AltServer
/// </summary>
public class SettingsService
{
    private readonly string _settingsDir;
    private readonly string _settingsPath;

    public Settings Data { get; private set; }

    public class Settings
    {
        public string ToolsDir { get; set; } = "tools";
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
    }

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsDir = Path.Combine(appData, "AltServer");
        _settingsPath = Path.Combine(_settingsDir, "settings.json");

        Directory.CreateDirectory(_settingsDir);
        Data = Load();
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

    public void Save()
    {
        try
        {
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