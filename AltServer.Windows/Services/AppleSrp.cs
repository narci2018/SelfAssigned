using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AltServer.Windows.Services;

/// <summary>
/// Apple SRP-6a 实现 (基于 AltStore/gsa.py 源码)
/// Apple 对标准 SRP 的修改:
/// - k = sha256(N+g) (N, g 为 256 字节大端整数)
/// - x = H(":" + P) (不包含用户名)
/// - 密码推导: P = PBKDF2(sha256(password), salt, iterations)
/// </summary>
public static class AppleSrp
{
    // 2048-bit SRP prime (来自 Apple corecrypto)
    private static readonly byte[] N = HexToBytes(
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

    private static readonly BigInteger g = BigInteger.Parse("2");
    private static readonly int k = 3; // hash digest length in 32-byte blocks for 2048-bit

    /// <summary>
    /// 生成 SRP 客户端公钥 A
    /// </summary>
    public static byte[] GeneratePublicKey()
    {
        var a = GenerateRandomBytes(32);
        var bigN = new BigInteger(N.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);
        var A = BigInteger.ModPow(g, a, bigN);
        return A.ToByteArray().Reverse().Take(256).ToArray().PadLeft(256, 0);
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

        var bigN = new BigInteger(N.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);

        // x = H(":" + derivedKey)
        var xBytes = SHA256.HashData([0x3a, .. derivedKey]);
        var x = new BigInteger(xBytes.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);

        // v = g^x mod N
        var v = BigInteger.ModPow(g, x, bigN);
        return v.ToByteArray().Reverse().Take(256).ToArray().PadLeft(256, 0);
    }

    /// <summary>
    /// 计算客户端证明 M1
    /// </summary>
    public static byte[] ComputeProof(byte[] salt, string password, byte[] A, byte[] B,
        byte[] serverProof, string username, int iterations = 10000)
    {
        var bigN = new BigInteger(N.Reverse().Concat(new byte[] { 0 }).ToArray(), isUnsigned: true);

        // H(N) XOR H(g)
        var hN = SHA256.HashData(N);
        var hg = SHA256.HashData(g.ToByteArray().Reverse().Take(256).ToArray().PadLeft(256, 0));
        var hNg = hN.Zip(hg, (a, b) => (byte)(a ^ b)).ToArray();

        var hUser = SHA256.HashData(Encoding.UTF8.GetBytes(username));

        // H(A) + H(B)
        var hA = SHA256.HashData(A);
        var hB = SHA256.HashData(B);

        // M1 = H(hNg + hUser + salt + hA + hB + serverProof)
        var proof = SHA256.HashData([.. hNg, .. hUser, .. salt, .. hA, .. hB, .. serverProof]);
        return proof;
    }

    /// <summary>
    /// 生成 Anisette 数据（占位符，需要从设备获取真实值）
    /// </summary>
    public static AnisetteData GenerateAnisette()
    {
        var machineId = new byte[60];
        var oneTimePassword = new byte[28];
        var localUserId = Guid.NewGuid().ToString("N")[..40];
        var routingInfo = 1547392118L;
        var deviceUniqueId = Guid.NewGuid().ToString("N")[..40];
        var serialNumber = "F2LX1234ABCD";

        RandomNumberGenerator.Fill(machineId);
        RandomNumberGenerator.Fill(oneTimePassword);

        return new AnisetteData
        {
            MachineId = Convert.ToBase64String(machineId),
            OneTimePassword = Convert.ToBase64String(oneTimePassword),
            LocalUserId = localUserId,
            RoutingInfo = routingInfo,
            DeviceUniqueId = deviceUniqueId,
            SerialNumber = serialNumber
        };
    }

    private static byte[] GenerateRandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static byte[] HexToBytes(string hex)
    {
        return Enumerable.Range(0, hex.Length / 2)
            .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
            .ToArray();
    }
}

/// <summary>
/// Anisette 数据结构
/// </summary>
public class AnisetteData
{
    public string MachineId { get; set; } = string.Empty;
    public string OneTimePassword { get; set; } = string.Empty;
    public string LocalUserId { get; set; } = string.Empty;
    public long RoutingInfo { get; set; }
    public string DeviceUniqueId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
}
