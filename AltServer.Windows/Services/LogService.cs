namespace AltServer.Windows.Services;

/// <summary>
/// 日志服务：控制台输出 + 文件记录 (%AppData%/AltServer/logs)
/// </summary>
public static class LogService
{
    private static readonly object _lock = new();
    private static string? _logFile;

    public static event Action<string, LogLevel>? LogReceived;

    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    public static void Initialize()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = Path.Combine(appData, "AltServer", "logs");
            Directory.CreateDirectory(logDir);

            var fileName = $"altserver_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            _logFile = Path.Combine(logDir, fileName);

            // 清理7天前的日志
            foreach (var oldFile in Directory.GetFiles(logDir, "altserver_*.log"))
            {
                var info = new FileInfo(oldFile);
                if (info.LastWriteTime < DateTime.Now.AddDays(-7))
                {
                    try { info.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // 日志初始化失败不影响主程序
        }
    }

    public static void Info(string message) => Write(message, LogLevel.Info);
    public static void Success(string message) => Write(message, LogLevel.Success);
    public static void Warning(string message) => Write(message, LogLevel.Warning);
    public static void Error(string message) => Write(message, LogLevel.Error);
    public static void Error(string message, Exception ex) => Write($"{message}\n{ex}", LogLevel.Error);

    private static void Write(string message, LogLevel level)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var fullMessage = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            // 文件日志
            if (_logFile is not null)
            {
                try { File.AppendAllText(_logFile, fullMessage + Environment.NewLine); } catch { }
            }
        }

        // UI日志事件
        LogReceived?.Invoke(fullMessage, level);
    }
}