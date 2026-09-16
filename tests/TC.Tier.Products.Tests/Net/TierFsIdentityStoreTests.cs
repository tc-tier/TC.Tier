using System.Buffers.Binary;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Ports;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// TierFsIdentityStore 契约（spec-12 §8.3 / D6-T3——身份文件适配器）：
/// 首次生成持久化 / 跨实例（重启）稳定 / 损坏 fail-fast 禁重生成 / 并发首调收敛单身份。
/// </summary>
public class TierFsIdentityStoreTests
{
    private const string IdPath = "node.id";

    [Fact]
    public async Task FirstCall_Generates_Persists_LeavesNoTemp()
    {
        var fs = TierFs.New("memory:");
        using var store = new TierFsIdentityStore(fs, IdPath);

        var identity = await store.LoadOrCreateAsync();

        identity.Id.Should().NotBe(NodeId.Empty, "首次调用生成随机身份");
        fs.Exists(IdPath).Should().BeTrue("首次生成必须落盘");
        fs.Exists(IdPath + ".tmp").Should().BeFalse("rename 原子就位后 temp 不残留");
    }

    [Fact]
    public async Task AcrossInstances_SameIdentity()
    {
        var fs = TierFs.New("memory:");
        NodeId first;
        using (var store = new TierFsIdentityStore(fs, IdPath))
            first = (await store.LoadOrCreateAsync()).Id;

        using var reloaded = new TierFsIdentityStore(fs, IdPath);   // 重启 = 实例重建、卷存续
        var second = await reloaded.LoadOrCreateAsync();

        second.Id.Should().Be(first, "NodeId 跨重启稳定——成员表/votedFor 绑定前提");
    }

    [Fact]
    public async Task ConcurrentFirstCalls_SingleIdentity()
    {
        var fs = TierFs.New("memory:");
        using var store = new TierFsIdentityStore(fs, IdPath);

        var ids = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.LoadOrCreateAsync().AsTask()));

        ids.Select(i => i.Id).Distinct().Should().ContainSingle("进程内并发首次调用必须收敛到同一身份");
    }

    [Fact]
    public async Task CorruptedContent_FailsFast_NoRegeneration()
    {
        var fs = TierFs.New("memory:");
        NodeId original;
        using (var store = new TierFsIdentityStore(fs, IdPath))
            original = (await store.LoadOrCreateAsync()).Id;

        // 破坏 NodeId 首字节——CRC 随之失配
        using (var handle = fs.Open(IdPath, new FileOpenOptions { Access = AccessMode.ReadWrite }))
            handle.Write(5, new byte[] { 0xAB });

        using var reloaded = new TierFsIdentityStore(fs, IdPath);
        var act = () => reloaded.LoadOrCreateAsync().AsTask();

        (await act.Should().ThrowAsync<FileIOException>())
            .Which.Error.Should().Be(IOError.PreconditionFailed, "CRC 失配 = 内容损坏");
        fs.Exists(IdPath).Should().BeTrue("fail-fast——禁静默重生成（重生成=静默换节点身份）");
        _ = original;
    }

    [Fact]
    public async Task TruncatedFile_FailsFast()
    {
        var fs = TierFs.New("memory:");
        fs.CreateFile(IdPath);
        using (var handle = fs.Open(IdPath, new FileOpenOptions { Access = AccessMode.Write }))
            handle.Write(0, new byte[10]);

        using var store = new TierFsIdentityStore(fs, IdPath);
        var act = () => store.LoadOrCreateAsync().AsTask();

        (await act.Should().ThrowAsync<FileIOException>())
            .Which.Error.Should().Be(IOError.PreconditionFailed, "长度 ≠ 25B = 截断");
    }

    [Fact]
    public async Task ForeignMagic_FailsFast()
    {
        var fs = TierFs.New("memory:");
        fs.CreateFile(IdPath);
        using (var handle = fs.Open(IdPath, new FileOpenOptions { Access = AccessMode.Write }))
            handle.Write(0, new byte[25]);

        using var store = new TierFsIdentityStore(fs, IdPath);
        var act = () => store.LoadOrCreateAsync().AsTask();

        (await act.Should().ThrowAsync<FileIOException>())
            .Which.Message.Should().Contain("魔数", "25B 全零 = 魔数不匹配（非本适配器文件）");
    }

    [Fact]
    public async Task LoadOrCreateAsync_ReturnsIdentityWithEmptyKeyMaterial()
    {
        var fs = TierFs.New("memory:");
        using var store = new TierFsIdentityStore(fs, IdPath);

        var identity = await store.LoadOrCreateAsync();

        identity.KeyMaterial.Should().BeEmpty("密钥材料形态由 W-Security 波次定义（当前 Plaintext 档期）");
    }
    /// <summary>H5：密钥材料持久化——Save 后新实例加载同一 NodeId + 材料逐字节一致。</summary>
    [Fact]
    public async Task SaveKeyMaterial_RoundTrips_AcrossInstances()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-h5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = "identity.bin";   // TierFs 根内相对路径
        var store1 = new TierFsIdentityStore(TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"), path);
        var identity = await store1.LoadOrCreateAsync();
        var material = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        await store1.SaveKeyMaterialAsync(material);
        store1.Dispose();

        var store2 = new TierFsIdentityStore(TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"), path);
        try
        {
            var loaded = await store2.LoadOrCreateAsync();
            loaded.Id.Should().Be(identity.Id, "NodeId 跨重启稳定");
            loaded.KeyMaterial.Should().Equal(material, "密钥材料逐字节一致（v2 布局）");
        }
        finally { store2.Dispose(); }
    }

    /// <summary>H5：材料超限拒绝（防御上界 4096B）。</summary>
    [Fact]
    public async Task SaveKeyMaterial_Overflow_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-h5b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = "identity.bin";   // TierFs 根内相对路径
        var store = new TierFsIdentityStore(TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"), path);
        try
        {
            await store.LoadOrCreateAsync();
            var act = async () => await store.SaveKeyMaterialAsync(new byte[4097]);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>("材料防御上界");
        }
        finally
        {
            store.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>H5：v1 旧版身份文件（25B）加载兼容——NodeId 保留、材料空。</summary>
    [Fact]
    public async Task LegacyV1File_Loads_Compatible()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-h5c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = "identity.bin";   // TierFs 根内相对路径
        var fs = TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}");
        try
        {
            // 手工构造 v1：[Magic 4][Ver=1][NodeId 16][CRC32 4]
            var id = NodeId.NewRandom();
            var bytes = new byte[25];
            new[] { (byte)'T', (byte)'C', (byte)'I', (byte)'D' }.CopyTo(bytes, 0);
            bytes[4] = 1;
            id.CopyTo(bytes.AsSpan(5));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21),
                UnifiedCrc.ComputeCrc32(bytes.AsSpan(4, 17)));
            fs.CreateFile(path);
            using (var handle = fs.Open(path, new FileOpenOptions { Access = AccessMode.Write }))
                await handle.WriteAsync(0, bytes, CancellationToken.None);

            var store = new TierFsIdentityStore(fs, path);
            var loaded = await store.LoadOrCreateAsync();
            loaded.Id.Should().Be(id, "v1 兼容加载——NodeId 保留");
            loaded.KeyMaterial.Should().BeEmpty("v1 无材料段");
        }
        finally { fs.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }

}
