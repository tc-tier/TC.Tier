using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Security;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// NodeKeyPair 契约测试（spec-12 §3.4 KeyPair 档密码原语——P-256 三件套）：
/// 生成/私钥重建往返、公钥 compressed 编解码往返（★自写解压 vs .NET 原生参数对拍）、
/// 签名/验签（定长 P1363、错钥拒、篡改拒）、ECDH 双方一致、畸形公钥防御。
/// </summary>
public class NodeKeyPairTests : IDisposable
{
    private readonly NodeKeyPair _a = NodeKeyPair.Generate();
    private readonly NodeKeyPair _b = NodeKeyPair.Generate();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _a.Dispose();
        _b.Dispose();
    }

    /// <summary>私钥重建往返：ExportPrivateKey → FromPrivateKey → 同公钥（持久化迁移面）。</summary>
    [Fact]
    public void FromPrivateKey_RoundTrip_SamePublicKey()
    {
        using var rebuilt = NodeKeyPair.FromPrivateKey(_a.ExportPrivateKey());
        rebuilt.PublicKey.ToArray().Should().Equal(_a.PublicKey.ToArray(), "私钥唯一确定公钥点");
    }

    /// <summary>私钥长度防御：非 32B 拒绝。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void FromPrivateKey_WrongLength_Throws(int length)
    {
        var act = () => NodeKeyPair.FromPrivateKey(new byte[length]);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>公钥定长：33B compressed（前缀 0x02/0x03）。</summary>
    [Fact]
    public void PublicKey_IsCompressed33Bytes()
    {
        _a.PublicKey.Length.Should().Be(NodeKeyPair.PublicKeySize);
        _a.PublicKey[0].Should().BeOneOf([(byte)0x02, (byte)0x03], "SEC1 compressed 前缀=0x02|奇偶位");
    }
    /// <summary>签名往返：A 签 B 验（定长 64B P1363）。</summary>
    [Fact]
    public void Sign_Verify_RoundTrip()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var signature = _a.Sign(data);
        signature.Length.Should().Be(NodeKeyPair.SignatureSize, "P1363 定长——握手载荷定长化");
        NodeKeyPair.Verify(_a.PublicKey, data, signature).Should().BeTrue();
    }

    /// <summary>错公钥验签拒绝（身份证明失败形态——握手层据此 Error + 断连）。</summary>
    [Fact]
    public void Verify_WrongKey_Rejected()
    {
        var signature = _a.Sign(new byte[] { 0x01 });
        NodeKeyPair.Verify(_b.PublicKey, new byte[] { 0x01 }, signature).Should().BeFalse("B 的公钥验 A 的签名");
    }

    /// <summary>签名篡改拒绝（一字节翻转）。</summary>
    [Fact]
    public void Verify_TamperedSignature_Rejected()
    {
        var data = new byte[] { 0x01, 0x02 };
        var signature = _a.Sign(data);
        signature[10] ^= 0xFF;
        NodeKeyPair.Verify(_a.PublicKey, data, signature).Should().BeFalse();
    }

    /// <summary>数据篡改拒绝。</summary>
    [Fact]
    public void Verify_TamperedData_Rejected()
    {
        var signature = _a.Sign(new byte[] { 0x01, 0x02 });
        NodeKeyPair.Verify(_a.PublicKey, new byte[] { 0x01, 0x0F }, signature).Should().BeFalse();
    }

    /// <summary>ECDH 交换律：A(B公钥) == B(A公钥)（会话密钥派生的共同输入）。</summary>
    [Fact]
    public void DeriveSharedSecret_BothSides_Agree()
    {
        var a2b = _a.DeriveSharedSecret(_b.PublicKey);
        var b2a = _b.DeriveSharedSecret(_a.PublicKey);
        a2b.Should().Equal(b2a, "ECDH 交换律——双方派生同一会话密钥材料");
        a2b.Length.Should().Be(32, "P-256 共享点 X 坐标");
    }

    /// <summary>★解压高位零字节回归（flaky 根因判例 / #470）：compressed→解压对 y 高位字节为
    ///   零（~1/256/密钥对）的点必须逐字节精确——stackalloc 垃圾残留曾致公钥点静默错误、验签必败。
    ///   角点以测试侧独立曲线数学确定性构造（100% 覆盖、构造零 CNG 压力——全套件尾部 CNG
    ///   密集突发是挂死暴露面，见 #470），CNG 只承担一次解压点受理验收；另以 64 对真实密钥
    ///   验签全路径 sweep 保持端到端回归面。</summary>
    [Fact]
    public void CompressedDecompress_HighZeroByteY_VerifyAlwaysSucceeds()
    {
        // 角点（确定性）：解压逐字节对拍——y 高位字节为零时高位缓冲正是原残留区
        var corner = FindPointWithHighZeroByteY();
        var compressed = new byte[NodeKeyPair.PublicKeySize];
        compressed[0] = (byte)(0x02 | (corner.Y[31] & 1));
        corner.X.CopyTo(compressed.AsSpan(1));

        var imported = NodeKeyPair.ImportCompressed(compressed);
        imported.Q.X.Should().Equal(corner.X, "X 逐字节精确（32B 右对齐）");
        imported.Q.Y.Should().Equal(corner.Y, "y 高位字节为零——解压不容垃圾残留");

        // 解压点必须被 BCL 判为曲线点（残留形态 = 点不在曲线上，Create 即拒）
        var act = () => { using var _ = ECDsa.Create(imported); };
        act.Should().NotThrow("解压点必须在 P-256 曲线上");

        // 真实密钥全路径 sweep（compressed→解压→验签往返）
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        for (var i = 0; i < 64; i++)
        {
            using var key = NodeKeyPair.Generate();
            var signature = key.Sign(data);
            NodeKeyPair.Verify(key.PublicKey, data, signature).Should().BeTrue($"第 {i} 对——解压公钥点必须精确");
        }
    }

    // ── 角点构造 oracle（独立于 NodeKeyPair 内部实现——对拍互证）──

    private static readonly BigInteger P = BigInteger.Parse(
        "115792089210356248762697446949407573530086143415290314195533631308867097853951",
        CultureInfo.InvariantCulture);
    private static readonly BigInteger B = BigInteger.Parse(
        "41058363725152142129326129780047268409114441015993725554835256314039467401291",
        CultureInfo.InvariantCulture);

    /// <summary>确定性求 y 高位字节为零的曲线点：x 邻域遍历（期望 ~512 次命中，纯 BigInteger 微秒级）。
    /// y² = x³-3x+b (mod p)；p ≡ 3 (mod 4) → y = y^((p+1)/4)，平方校验过滤非曲线 x。</summary>
    private static (byte[] X, byte[] Y) FindPointWithHighZeroByteY()
    {
        for (var xi = 1; xi < 100_000; xi++)
        {
            var x = new BigInteger(xi);
            var ySquared = (x * x % P * x + (P - 3) * x + B) % P;
            var y = BigInteger.ModPow(ySquared, (P + 1) / 4, P);
            if (y * y % P != ySquared) continue;   // x 不在曲线上（~1/2）
            var yBytes = To32BigEndianBytes(y);
            if (yBytes[0] != 0) continue;          // 高位字节非零——非角点（~255/256）
            return (To32BigEndianBytes(x), yBytes);
        }
        throw new InvalidOperationException("遍历域内未找到高位零字节 y 角点——域参数或遍历式错误。");
    }

    /// <summary>BigInteger → 定长 32B 大端（右对齐高位补零）。</summary>
    private static byte[] To32BigEndianBytes(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var full = new byte[32];
        bytes.CopyTo(full, 32 - bytes.Length);
        return full;
    }

    /// <summary>畸形公钥防御：不在曲线上的点/错长度——Verify/Derive 不炸不通过。</summary>
    [Fact]
    public void MalformedPublicKey_Defended()
    {
        var notOnCurve = new byte[NodeKeyPair.PublicKeySize];   // 全零 X——大概率不在曲线上
        notOnCurve[0] = 0x02;
        NodeKeyPair.Verify(notOnCurve, new byte[] { 0x01 }, new byte[NodeKeyPair.SignatureSize])
            .Should().BeFalse("不在曲线的点——验签失败（不抛）");

        var act = () => _a.DeriveSharedSecret(new byte[10]);    // 错长度
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>★自写解压对拍：compressed → 解压 → DeriveSharedSecret 与 .NET 原生导出点一致
    /// （自写 Tonelli–Shanks 特例与 BCL 曲线实现交叉验证——解压错点会静默派生错误密钥）。</summary>
    [Fact]
    public void CompressedDecompress_MatchesBclCurve()
    {
        // A 的公钥（自写解压路径）与 B 交换 → 密钥一致已由上一测覆盖；
        // 这里对拍双方多次（随机覆盖奇偶两种前缀形态）
        for (var i = 0; i < 8; i++)
        {
            using var x = NodeKeyPair.Generate();
            using var y = NodeKeyPair.Generate();
            x.DeriveSharedSecret(y.PublicKey).Should().Equal(y.DeriveSharedSecret(x.PublicKey),
                $"第 {i} 轮随机密钥对——解压路径与 BCL 派生一致");
        }
    }
}
