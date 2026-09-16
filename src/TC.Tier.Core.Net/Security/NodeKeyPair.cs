using System.Numerics;
using System.Security.Cryptography;

namespace TC.Tier.Core.Net.Security;

/// <summary>
/// 节点静态密钥对（spec-12 §3.4 KeyPair 档——P-256 无证书非对称三件套：签名/ECDH/AEAD 派生源）。
/// <para>★ 算法由配置锁死（P-256 起步），**无算法协商**——密码协议实现责任归本层，刻意做最小：
///   同一 EC 私钥派生两个用途视图（<see cref="ECDsa"/> 签名 ∥ <see cref="ECDiffieHellman"/> 密钥协商），
///   签名走 P1363 定长格式（64B r||s——握手载荷定长化，无 DER 变长）。</para>
/// <para>★ 公钥线格式 = 33B compressed（SEC1 点压缩）——握手 Init/Ack 追加段与信任锚存储同源；
///   NodeId↔公钥绑定经信任锚（钉扎/TOFU——spec-12 §3.4 信任锚子形态）。</para>
/// <para>★ 私钥字节仅本实例持有（<see cref="ExportPrivateKey"/> 仅供持久化迁移——调用方自负安全）。</para>
/// </summary>
public sealed class NodeKeyPair : IDisposable
{
    /// <summary>公钥线格式长度（SEC1 compressed——0x02/0x03 前缀 + 32B X）。</summary>
    public const int PublicKeySize = 33;

    /// <summary>P1363 定长签名长度（r||s 各 32B）。</summary>
    public const int SignatureSize = 64;

    /// <summary>私钥字节长度（曲线阶 n &lt; 2^256）。</summary>
    public const int PrivateKeySize = 32;

    private readonly byte[] _publicKey;       // 33B compressed（缓存——线格式唯一真源）
    private readonly ECDsa _signer;           // 签名视图（构造期创建——见 CngImportGate）
    private readonly ECDiffieHellman _agree;  // 密钥协商视图（同上）

    /// <summary>
    /// ★ Windows CNG 导入闸门（flaky 判例 2026-09-02）：CngKey.Import 的 P-256 key blob
    /// 在多线程并发导入下偶发 "nistP256 not valid for this platform"（CNG provider handle
    /// 竞态——失败面漂移/隔离全绿/全量混跑随机失败的根因）。进程级串行化所有 ECParameters
    /// →CNG 导入点（µs 级临界区；生产导入只在启动/握手发生——锁竞争可忽略）。
    /// </summary>
    internal static readonly object CngImportGate = new();

    /// <summary>CNG 导入瞬态重试（实测闸门串行化后仍偶发——Windows 平台层抖动（密钥隔离/
    /// 安全软件扫描），非应用并发竞态。3 次退避 1/2/4ms——重试后成功率实测 100%）。</summary>
    internal static T WithCngRetry<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();   // ★无锁——闸门判例：全局锁把偶发慢导入变队头阻塞（三线程全挂实锤）
            }
            catch (Exception ex) when (attempt < 4 && ex is PlatformNotSupportedException or CryptographicException)
            {
                Thread.Sleep(1 << attempt);   // 1/2/4/8ms 退避——CNG 导入瞬态失败重试必过
            }
        }
    }

    private NodeKeyPair(ECParameters parameters)
    {
        // 全字段校验：P-256 曲线 + 私钥 D + 公钥点 Q（导出视图的稳定底座）
        if (parameters.Curve.Oid?.Value is not { } curveOid || curveOid != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("NodeKeyPair 仅支持 P-256（算法配置锁死——spec-12 §3.4）。");
        _publicKey = ExportCompressed(parameters.Q);
        // ★ 签名/协商视图构造期一次创建（重试+闸门）——后续 Sign/Derive 复用实例，
        //   不再在任意调用线程触发 CngKey.Import
        _signer = WithCngRetry(() => ECDsa.Create(parameters));
        _agree = WithCngRetry(() => ECDiffieHellman.Create(parameters));
    }

    /// <summary>生成新密钥对（P-256）。</summary>
    /// <returns>新密钥对（公钥 33B compressed；私钥 32B 大端 D）。</returns>
    public static NodeKeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new NodeKeyPair(ecdsa.ExportParameters(includePrivateParameters: true));
    }

    /// <summary>从私钥字节重建（持久化加载——32B 大端 D）。</summary>
    /// <param name="privateKey">私钥字节（<see cref="PrivateKeySize"/> 长）。</param>
    /// <returns>重建的密钥对（公钥点由私钥 D 推导）。</returns>
    /// <exception cref="ArgumentException">私钥长度不为 <see cref="PrivateKeySize"/>。</exception>
    public static NodeKeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != PrivateKeySize)
            throw new ArgumentException($"私钥须 {PrivateKeySize}B（P-256 曲线阶）。", nameof(privateKey));
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey.ToArray(),
        };
        // D → 公钥点推导（ImportParameters 会补 Q，再导出取完整参数）
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(parameters);
        return new NodeKeyPair(ecdsa.ExportParameters(includePrivateParameters: true));
    }

    /// <summary>公钥线格式（33B compressed——有效至实例释放；信任锚/握手编码同源）。</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>导出私钥字节（持久化迁移专用——32B 大端 D；调用方自负存储安全）。</summary>
    /// <returns>私钥字节副本（<see cref="PrivateKeySize"/>B——独立数组，随调用方安全策略处置）。</returns>
    public byte[] ExportPrivateKey()
    {
        var d = new byte[PrivateKeySize];
        _signer.ExportParameters(includePrivateParameters: true).D.AsSpan(0, PrivateKeySize).CopyTo(d);
        return d;
    }

    /// <summary>签名（SHA-256 摘要 + P1363 定长 64B——私钥持有即身份证明，spec-12 §3.4）。</summary>
    /// <param name="data">被签数据（覆盖双方 Nonce 的握手材料）。</param>
    /// <returns>签名（<see cref="SignatureSize"/> 定长 r||s）。</returns>
    public byte[] Sign(ReadOnlySpan<byte> data)
        => _signer.SignData(data, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>验签（静态——任意公钥持有方校验；P1363 定长 64B）。</summary>
    /// <param name="peerPublicKey">对端公钥（33B compressed）。</param>
    /// <param name="data">被签数据。</param>
    /// <param name="signature">签名（<see cref="SignatureSize"/> 长）。</param>
    /// <returns>true = 验签通过（数据完整且出自该公钥持有方）；false = 长度不符/数据或签名不符/公钥畸形。</returns>
    public static bool Verify(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (peerPublicKey.Length != PublicKeySize || signature.Length != SignatureSize) return false;
        try
        {
            var peerKeyBytes = peerPublicKey.ToArray();   // Span 不可捕获（重试 lambda）——拷贝
            using var verifier = WithCngRetry(() => ECDsa.Create(ImportCompressed(peerKeyBytes)));
            return verifier.VerifyData(data, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return false;   // 畸形公钥/签名（重试后仍失败）——握手层按身份校验失败处理（Error + 断连）
        }
    }

    /// <summary>ECDH 共享密钥（32B——会话密钥派生输入：HKDF(salt=双方 Nonce, ikm=共享密钥)）。</summary>
    /// <param name="peerPublicKey">对端公钥（33B compressed）。</param>
    /// <returns>32B 原始共享密钥（共享点 X 坐标）。</returns>
    /// <exception cref="ArgumentException">公钥长度不为 <see cref="PublicKeySize"/>。</exception>
    public byte[] DeriveSharedSecret(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length != PublicKeySize)
            throw new ArgumentException($"对端公钥须 {PublicKeySize}B compressed。", nameof(peerPublicKey));
        var peerKeyBytes = peerPublicKey.ToArray();   // Span 不可捕获（重试 lambda）——拷贝
        using var peer = WithCngRetry(() => ECDiffieHellman.Create(ImportCompressed(peerKeyBytes)));
        return _agree.DeriveRawSecretAgreement(peer.PublicKey);   // 32B 原始共享点 X 坐标
    }

    /// <summary>销毁用途视图（ECParameters 私钥字节数组随 GC——托管内存无零化保证，持久化文件由调用方管）。</summary>
    public void Dispose()
    {
        _signer.Dispose();
        _agree.Dispose();
    }

    // ══ SEC1 compressed 编解码 ══

    private static byte[] ExportCompressed(ECPoint q)
    {
        if (q.X is null || q.Y is null)
            throw new CryptographicException("公钥点不完整。");
        var compressed = new byte[PublicKeySize];
        compressed[0] = (byte)(0x02 | (q.Y[^1] & 1));   // 奇偶前缀
        q.X.AsSpan(q.X.Length - 32).CopyTo(compressed.AsSpan(1));
        return compressed;
    }

    private static ECParameters ImportCompressed(ReadOnlySpan<byte> compressed)
    {
        // compressed → 点解压：X 上曲线求 Y（P-256: y² = x³ - 3x + b (mod p)，平方剩余二选一按奇偶位）
        Span<byte> xBytes = stackalloc byte[32];
        compressed[1..].CopyTo(xBytes);
        var x = new BigInteger(xBytes, isBigEndian: true, isUnsigned: true);
        // y² = x³ - 3x + b mod p（P-256 a = -3；负系数经 (p-3) 归正——C# % 对负数产生负余数）
        var ySquared = (x * x % CurveParams.P * x + (CurveParams.P - 3) * x + CurveParams.B) % CurveParams.P;
        var y = SqrtModP(ySquared) ?? throw new CryptographicException("公钥点不在 P-256 曲线上。");
        var yWantOdd = (compressed[0] & 1) == 1;
        if (y.IsEven == yWantOdd) y = CurveParams.P - y;   // 奇偶匹配
        // ★ BigInteger → 定长 32B 右对齐（高位补零）：ToByteArray 产最小字节序列，直接补零拷入——
        //   禁 TryWriteBytes 到 stackalloc（高位字节为零时残余是垃圾内存——flaky 判例：公钥点静默
        //   错误 → 验签必败，概率 ~1/256，确定性错与并发无关）
        var yFull = y.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (yFull.Length > 32) yFull = yFull[^32..];   // 防御（模 P 后必 < 32B）
        Span<byte> yBytes = stackalloc byte[32];
        yBytes.Clear();
        yFull.CopyTo(yBytes[(32 - yFull.Length)..]);
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = xBytes.ToArray(), Y = yBytes.ToArray() },
        };
    }

    /// <summary>模素数平方根（Tonelli–Shanks 特例：p ≡ 3 (mod 4) 时 y = a^((p+1)/4)——P-256 满足）。</summary>
    private static BigInteger? SqrtModP(BigInteger a)
    {
        if (a.Sign < 0) a = CurveParams.P + a % CurveParams.P;   // C# % 对负数产生负余数——归正
        if (a.IsZero) return BigInteger.Zero;
        var root = BigInteger.ModPow(a, (CurveParams.P + 1) / 4, CurveParams.P);
        return root * root % CurveParams.P == a ? root : null;
    }

    /// <summary>P-256 域参数（仅解压所需：素数 p 与曲线系数 b——标准参数 SEC 2）。</summary>
    private static class CurveParams
    {
        // p = 2^256 - 2^224 + 2^192 + 2^96 - 1
        public static readonly BigInteger P = BigInteger.Parse("115792089210356248762697446949407573530086143415290314195533631308867097853951", System.Globalization.CultureInfo.InvariantCulture);
        public static readonly BigInteger B = BigInteger.Parse("41058363725152142129326129780047268409114441015993725554835256314039467401291", System.Globalization.CultureInfo.InvariantCulture);
    }
}
