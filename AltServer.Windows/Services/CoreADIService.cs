using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltServer.Windows.Services;

/// <summary>
/// Loads Apple's CoreADI.dll (bundled in an `an` directory) and produces
/// Apple-valid anisette. Verified working on this machine against the x64
/// CoreADI.dll shipped with iTunes / Apple Application Support:
///   vdfut768ig export present; Init dispatch ret=0 (note: Init does NOT
///   overwrite the payload, so the marker stays ==1 -- judge Init by the
///   dispatch return value, not the payload status);
///   GetIDMSRouting(-2) status=0, RoutingInfo=33883392;
///   RequestOTP(-2) status=0, MID=60 bytes, OTP=28 bytes.
///
/// Message layout: payload = [u32 enc=1][u32 marker=1] + msg (big-endian).
/// A single export `vdfut768ig` dispatches by magic function code.
/// </summary>
public static class CoreADIService
{
    private const uint MagicInit = 0x12db31c5;
    private const uint MagicIsProvisioned = 0xb0eda7af;
    private const uint MagicGetIdmsRouting = 0x85fe63b0;
    private const uint MagicRequestOtp = 0xcfe0b46a;

    [StructLayout(LayoutKind.Sequential)]
    private struct CoreADIParameters
    {
        public IntPtr Payload;
        public uint InputSize;
        public uint OutputSize;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DispatchFn(uint functionCode, ref CoreADIParameters parameters);

    /// <summary>Fetch anisette via a bundled CoreADI.dll. Returns null if unavailable.</summary>
    public static AnisetteData? TryFetchAnisette(string toolsDir)
    {
        // 优先使用官方原版 iCloud Anisette.exe 执行体（若存在）
        var fromExe = TryFetchFromAnisetteExe(toolsDir);
        if (fromExe != null) return fromExe;

        try
        {
            var dllPath = LocateCoreAdiDll(toolsDir);
            if (dllPath == null) return null;

            var anDir = Path.GetDirectoryName(dllPath)!;
            var lib = LoadLibraryExW(dllPath, IntPtr.Zero, 0x100 | 0x1000);
            if (lib == IntPtr.Zero) return null;

            try
            {
                var proc = GetProcAddress(lib, "vdfut768ig");
                if (proc == IntPtr.Zero) return null;

                var dispatch = Marshal.GetDelegateForFunctionPointer<DispatchFn>(proc);
                var prevDir = Environment.CurrentDirectory;
                Environment.CurrentDirectory = anDir;
                try
                {
                    return FetchWithDll(dispatch);
                }
                finally
                {
                    Environment.CurrentDirectory = prevDir;
                }
            }
            finally
            {
                FreeLibrary(lib);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CoreADI load failed: {ex.Message}");
        }
        return null;
    }

    private static string? LocateCoreAdiDll(string toolsDir)
    {
        foreach (var dir in new[] { Path.Combine(toolsDir, "an"), toolsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var name in new[] { "CoreADI.dll", "libCoreADI.dll" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static AnisetteData? FetchWithDll(DispatchFn dispatch)
    {
        var (_, initRet) = Call(dispatch, MagicInit, Array.Empty<byte>());
        if (initRet != 0) return null;

        // IsMachineProvisioned(-2): loads the machine's ADI provisioning state
        // into memory. REQUIRED before GetIDMSRouting/RequestOTP -- without it,
        // RequestOTP uses uninitialized provisioning and returns a MID that GSA
        // rejects with ec=-29004 ("possible environment mismatch").
        var imp = new byte[8];
        PutI64(imp, 0, -2);
        Call(dispatch, MagicIsProvisioned, imp);

        // GetIDMSRouting(-2): msg = [i64 ds_id][u64 raw ptr to 8-byte buffer]
        var rSlot = Marshal.AllocHGlobal(8);
        try
        {
            var gim = new byte[16];
            PutI64(gim, 0, -2);
            PutU64(gim, 8, (ulong)rSlot.ToInt64());
            var (gr, grRet) = Call(dispatch, MagicGetIdmsRouting, gim);
            long routing = 17106176L;
            if (grRet == 0 && GetI32(gr, 0) == 0)
            {
                routing = Marshal.ReadInt64(rSlot);
            }
            else
            {
                // 静默回退到硬编码 RINFO 会让 anisette 的"产品线"标识与本机的真实值不一致。
                // v1.0.89 拿到的是本机真实值 33883392，v1.0.97 起这里失败并悄悄用了 17106176。
                LogService.Warning(
                    $"[Anisette] CoreADI GetIDMSRouting 失败 (ret=0x{grRet:X8}, status={GetI32(gr, 0)})，" +
                    $"回退 RINFO=17106176；本机真实路由值取不到，可能与苹果侧的产品线校验不一致。");
            }

            // RequestOTP(-2): msg = [i64 ds_id][u64 midPtr][u64 midLenPtr][u64 otpPtr][u64 otpLenPtr]
            var midPtr = Marshal.AllocHGlobal(1024);
            var midLen = Marshal.AllocHGlobal(4);
            var otpPtr = Marshal.AllocHGlobal(1024);
            var otpLen = Marshal.AllocHGlobal(4);
            try
            {
                var rom = new byte[40];
                PutI64(rom, 0, -2);
                PutU64(rom, 8, (ulong)midPtr.ToInt64());
                PutU64(rom, 16, (ulong)midLen.ToInt64());
                PutU64(rom, 24, (ulong)otpPtr.ToInt64());
                PutU64(rom, 32, (ulong)otpLen.ToInt64());
                var (rr, rrRet) = Call(dispatch, MagicRequestOtp, rom);
                if (rrRet != 0 || GetI32(rr, 0) != 0) return null;

                var ml = Marshal.ReadInt32(midLen);
                var ol = Marshal.ReadInt32(otpLen);
                if (ml <= 0 || ml > 1024 || ol <= 0 || ol > 1024) return null;

                // CoreADI writes a POINTER to the (internally-owned) MID/OTP
                // data buffer into the midPtr/otpPtr slots -- it does NOT write
                // the bytes inline. Dereference the slot to get the data
                // pointer, then copy. Reading the slot directly yields the
                // pointer value (a per-run heap address) as the first bytes,
                // producing a MID that GSA rejects with ec=-29004.
                var mid = new byte[ml];
                var otp = new byte[ol];
                Marshal.Copy(Marshal.ReadIntPtr(midPtr), mid, 0, ml);
                Marshal.Copy(Marshal.ReadIntPtr(otpPtr), otp, 0, ol);

                return new AnisetteData
                {
                    MachineId = Convert.ToBase64String(mid),
                    OneTimePassword = Convert.ToBase64String(otp),
                    LocalUserId = GetPersistentLocalUserId(),
                    RoutingInfo = routing,
                    DeviceUniqueId = GetPersistentDeviceId(),
                    SerialNumber = "C02LKHBBFD57",
                    ClientInfo = "<PC> <Windows;6.2(0,0);9200> <com.apple.AuthKitWin/1 (com.apple.iCloud/7.17)>",
                    Locale = "en_GB",
                    TimeZone = "GMT+0",
                    ClientTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
            }
            finally
            {
                Marshal.FreeHGlobal(midPtr);
                Marshal.FreeHGlobal(midLen);
                Marshal.FreeHGlobal(otpPtr);
                Marshal.FreeHGlobal(otpLen);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(rSlot);
        }
    }

    private static (byte[] payload, int ret) Call(DispatchFn dispatch, uint magic, byte[] msg)
    {
        var payload = new byte[8 + msg.Length];
        PutU32(payload, 0, 1); // enc
        PutU32(payload, 4, 1); // marker
        Buffer.BlockCopy(msg, 0, payload, 8, msg.Length);

        var gc = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var pars = new CoreADIParameters
            {
                Payload = gc.AddrOfPinnedObject(),
                InputSize = (uint)payload.Length,
                OutputSize = 4,
                Flags = 0
            };
            // dispatch ret is the authoritative status for every call. Note that
            // Init does NOT overwrite the payload (the marker stays ==1), so the
            // payload status must not be used to judge Init success -- only ret.
            // GIM / RequestOTP additionally write status 0 into payload[0..4].
            int ret = dispatch(magic, ref pars);
            return (payload, ret);
        }
        finally
        {
            gc.Free();
        }
    }

    private static int GetI32(byte[] p, int off)
        => (p[off] << 24) | (p[off + 1] << 16) | (p[off + 2] << 8) | p[off + 3];

    private static void PutU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }

    private static void PutI64(byte[] buf, int off, long v)
    {
        var uv = unchecked((ulong)v);
        for (int i = 0; i < 8; i++) buf[off + i] = (byte)(uv >> (56 - 8 * i));
    }

    private static void PutU64(byte[] buf, int off, ulong v)
    {
        for (int i = 0; i < 8; i++) buf[off + i] = (byte)(v >> (56 - 8 * i));
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetCurrentDirectoryW(string path);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr hFile, uint flags);

    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private static string? _cachedDeviceId;
    private static string? _cachedLocalUserId;

    public static string GetPersistentDeviceId()
    {
        if (!string.IsNullOrEmpty(_cachedDeviceId)) return _cachedDeviceId;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrEmpty(guid))
            {
                using var md5 = MD5.Create();
                var hash1 = md5.ComputeHash(Encoding.UTF8.GetBytes(guid));
                var hash2 = md5.ComputeHash(Encoding.UTF8.GetBytes(guid + "-device"));
                var combined = hash1.Concat(hash2).Take(28).ToArray();
                var parts = new List<string>();
                for (int i = 0; i < combined.Length; i += 4)
                {
                    parts.Add(BitConverter.ToString(combined, i, 4).Replace("-", ""));
                }
                _cachedDeviceId = string.Join(".", parts);
                return _cachedDeviceId;
            }
        }
        catch { }
        _cachedDeviceId = "A4D9E3A4.346613DD.9A5E8771.DB87980F.1DD97891.EE3411D0.6BD27B52";
        return _cachedDeviceId;
    }

    public static string GetPersistentLocalUserId()
    {
        if (!string.IsNullOrEmpty(_cachedLocalUserId)) return _cachedLocalUserId;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrEmpty(guid))
            {
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(guid + "-lu"));
                _cachedLocalUserId = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                return _cachedLocalUserId;
            }
        }
        catch { }
        _cachedLocalUserId = "379D2D200DEFB6B8A3A218053471D202";
        return _cachedLocalUserId;
    }

    private static AnisetteData? TryFetchFromAnisetteExe(string toolsDir)
    {
        var candidates = new[]
        {
            Path.Combine(toolsDir, "Anisette.exe"),
            Path.Combine(toolsDir, "an", "Anisette.exe"),
            @"C:\Program Files\i4Tools9\iCloudDll\Anisette.exe",
            @"C:\Program Files (x86)\i4Tools9\iCloudDll\Anisette.exe"
        };

        foreach (var exe in candidates)
        {
            if (!File.Exists(exe)) continue;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                if (!p.WaitForExit(6000))
                {
                    try { p.Kill(); } catch { }
                    continue;
                }
                var stdout = p.StandardOutput.ReadToEnd();
                if (string.IsNullOrWhiteSpace(stdout) || !stdout.Contains("X-Apple-I-MD")) continue;

                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                return new AnisetteData
                {
                    MachineId = root.TryGetProperty("X-Apple-I-MD-M", out var mid) ? mid.GetString() ?? "" : "",
                    OneTimePassword = root.TryGetProperty("X-Apple-I-MD", out var otp) ? otp.GetString() ?? "" : "",
                    LocalUserId = root.TryGetProperty("X-Apple-I-MD-LU", out var lu) ? lu.GetString() ?? "" : GetPersistentLocalUserId(),
                    RoutingInfo = root.TryGetProperty("X-Apple-I-MD-RINFO", out var rinfo) && long.TryParse(rinfo.GetString(), out var r) ? r : 17106176,
                    DeviceUniqueId = root.TryGetProperty("X-Mme-Device-Id", out var did) ? did.GetString() ?? "" : GetPersistentDeviceId(),
                    SerialNumber = root.TryGetProperty("X-Apple-I-SRL-NO", out var srl) ? srl.GetString() ?? "" : "C02LKHBBFD57",
                    ClientInfo = root.TryGetProperty("X-MMe-Client-Info", out var ci) ? ci.GetString() ?? "" : AnisetteData.DefaultClientInfo,
                    Locale = root.TryGetProperty("X-Apple-Locale", out var loc) ? loc.GetString() ?? "" : "en_GB",
                    TimeZone = root.TryGetProperty("X-Apple-I-TimeZone", out var tz) ? tz.GetString() ?? "" : "GMT+0",
                    ClientTime = root.TryGetProperty("X-Apple-I-Client-Time", out var ct) ? ct.GetString() ?? "" : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Anisette.exe failed: {ex.Message}");
            }
        }
        return null;
    }
}
