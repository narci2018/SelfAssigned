using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple SRP-6a implementation, byte-for-byte compatible with the `srp` crate
/// v0.7.0-rc.1 (RustCrypto/PAKEs) used by apple-crates/grandslam.
///
/// Verified live against gsa.apple.com:
/// - group G2048 = RFC5054-style prime used by the RustCrypto srp crate
///   (starts 0xac6bdb41..., NOT the RFC5054 appendix FFFFFFFF... prime)
/// - username_in_x = false  =>  identity_hash = H(":" | processed_password)
/// - x = H(salt | identity_hash)
/// - u = H(A | B)  over the EXACT trimmed bytes sent as A2k
/// - S = (B - k*g^x)^(a + u*x) mod N
/// - K = H(S_trimmed)
/// - M1 = H(H(N) XOR H(PAD(g)) | H(username) | salt | A | B | K)
/// - M2 = H(A | M1 | K)
/// - spd decrypt: key = HMAC-SHA256(S_raw, "extra data key:"),
///   iv = HMAC-SHA256(S_raw, "extra data iv:")[..16], AES-256-CBC + PKCS7
/// </summary>
public static class AppleSrp
{
    // G2048 prime from srp crate 0.7.0-rc.1 (groups.rs)
    private static readonly byte[] NBytes = HexToBytes(
        "ac6bdb41324a9a9bf166de5e1389582faf72b6651987ee07fc3192943db56050a37329cbb4a099ed8193e0757767a13dd52312ab4b03310dcd7f48a9da04fd50e8083969edb767b0cf6095179a163ab3661a05fbd5faaae82918a9962f0b93b855f97993ec975eeaa80d740adbf4ff747359d041d5c33ea71d281e446b14773bca97b43a23fb801676bd207a436c6481f1d2b9078717461a5b9d32e688f87748544523b524b0d57d5ea77a2775d2ecfa032cfbdbf52fb3786160279004e57ae6af874e7303ce53299ccc041c7bc308d82a5698f3a8d0c38271ae35f8e9dbfbb694b5c803d89f7ae435de236d525f54759b65e372fcd68ef20fa7111f9e4aff73");

    private static readonly BigInteger N = ToBigInt(NBytes);
    private static readonly BigInteger G = new BigInteger(new byte[] { 2 }, isUnsigned: true);

    // PAD(g): 256-byte big-endian representation of generator g=2
    private static readonly byte[] PaddedG;
    // H(N) and H(PAD(g)) — used in M1 = H( H(N) XOR H(PAD(g)) || ... )
    private static readonly byte[] NHash;
    private static readonly byte[] GHash;
    // k = H(N || PAD(g)) — single SHA-256 of raw N bytes + padded g
    private static readonly byte[] kHash;

    static AppleSrp()
    {
        PaddedG = new byte[256];
        PaddedG[255] = 2;
        NHash = SHA256.HashData(NBytes);
        GHash = SHA256.HashData(PaddedG);
        kHash = SHA256.HashData(NBytes.Concat(PaddedG).ToArray());
    }

    // Store private value 'a' across GeneratePublicKey and ComputeProof
    private static byte[]? _privateA;
    // Store trimmed public A (exactly what was POSTed) for M1/M2
    private static byte[]? _trimmedA;

    /// <summary>
    /// Generate client public key A = g^a mod N.
    /// Returns the TRIMMED big-endian bytes (no leading zeros), exactly what
    /// apple-crates' compute_public_ephemeral() sends as A2k.
    /// </summary>
    public static byte[] GeneratePublicKey()
    {
        var a = new byte[256];
        RandomNumberGenerator.Fill(a);
        a[0] |= 0x80; // ensure high bit set
        _privateA = a;

        var bigA = BigInteger.ModPow(G, ToBigInt(a), N);
        _trimmedA = FromBigInt(bigA);
        return _trimmedA;
    }

    /// <summary>
    /// Derive password key using PBKDF2-HMAC-SHA256.
    /// s2k:    hashed = SHA256(password)
    /// s2k_fo: hashed = ASCII(hex(SHA256(password)))
    /// P = PBKDF2(hashed, salt, iterations, 32)
    /// </summary>
    public static byte[] DerivePassword(string password, byte[] salt, int iterations, bool s2kFo)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        if (s2kFo)
        {
            digest = Encoding.ASCII.GetBytes(Convert.ToHexString(digest).ToLowerInvariant());
        }
        return Rfc2898DeriveBytes.Pbkdf2(digest, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>
    /// Compute the SRP-6a client proof M1.
    /// M1 = H(H(N) XOR H(PAD(g)) | H(username) | salt | A | B | K)
    /// where K = H(S) and S is the trimmed premaster secret.
    /// </summary>
    public static byte[] ComputeProof(byte[] salt, string password, byte[] A, byte[] B,
        string username, int iterations, bool s2kFo)
    {
        var a = ToBigInt(_privateA ?? throw new InvalidOperationException("GeneratePublicKey must be called first"));

        // x = H(salt | H(":" | P))  (username_in_x = false)
        var processed = DerivePassword(password, salt, iterations, s2kFo);
        var colonPassword = new byte[1 + processed.Length];
        colonPassword[0] = 0x3a;
        Buffer.BlockCopy(processed, 0, colonPassword, 1, processed.Length);
        var identityHash = SHA256.HashData(colonPassword);
        var x = ToBigInt(SHA256.HashData(salt.Concat(identityHash).ToArray()));

        var v = BigInteger.ModPow(G, x, N);

        // u = H(A | B) — A is the trimmed bytes actually sent
        var u = ToBigInt(SHA256.HashData(A.Concat(B).ToArray()));

        // S = (B - k*v)^(a + u*x) mod N
        var bigB = ToBigInt(B);
        var k = ToBigInt(kHash);

        var kv = (bigB - k * v) % N;
        if (kv.Sign < 0) kv += N;
        var exp = a.Sign < 0 ? BigInteger.Add(a, u * x) : a + u * x;
        var S = BigInteger.ModPow(kv, exp, N);

        var sTrimmed = FromBigInt(S);
        var K = SHA256.HashData(sTrimmed);

        // M1 = H(H(N) XOR H(PAD(g)) | H(username) | salt | A | B | K)
        var hNg = NHash.Zip(GHash, (a_, b) => (byte)(a_ ^ b)).ToArray();
        var hUser = SHA256.HashData(Encoding.UTF8.GetBytes(username));
        var M1 = SHA256.HashData(hNg.Concat(hUser).Concat(salt).Concat(A).Concat(B).Concat(K).ToArray());

        return M1;
    }

    /// <summary>
    /// Verify server proof M2 = H(A | M1 | K).
    /// </summary>
    public static bool VerifyServerProof(byte[] A, byte[] M1, byte[] serverM2,
        byte[] salt, string password, byte[] B, string username, int iterations, bool s2kFo)
    {
        var a = ToBigInt(_privateA ?? throw new InvalidOperationException("GeneratePublicKey must be called first"));

        var processed = DerivePassword(password, salt, iterations, s2kFo);
        var colonPassword = new byte[1 + processed.Length];
        colonPassword[0] = 0x3a;
        Buffer.BlockCopy(processed, 0, colonPassword, 1, processed.Length);
        var identityHash = SHA256.HashData(colonPassword);
        var x = ToBigInt(SHA256.HashData(salt.Concat(identityHash).ToArray()));
        var v = BigInteger.ModPow(G, x, N);
        var u = ToBigInt(SHA256.HashData(A.Concat(B).ToArray()));
        var k = ToBigInt(kHash);
        var bigB = ToBigInt(B);

        var kv = (bigB - k * v) % N;
        if (kv.Sign < 0) kv += N;
        var exp = a.Sign < 0 ? BigInteger.Add(a, u * x) : a + u * x;
        var S = BigInteger.ModPow(kv, exp, N);

        var sTrimmed = FromBigInt(S);
        var K = SHA256.HashData(sTrimmed);

        var expectedM2 = SHA256.HashData(A.Concat(M1).Concat(K).ToArray());
        return expectedM2.SequenceEqual(serverM2);
    }

    /// <summary>
    /// Decrypt the server-provided data (spd) using the raw premaster secret S.
    /// key = HMAC-SHA256(S, "extra data key:")
    /// iv  = HMAC-SHA256(S, "extra data iv:")[..16]
    /// AES-256-CBC + PKCS7
    /// </summary>
    public static byte[] DecryptServerProvidedData(byte[] salt, string password, byte[] A, byte[] B,
        string username, int iterations, bool s2kFo, byte[] spd)
    {
        var a = ToBigInt(_privateA ?? throw new InvalidOperationException("GeneratePublicKey must be called first"));

        var processed = DerivePassword(password, salt, iterations, s2kFo);
        var colonPassword = new byte[1 + processed.Length];
        colonPassword[0] = 0x3a;
        Buffer.BlockCopy(processed, 0, colonPassword, 1, processed.Length);
        var identityHash = SHA256.HashData(colonPassword);
        var x = ToBigInt(SHA256.HashData(salt.Concat(identityHash).ToArray()));
        var v = BigInteger.ModPow(G, x, N);
        var u = ToBigInt(SHA256.HashData(A.Concat(B).ToArray()));
        var k = ToBigInt(kHash);
        var bigB = ToBigInt(B);

        var kv = (bigB - k * v) % N;
        if (kv.Sign < 0) kv += N;
        var exp = a.Sign < 0 ? BigInteger.Add(a, u * x) : a + u * x;
        var S = BigInteger.ModPow(kv, exp, N);

        var sTrimmed = FromBigInt(S);

        return DecryptSpdWithSessionKey(sTrimmed, spd);
    }

    /// <summary>
    /// Decrypt spd given the raw session key (premaster secret) bytes.
    /// </summary>
    public static byte[] DecryptSpdWithSessionKey(byte[] sessionKey, byte[] spd)
    {
        var key = HmacSha256(sessionKey, "extra data key:");
        var ivFull = HmacSha256(sessionKey, "extra data iv:");
        var iv = new byte[16];
        Buffer.BlockCopy(ivFull, 0, iv, 0, 16);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(spd, 0, spd.Length);
    }

    /// <summary>
    /// Compute the apptokens checksum:
    /// HMAC-SHA256(sessionKey, "apptokens" | adsid | appIdentifier)
    /// </summary>
    public static byte[] MakeChecksum(byte[] sessionKey, string adsid, string appIdentifier)
    {
        var input = Encoding.UTF8.GetBytes("apptokens" + adsid + appIdentifier);
        using var hmac = new HMACSHA256(sessionKey);
        return hmac.ComputeHash(input);
    }

    private static byte[] HmacSha256(byte[] key, string message)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private static BigInteger ToBigInt(byte[] bytes)
    {
        return new BigInteger(bytes.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);
    }

    private static byte[] FromBigInt(BigInteger value)
    {
        // ToByteArray() is little-endian (with a sign byte); reverse to big-endian
        // and drop leading zero padding.
        return value.ToByteArray().Reverse().SkipWhile(b => b == 0).ToArray();
    }

    private static byte[] HexToBytes(string hex)
    {
        return Enumerable.Range(0, hex.Length / 2)
            .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
            .ToArray();
    }
}

public class AnisetteData
{
    public const string DefaultClientInfo =
        "<PC> <Windows;10.0.26100> <com.apple.AuthKitWin/1 (com.apple.iTunes/12.13.7)>";

    public string MachineId { get; set; } = string.Empty;
    public string OneTimePassword { get; set; } = string.Empty;
    public string LocalUserId { get; set; } = string.Empty;
    public long RoutingInfo { get; set; }
    public string DeviceUniqueId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string ClientInfo { get; set; } = DefaultClientInfo;
    public string Locale { get; set; } = "en_US";
    public string TimeZone { get; set; } = "UTC";
    public string ClientTime { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public Dictionary<string, string> ToHeaders()
    {
        return new Dictionary<string, string>
        {
            ["X-Apple-I-Client-Time"] = ClientTime,
            ["X-Apple-I-TimeZone"] = TimeZone,
            ["X-Apple-Locale"] = Locale,
            ["X-Apple-I-MD"] = OneTimePassword,
            ["X-Apple-I-MD-LU"] = LocalUserId,
            ["X-Apple-I-MD-M"] = MachineId,
            ["X-Apple-I-MD-RINFO"] = RoutingInfo.ToString(),
            ["X-Mme-Device-Id"] = DeviceUniqueId,
            ["X-Apple-I-SRL-NO"] = SerialNumber,
            ["X-MMe-Client-Info"] = ClientInfo
        };
    }

    public Dictionary<string, object> ToCpd()
    {
        return new Dictionary<string, object>
        {
            ["bootstrap"] = true,
            ["capp"] = "akd",
            ["ckgen"] = true,
            ["icscrec"] = true,
            ["loc"] = Locale,
            ["pbe"] = false,
            ["prkgen"] = true,
            ["svct"] = "iCloud",
            ["X-Apple-I-Client-Time"] = ClientTime,
            ["X-Apple-I-TimeZone"] = TimeZone,
            ["X-Apple-Locale"] = Locale,
            ["X-Apple-I-MD"] = OneTimePassword,
            ["X-Apple-I-MD-M"] = MachineId,
            ["X-Apple-I-MD-RINFO"] = RoutingInfo.ToString(),
            ["X-Mme-Device-Id"] = DeviceUniqueId,
        };
    }
}
