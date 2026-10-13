using System.IO;
using System.Runtime.InteropServices;

namespace AltServer.Windows.Services;

/// <summary>
/// libimobiledevice 原生 API 封装（P/Invoke tools\imobiledevice.dll），
/// 提供 AFC（文件/相册访问）与 screenshotr（设备截屏）能力，供照片管理和投屏预览使用。
/// </summary>
public static class DeviceNative
{
    private static bool _dllDirSet;

    /// <summary>必须在首次 P/Invoke 前调用，把 tools\ 加入 DLL 搜索路径</summary>
    public static void EnsureToolsDirInSearchPath(string toolsDir)
    {
        if (_dllDirSet) return;
        if (Directory.Exists(toolsDir))
        {
            SetDllDirectory(toolsDir);
            _dllDirSet = true;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    // MARK: - 原生导入

    private const string Lib = "imobiledevice.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int idevice_new(out IntPtr device, string udid);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int idevice_free(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lockdownd_client_new_with_handshake(IntPtr device, out IntPtr client, string label);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lockdownd_client_free(IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lockdownd_start_service(IntPtr client, string service, out IntPtr serviceDescriptor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lockdownd_start_service_with_escrow_bag(IntPtr client, string service, out IntPtr serviceDescriptor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lockdownd_service_descriptor_free(IntPtr serviceDescriptor);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_client_new(IntPtr device, IntPtr serviceDescriptor, out IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_client_free(IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_read_directory(IntPtr client, string path, out IntPtr entries);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_get_file_info(IntPtr client, string path, out IntPtr info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_file_open(IntPtr client, string filename, int fileMode, out ulong handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_file_read(IntPtr client, ulong handle, byte[] data, int length, out uint bytesRead);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_file_write(IntPtr client, ulong handle, byte[] data, int length, out uint bytesWritten);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_file_close(IntPtr client, ulong handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_make_directory(IntPtr client, string dirname);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_remove_path(IntPtr client, string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int afc_rename_path(IntPtr client, string fromPath, string toPath);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int screenshotr_client_new(IntPtr device, IntPtr serviceDescriptor, out IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int screenshotr_client_free(IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int screenshotr_take_screenshot(IntPtr client, out IntPtr imageData, out ulong imageSize);

    // libimobiledevice 使用 MSVC 动态 UCRT 分配堆内存，字符串数组与截图缓冲需要用同一 CRT 释放
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void free(IntPtr ptr);

    // MARK: - 错误

    public class DeviceNativeException : Exception
    {
        public int ErrorCode { get; }
        public DeviceNativeException(string api, int code) : base($"{api} 失败 (错误码 {code})") => ErrorCode = code;
    }

    private static void Check(string api, int code)
    {
        if (code != 0) throw new DeviceNativeException(api, code);
    }

    /// <summary>读取 char**（NULL 结尾的 UTF-8 字符串数组）</summary>
    private static string[] ReadStringArray(IntPtr arrayPtr)
    {
        var result = new List<string>();
        var p = arrayPtr;
        for (var i = 0; ; i++)
        {
            var item = Marshal.ReadIntPtr(p, i * IntPtr.Size);
            if (item == IntPtr.Zero) break;
            result.Add(Marshal.PtrToStringUTF8(item) ?? string.Empty);
        }
        return result.ToArray();
    }

    private static void FreeStringArray(IntPtr arrayPtr)
    {
        if (arrayPtr == IntPtr.Zero) return;
        for (var i = 0; ; i++)
        {
            var item = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
            if (item == IntPtr.Zero) break;
            free(item);
        }
        free(arrayPtr);
    }

    // MARK: - 设备会话

    /// <summary>
    /// 一台设备的 lockdown 会话：持有 idevice_t 与 lockdownd_client_t，
    /// 可按需创建 AFC / screenshotr 服务客户端。
    /// </summary>
    public sealed class IdeviceSession : IDisposable
    {
        private IntPtr _device;
        private IntPtr _lockdown;
        private readonly string _udid;

        public IdeviceSession(string udid)
        {
            _udid = udid;
            Check("idevice_new", idevice_new(out _device, udid));
            Check("lockdownd_client_new_with_handshake", lockdownd_client_new_with_handshake(_device, out _lockdown, "AltServer"));
        }

        /// <summary>启动 lockdown 服务：优先使用 escrow bag（iOS 17+ 的 screenshotr/afc 等服务需要）；失败则回落到无 escrow bag 版本</summary>
        private IntPtr StartService(string serviceName)
        {
            // 优先使用 with_escrow_bag：iOS 17+ 的 screenshotr、afc 等服务需要
            // escrow bag 提供更高信任级别，否则 lockdownd 返回 -27
            var rc = lockdownd_start_service_with_escrow_bag(_lockdown, serviceName, out var descriptor);
            if (rc == 0) return descriptor;

            // 回落到普通版本（兼容旧版 libimobiledevice 或不需要 escrow bag 的服务）
            Check($"lockdownd_start_service({serviceName})", lockdownd_start_service(_lockdown, serviceName, out descriptor));
            return descriptor;
        }

        public AfcClient CreateAfcClient()
        {
            var descriptor = StartService("com.apple.afc");
            try
            {
                Check("afc_client_new", afc_client_new(_device, descriptor, out var client));
                return new AfcClient(client);
            }
            finally
            {
                lockdownd_service_descriptor_free(descriptor);
            }
        }

        public ScreenshotrClient CreateScreenshotrClient()
        {
            var descriptor = StartService("com.apple.mobile.screenshotr");
            try
            {
                Check("screenshotr_client_new", screenshotr_client_new(_device, descriptor, out var client));
                return new ScreenshotrClient(client);
            }
            finally
            {
                lockdownd_service_descriptor_free(descriptor);
            }
        }

        public void Dispose()
        {
            if (_lockdown != IntPtr.Zero) { lockdownd_client_free(_lockdown); _lockdown = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { idevice_free(_device); _device = IntPtr.Zero; }
        }
    }

    // MARK: - AFC 客户端

    /// <summary>AFC 文件客户端（线程安全）。AFC 根目录即设备媒体分区：DCIM、PhotoData 等。</summary>
    public sealed class AfcClient : IDisposable
    {
        private IntPtr _client;
        private readonly object _lock = new();

        internal AfcClient(IntPtr client) => _client = client;

        public sealed class DirEntry
        {
            public string Name { get; init; } = "";
            public bool IsDirectory { get; init; }
            public long SizeBytes { get; init; }
            public DateTime Modified { get; init; }
        }

        /// <summary>列出目录内容（含 "." ".."，已过滤）</summary>
        public DirEntry[] ListDirectory(string path)
        {
            lock (_lock)
            {
                Check("afc_read_directory", afc_read_directory(_client, path, out var entries));
                try
                {
                    var names = ReadStringArray(entries)
                        .Where(n => n is not ("." or ".."))
                        .ToList();

                    return names.Select(n =>
                    {
                        var info = GetFileInfoInternal($"{path.TrimEnd('/')}/{n}");
                        var isDir = info.TryGetValue("st_ifmt", out var ifmt) && ifmt.Contains("S_IFDIR");
                        var size = info.TryGetValue("st_size", out var sz) && long.TryParse(sz, out var s) ? s : 0;
                        var mtime = info.TryGetValue("st_mtime", out var mt) && long.TryParse(mt, out var m)
                            ? DateTimeOffset.FromUnixTimeSeconds(m).LocalDateTime
                            : DateTime.MinValue;
                        return new DirEntry { Name = n, IsDirectory = isDir, SizeBytes = size, Modified = mtime };
                    }).ToArray();
                }
                finally
                {
                    FreeStringArray(entries);
                }
            }
        }

        public Dictionary<string, string> GetFileInfo(string path)
        {
            lock (_lock) return GetFileInfoInternal(path);
        }

        private Dictionary<string, string> GetFileInfoInternal(string path)
        {
            Check("afc_get_file_info", afc_get_file_info(_client, path, out var info));
            try
            {
                var items = ReadStringArray(info);
                var dict = new Dictionary<string, string>();
                for (var i = 0; i + 1 < items.Length; i += 2)
                {
                    dict[items[i]] = items[i + 1];
                }
                return dict;
            }
            finally
            {
                FreeStringArray(info);
            }
        }

        public byte[] ReadFile(string path, CancellationToken ct = default, Action<long>? progress = null)
        {
            lock (_lock)
            {
                Check("afc_file_open", afc_file_open(_client, path, 1 /* RDONLY */, out var handle));
                try
                {
                    using var ms = new MemoryStream();
                    var buffer = new byte[256 * 1024];
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        Check("afc_file_read", afc_file_read(_client, handle, buffer, buffer.Length, out var read));
                        if (read == 0) break;
                        ms.Write(buffer, 0, (int)read);
                        progress?.Invoke(ms.Length);
                    }
                    return ms.ToArray();
                }
                finally
                {
                    afc_file_close(_client, handle);
                }
            }
        }

        /// <summary>流式读取到本地文件（大视频不占内存）</summary>
        public void StreamReadFile(string path, string destPath, CancellationToken ct = default)
        {
            lock (_lock)
            {
                Check("afc_file_open", afc_file_open(_client, path, 1 /* RDONLY */, out var handle));
                try
                {
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
                    var buffer = new byte[256 * 1024];
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        Check("afc_file_read", afc_file_read(_client, handle, buffer, buffer.Length, out var read));
                        if (read == 0) break;
                        fs.Write(buffer, 0, (int)read);
                    }
                }
                finally
                {
                    afc_file_close(_client, handle);
                }
            }
        }

        public void WriteFile(string path, byte[] data, CancellationToken ct = default)
        {
            lock (_lock)
            {
                Check("afc_file_open", afc_file_open(_client, path, 2 /* "w" 创建/截断写入 */, out var handle));
                try
                {
                    var offset = 0;
                    while (offset < data.Length)
                    {
                        ct.ThrowIfCancellationRequested();
                        var chunk = Math.Min(256 * 1024, data.Length - offset);
                        Check("afc_file_write", afc_file_write(_client, handle, data, chunk, out var written));
                        if (written == 0) throw new IOException($"AFC 写入停滞: {path}");
                        offset += (int)written;
                    }
                }
                finally
                {
                    afc_file_close(_client, handle);
                }
            }
        }

        public void MakeDirectory(string path)
        {
            lock (_lock) Check("afc_make_directory", afc_make_directory(_client, path));
        }

        public void Remove(string path)
        {
            lock (_lock) Check("afc_remove_path", afc_remove_path(_client, path));
        }

        public void Rename(string fromPath, string toPath)
        {
            lock (_lock) Check("afc_rename_path", afc_rename_path(_client, fromPath, toPath));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_client != IntPtr.Zero) { afc_client_free(_client); _client = IntPtr.Zero; }
            }
        }
    }

    // MARK: - 截屏客户端

    /// <summary>screenshotr 截屏客户端（线程安全）：返回 PNG 数据。设备需已解锁。</summary>
    public sealed class ScreenshotrClient : IDisposable
    {
        private IntPtr _client;
        private readonly object _lock = new();

        internal ScreenshotrClient(IntPtr client) => _client = client;

        /// <summary>截取一帧，返回 PNG 字节。设备锁屏/未信任时会抛出异常。</summary>
        public byte[] TakeScreenshot()
        {
            lock (_lock)
            {
                Check("screenshotr_take_screenshot", screenshotr_take_screenshot(_client, out var data, out var size));
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(data, bytes, 0, (int)size);
                    return bytes;
                }
                finally
                {
                    free(data);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_client != IntPtr.Zero) { screenshotr_client_free(_client); _client = IntPtr.Zero; }
            }
        }
    }
}
