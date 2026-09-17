using System.IO;
using System.Runtime.InteropServices;

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
                routing = Marshal.ReadInt64(rSlot);

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
                    LocalUserId = "-2",
                    RoutingInfo = routing,
                    DeviceUniqueId = Guid.NewGuid().ToString("N")[..32],
                    SerialNumber = "F2LX1234ABCD",
                    ClientInfo = AnisetteData.DefaultClientInfo,
                    Locale = "en_US",
                    TimeZone = "UTC",
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
}
