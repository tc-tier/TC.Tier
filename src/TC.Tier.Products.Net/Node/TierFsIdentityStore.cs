using System.Buffers.Binary;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net.Ports;

namespace TC.Tier.Products.Net.Node;

/// <summary>
/// <see cref="IIdentitySource"/> 的 TierFs 身份文件适配器（spec-12 §8.3 / D6-T3——Core.Net 零文件 IO
/// 的组装层落地，两个核心在组装层汇合）：身份文件落使用方指定卷/路径，四介质任意
/// （测试 memory 卷 / 生产 local / 云 network）。
/// <para>★ 文件布局（本适配器私有，25B 定长）：<c>[Magic "TCID" 4B][Ver 1B][NodeId 16B][CRC32 4B LE]</c>——
/// CRC 覆盖 Ver+NodeId；Ver 留 W-Security 密钥材料升级位。</para>
/// <para>★ 契约：首次 <see cref="LoadOrCreateAsync"/> 生成（<see cref="NodeId.NewRandom"/>）并原子保存
/// （temp 写全 + Flush → rename 覆盖，半写不可见），此后加载同一身份——NodeId 跨重启稳定
/// （成员表/votedFor 绑定前提）。文件损坏/截断 = 身份断裂风险，fail-fast 抛
/// <see cref="FileIOException"/>——禁静默重生成（重生成等于静默换节点身份）。</para>
/// <para>★ 一个路径 = 一个节点进程（进程内并发经 <see cref="_gate"/> 串行；跨进程同路径并发创建 =
/// 部署误用，不做跨进程互斥）。权限收紧（§8.3——凭据不是普通数据）：IFileSystem 无权限语义面，
/// 随 W-Security（密钥材料入文件）一并收口。</para>
/// <param name="fs">组合根文件系统（测试 memory 卷 / 生产 local / 云 network 四介质任意）。</param>
/// <param name="path">身份文件路径（一个路径 = 一个节点进程）。</param>
/// </summary>
public sealed class TierFsIdentityStore(IFileSystem fs, string path) : IIdentitySource, IDisposable
{
    private static readonly byte[] Magic = [(byte)'T', (byte)'C', (byte)'I', (byte)'D'];
    private const byte FormatVersionV1 = 1;    // 旧版：25B [Magic][Ver][NodeId][CRC]——加载兼容
    private const byte FormatVersionV2 = 2;    // ★ 二期-H5：密钥材料持久化（[..][KeyLen 4B][材料][CRC]）
    private const int V1FileBytes = 25;        // v1 [Magic 4B][Ver 1B][NodeId 16B][CRC32 4B]
    private const int V1CrcOffset = 21;        // v1 CRC 覆盖 [Ver..NodeId]（17B）
    private const int V1CrcSpanBytes = 17;
    private const int KeyLenBytes = 4;         // v2 KeyLen 字段宽
    private const int MaxKeyMaterialBytes = 4096;  // 材料防御上限

    private readonly IFileSystem _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NodeIdentity? _cached;

    /// <inheritdoc/>
    /// <param name="ct">取消令牌。</param>
    /// <returns>节点身份（跨调用/跨重启稳定——首次调用生成随机 NodeId 并以 v2 布局原子落盘）。</returns>
    public async ValueTask<NodeIdentity> LoadOrCreateAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached) return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } raced) return raced;
            var identity = _fs.Exists(_path) ? await LoadAsync(ct).ConfigureAwait(false) : await CreateAsync(ct).ConfigureAwait(false);
            _cached = identity;
            return identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>加载既有身份——魔数/版本/CRC 三重校验，任一不过即 fail-fast（见类型契约）。
    /// v1（25B 旧版）加载兼容——材料空；v2（二期-H5）含 [KeyLen][材料] 段，CRC 全覆盖。</summary>
    private async ValueTask<NodeIdentity> LoadAsync(CancellationToken ct)
    {
        using var handle = _fs.Open(_path, new FileOpenOptions { Access = AccessMode.Read });
        var length = handle.Length;
        if (length is not (V1FileBytes or >= 29))
            throw new FileIOException(IOError.PreconditionFailed,
                $"身份文件长度 {length} 非法（v1=25 或 v2 ≥ 29）——截断/损坏，禁止静默重生成", _path, "IdentityLoad");
        var bytes = new byte[length];
        var read = await handle.ReadAsync(0, bytes, ct).ConfigureAwait(false);
        if (read != length)
            throw new FileIOException(IOError.IOFailure,
                $"身份文件读取 {read}/{length} 字节——读取不完整", _path, "IdentityLoad");
        if (!bytes.AsSpan(0, 4).SequenceEqual(Magic))
            throw new FileIOException(IOError.PreconditionFailed,
                "身份文件魔数不匹配——非本适配器的文件", _path, "IdentityLoad");

        var version = bytes[4];
        if (version == FormatVersionV1)
        {
            if (length != V1FileBytes)
                throw new FileIOException(IOError.PreconditionFailed,
                    $"v1 身份文件长度 {length} ≠ {V1FileBytes}——截断/损坏", _path, "IdentityLoad");
            var expectedV1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(V1CrcOffset));
            if (UnifiedCrc.ComputeCrc32(bytes.AsSpan(4, V1CrcSpanBytes)) != expectedV1)
                throw new FileIOException(IOError.PreconditionFailed,
                    "身份文件 CRC 不匹配——内容损坏，禁止静默重生成", _path, "IdentityLoad");
            return new NodeIdentity { Id = new NodeId(bytes.AsSpan(5, NodeId.Size)) };
        }
        if (version != FormatVersionV2)
            throw new FileIOException(IOError.Unsupported,
                $"身份文件版本 {version} ≠ 当前支持 {FormatVersionV2}", _path, "IdentityLoad");

        // v2：[Ver 1B][NodeId 16B][KeyLen 4B][材料][CRC32 4B]——CRC 覆盖 [Ver..材料尾]
        if (length < 29)
            throw new FileIOException(IOError.PreconditionFailed,
                $"v2 身份文件长度 {length} < 29——截断/损坏", _path, "IdentityLoad");
        var crcOffsetV2 = (int)(length - 4);
        var expectedV2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(crcOffsetV2));
        if (UnifiedCrc.ComputeCrc32(bytes.AsSpan(4, crcOffsetV2 - 4)) != expectedV2)
            throw new FileIOException(IOError.PreconditionFailed,
                "身份文件 CRC 不匹配——内容损坏，禁止静默重生成", _path, "IdentityLoad");
        var keyLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(21, KeyLenBytes));
        if (keyLen < 0 || 21 + KeyLenBytes + keyLen != crcOffsetV2)
            throw new FileIOException(IOError.PreconditionFailed,
                $"v2 密钥材料长度 {keyLen} 与文件布局不符——截断/损坏", _path, "IdentityLoad");
        var material = new byte[keyLen];
        bytes.AsSpan(25, keyLen).CopyTo(material);
        return new NodeIdentity { Id = new NodeId(bytes.AsSpan(5, NodeId.Size)), KeyMaterial = material };
    }

    /// <summary>首次生成：NewRandom → v2 布局（材料空——SaveKeyMaterialAsync 可后续升级）→
    /// temp 写全刷盘 → rename 原子就位（半写不可见）。</summary>
    private async ValueTask<NodeIdentity> CreateAsync(CancellationToken ct)
    {
        var identity = new NodeIdentity { Id = NodeId.NewRandom() };
        var emptyMaterial = Array.Empty<byte>();
        var bytes = BuildV2(identity.Id, emptyMaterial, out var crcOffset);
        Magic.CopyTo(bytes, 0);
        bytes[4] = FormatVersionV2;
        identity.Id.CopyTo(bytes.AsSpan(5));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(21, KeyLenBytes), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset),
            UnifiedCrc.ComputeCrc32(bytes.AsSpan(4, crcOffset - 4)));

        await WriteAtomicAsync(bytes, ct).ConfigureAwait(false);
        return identity;
    }

    /// <summary>原子写（temp 写全刷盘 → rename 覆盖——半写不可见）。</summary>
    private async ValueTask WriteAtomicAsync(byte[] bytes, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        if (_fs.Exists(temp)) _fs.Delete(temp);   // 上次中断的残留（OpenOrCreate 重开不抛，但留残骸碍眼）
        _fs.CreateFile(temp);
        try
        {
            using (var handle = _fs.Open(temp, new FileOpenOptions { Access = AccessMode.Write }))
            {
                await handle.WriteAsync(0, bytes, ct).ConfigureAwait(false);
                handle.Flush();   // 身份先于一切协议流量落盘（mem 卷 no-op）
            }

            _fs.Move(temp, _path, overwrite: true);   // rename 原子——半写不可见
        }
        catch
        {
            try { _fs.Delete(temp); } catch { /* 清理尽力——保留原失败 */ }
            throw;
        }
    }

    /// <summary>v2 布局组装：[Magic 4B][Ver 1B][NodeId 16B][KeyLen 4B][材料][CRC32 4B]——CRC 覆盖 [Ver..材料尾]。</summary>
    private static byte[] BuildV2(NodeId id, ReadOnlyMemory<byte> material, out int crcOffset)
    {
        // 布局 = [Magic 4][Ver 1][NodeId 16]（21B 头）+ [KeyLen 4B][材料][CRC32 4B]
        var bytes = new byte[21 + KeyLenBytes + material.Length + 4];
        Magic.CopyTo(bytes, 0);
        bytes[4] = FormatVersionV2;
        id.CopyTo(bytes.AsSpan(5));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(21, KeyLenBytes), material.Length);
        material.Span.CopyTo(bytes.AsSpan(25));
        crcOffset = 25 + material.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset),
            UnifiedCrc.ComputeCrc32(bytes.AsSpan(4, crcOffset - 4)));
        return bytes;
    }

    /// <summary>
    /// 密钥材料持久化（二期-H5 NETGAP-039——TierFsIdentityStore 升级位落地）：
    /// 原子升级身份文件至 v2（保留 NodeId，写入材料；重复保存 = 覆盖）。
    /// 权限收紧随介质宿主（IFileSystem 无权限语义面——文档见类型契约）。
    /// </summary>
    /// <param name="material">密钥材料字节（≤ 4096B；空数组 = 清除材料）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>身份文件原子升级至 v2 并落盘（temp 写全刷盘 → rename）后完成——NodeId 保留、材料覆盖、进程内缓存失效（下次 LoadOrCreateAsync 重载）。</returns>
    public async ValueTask SaveKeyMaterialAsync(byte[] material, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(material.Length, MaxKeyMaterialBytes);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cached = null;
            var identity = _fs.Exists(_path)
                ? await LoadAsync(ct).ConfigureAwait(false)
                : new NodeIdentity { Id = NodeId.NewRandom() };
            var bytes = BuildV2(identity.Id, (ReadOnlyMemory<byte>)material, out var crcOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset),
                UnifiedCrc.ComputeCrc32(bytes.AsSpan(4, crcOffset - 4)));
            await WriteAtomicAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>释放进程内串行门。</summary>
    public void Dispose() => _gate.Dispose();
}
