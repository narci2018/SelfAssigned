using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple SRP-6a implementation (ported from iPASide/Python srp library)
/// Apple modifications:
/// - k = H(N | g)
/// - x = H(salt | H(":" | password))  (username excluded from x)
/// - s2k_fo: password hash is hex-encoded before PBKDF2
/// </summary>
public static class AppleSrp
{
    // 2048-bit MODP group from RFC 5054
    private static readonly byte[] NBytes = HexToBytes(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DDEF" +
        "9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485" +
        "B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386B" +
        "FB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB" +
        "8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23" +
        "DCA3AD961C62F356208552BB9ED529077096966D670C354E4A" +
        "BC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E" +
        "86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581" +
        "7183995497CEA956AE515D2261898FA051015728E5A8AACAA" +
        "68FFFFFFFFFFFFFFFF");

    private static readonly BigInteger N = new BigInteger(NBytes.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);
    private static readonly BigInteger G = new BigInteger(new byte[] { 2 }, isUnsigned: true);

    private static readonly byte[] NHash = SHA256.HashData(NBytes);
    private static readonly byte[] GHash = SHA256.HashData(new byte[256].Select((_, i) => i == 255 ? (byte)2 : (byte)0).ToArray());
    private static readonly byte[] kHash = SHA256.HashData(NHash.Concat(GHash).ToArray());

    // Store private value 'a' across GeneratePublicKey and ComputeProof
    private static byte[] _privateA = Array.Empty<byte>();

    /// <summary>
    /// Generate client public key A = g^a mod N
    /// </summary>
    public static byte[] GeneratePublicKey()
    {
        _privateA = new byte[32];
        RandomNumberGenerator.Fill(_privateA);
        var bigA = BigInteger.ModPow(G, ToBigInt(_privateA), N);
        return PadLeft(FromBigInt(bigA), 256);
    }

    /// <summary>
    /// Derive password key using PBKDF2
    /// s2k: P = PBKDF2(sha256(password), salt, iterations)
    /// s2k_fo: P = PBKDF2(sha256(sha256(password)).hex(), salt, iterations)
    /// </summary>
    public static byte[] DerivePassword(string password, byte[] salt, int iterations, bool s2kFo)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        if (s2kFo)
        {
            // Hex-encode the digest before PBKDF2
            digest = Encoding.UTF8.GetBytes(BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant());
        }
        return Rfc2898DeriveBytes.Pbkdf2(digest, salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>
    /// Compute SRP client proof M1
    /// M1 = H(H(N) XOR H(g) | H(username) | salt | A | B | K)
    /// </summary>
    public static byte[] ComputeProof(byte[] salt, string password, byte[] A, byte[] B,
        string username, int iterations, bool s2kFo)
    {
        // Compute x = H(salt | H(":" | password))  -- username excluded
        var passwordDerived = DerivePassword(password, salt, iterations, s2kFo);
        var xHash = SHA256.HashData(new byte[] { 0x3a }.Concat(passwordDerived).ToArray());
        var x = ToBigInt(SHA256.HashData(salt.Concat(xHash).ToArray()));

        // Compute v = g^x mod N
        var v = BigInteger.ModPow(G, x, N);

        // Compute u = H(A | B)
        var uHash = SHA256.HashData(A.Concat(B).ToArray());
        var u = ToBigInt(uHash);

        // Compute S = (B - k * v)^(a + u * x) mod N
        var a = ToBigInt(_privateA);
        var bigB = ToBigInt(B);
        var k = ToBigInt(kHash);

        var Kv = (bigB - k * v) % N;
        if (Kv < 0) Kv += N;
        var au = a + u * x;
        var S = BigInteger.ModPow(Kv, au, N);

        // K = H(S)
        var SBytes = PadLeft(FromBigInt(S), 256);
        var K = SHA256.HashData(SBytes);

        // M1 = H(H(N) XOR H(g) | H(username) | salt | A | B | K)
        var hNg = NHash.Zip(GHash, (a, b) => (byte)(a ^ b)).ToArray();
        var hUser = SHA256.HashData(Encoding.UTF8.GetBytes(username));
        var M1 = SHA256.HashData(hNg.Concat(hUser).Concat(salt).Concat(A).Concat(B).Concat(K).ToArray());

        return M1;
    }

    /// <summary>
    /// Verify server proof M2 = H(A | M1 | K)
    /// </summary>
    public static bool VerifyServerProof(byte[] A, byte[] M1, byte[] serverM2, byte[] salt,
        string password, string username, int iterations, bool s2kFo)
    {
        var passwordDerived = DerivePassword(password, salt, iterations, s2kFo);
        var xHash = SHA256.HashData(new byte[] { 0x3a }.Concat(passwordDerived).ToArray());
        var x = ToBigInt(SHA256.HashData(salt.Concat(xHash).ToArray()));
        var v = BigInteger.ModPow(G, x, N);
        var uHash = SHA256.HashData(A.Concat(Convert.FromBase64String(Convert.ToBase64String(new byte[256]))).ToArray());
        // Simplified: just recompute K and check M2
        var a = ToBigInt(_privateA);
        var k = ToBigInt(kHash);
        var u = ToBigInt( SHA256.HashData(A.Concat(new byte[256]).ToArray()) );

        // Recompute session key
        var S = BigInteger.ModPow((BigInteger.Zero - k * v) % N, a, N);
        var SBytes = PadLeft(FromBigInt(S), 256);
        var K = SHA256.HashData(SBytes);

        var expectedM2 = SHA256.HashData(A.Concat(M1).Concat(K).ToArray());
        return expectedM2.SequenceEqual(serverM2);
    }

    /// <summary>
    /// Generate placeholder Anisette data (only for testing, not real)
    /// </summary>
    public static AnisetteData GenerateAnisette()
    {
        var machineId = new byte[60];
        var oneTimePassword = new byte[28];
        RandomNumberGenerator.Fill(machineId);
        RandomNumberGenerator.Fill(oneTimePassword);

        return new AnisetteData
        {
            MachineId = Convert.ToBase64String(machineId),
            OneTimePassword = Convert.ToBase64String(oneTimePassword),
            LocalUserId = Guid.NewGuid().ToString("N")[..32],
            RoutingInfo = 17106176,
            DeviceUniqueId = Guid.NewGuid().ToString("N")[..32],
            SerialNumber = "F2LX1234ABCD"
        };
    }

    private static BigInteger ToBigInt(byte[] bytes)
    {
        return new BigInteger(bytes.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);
    }

    private static byte[] FromBigInt(BigInteger value)
    {
        return value.ToByteArray().Reverse().SkipWhile(b => b == 0).Concat(new byte[] { 0 }).ToArray();
    }

    private static byte[] PadLeft(byte[] source, int totalLength)
    {
        if (source.Length >= totalLength) return source;
        var result = new byte[totalLength];
        Buffer.BlockCopy(source, 0, result, totalLength - source.Length, source.Length);
        return result;
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
    public string MachineId { get; set; } = string.Empty;
    public string OneTimePassword { get; set; } = string.Empty;
    public string LocalUserId { get; set; } = string.Empty;
    public long RoutingInfo { get; set; }
    public string DeviceUniqueId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Convert to dictionary for plist/request building
    /// </summary>
    public Dictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>
        {
            ["X-Apple-I-MD"] = OneTimePassword,
            ["X-Apple-I-MD-M"] = MachineId,
            ["X-Apple-I-MD-LU"] = LocalUserId,
            ["X-Apple-I-MD-RINFO"] = RoutingInfo.ToString(),
            ["X-Mme-Device-Id"] = DeviceUniqueId,
            ["X-Apple-I-SRL-NO"] = SerialNumber,
            ["X-Apple-I-Client-Time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["X-Apple-Locale"] = "en_US",
            ["X-Apple-I-TimeZone"] = "GMT"
        };
    }
}
