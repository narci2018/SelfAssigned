using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple SRP-6a 实现
/// Apple 对标准 SRP 的修改:
/// - k = sha256(N+g)
/// - x = H(":" + P) (不包含用户名)
/// - 密码推导: P = PBKDF2(sha256(password), salt, iterations)
/// </summary>
public static class AppleSrp
{
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

    /// <summary>
    /// 生成 SRP 客户端公钥 A
    /// </summary>
    public static byte[] GeneratePublicKey()
    {
        var a = new byte[32];
        RandomNumberGenerator.Fill(a);
        var bigA = BigInteger.ModPow(G, new BigInteger(a.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true), N);
        return PadLeft(bigA.ToByteArray().Reverse().SkipWhile(b => b == 0).Concat(new byte[] { 0 }).ToArray(), 256);
    }

    /// <summary>
    /// 计算密码验证器
    /// </summary>
    public static byte[] ComputeVerifier(byte[] salt, string password, int iterations = 10000)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var passwordHash = SHA256.HashData(passwordBytes);

        var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
            passwordHash, salt, iterations, HashAlgorithmName.SHA256, 32);

        // x = H(":" + derivedKey)
        var xBytes = SHA256.HashData(new byte[] { 0x3a }.Concat(derivedKey).ToArray());
        var x = new BigInteger(xBytes.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);

        // v = g^x mod N
        var v = BigInteger.ModPow(G, x, N);
        return PadLeft(v.ToByteArray().Reverse().SkipWhile(b => b == 0).Concat(new byte[] { 0 }).ToArray(), 256);
    }

    /// <summary>
    /// 计算客户端证明 M1
    /// </summary>
    public static byte[] ComputeProof(byte[] salt, string password, byte[] A, byte[] B,
        byte[] serverProof, string username, int iterations = 10000)
    {
        // H(N) XOR H(g)
        var hN = SHA256.HashData(NBytes);
        var gBytes = new byte[256];
        gBytes[255] = 2;
        var hg = SHA256.HashData(gBytes);
        var hNg = hN.Zip(hg, (a, b) => (byte)(a ^ b)).ToArray();

        var hUser = SHA256.HashData(Encoding.UTF8.GetBytes(username));
        var hA = SHA256.HashData(A);
        var hB = SHA256.HashData(B);

        // M1 = H(hNg + hUser + salt + hA + hB + serverProof)
        var proof = SHA256.HashData(hNg.Concat(hUser).Concat(salt).Concat(hA).Concat(hB).Concat(serverProof).ToArray());
        return proof;
    }

    /// <summary>
    /// 生成占位符 Anisette 数据
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
            RoutingInfo = 1547392118L,
            DeviceUniqueId = Guid.NewGuid().ToString("N")[..32],
            SerialNumber = "F2LX1234ABCD"
        };
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
}
