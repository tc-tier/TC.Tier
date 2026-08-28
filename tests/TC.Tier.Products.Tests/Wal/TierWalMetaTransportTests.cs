using TC.Tier.Contracts.Meta;
using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// MetaPolicyKind.Transport 语义验证（此前零测试覆盖）：
/// ① 注入 IMetaTransport（自定义介质：单槽文件/远程/KV 皆可）——meta block 经传输持久化，重启经
///    传输读回（水位 + opaque 恢复闭环）；
/// ② 不注入回落 MetaHost——meta entry 嵌入 log 流，重启自 log 恢复。
/// </summary>
public class TierWalMetaTransportTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("tc-wal-meta-transport");
    private readonly List<IFileSystem> _fss = [];

    public void Dispose()
    {
        foreach (var fs in _fss) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
        GC.SuppressFinalize(this);
    }

    private IFileSystem NewVolume()
    {
        var vol = Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.raw");
        var fs = TierFs.New($"virtual:///{vol.Replace('\\', '/')}");
        _fss.Add(fs);
        return fs;
    }

    /// <summary>内存传输——模拟自定义介质（单槽覆盖语义：last-write-wins）。</summary>
    private sealed class InMemoryMetaTransport : IMetaTransport
    {
        private byte[] _last = [];
        public int WriteCount { get; private set; }

        public void WriteBlock(ReadOnlySpan<byte> block)
        {
            _last = block.ToArray();
            WriteCount++;
        }

        public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
        {
            _last = block.ToArray();
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public ReadOnlySpan<byte> ReadLastBlock() => _last;

        public ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
            => ValueTask.FromResult<ReadOnlyMemory<byte>>(_last);
    }

    [Fact]
    public async Task Transport_Injected_RestartRestoresWatermarks()
    {
        using var fs = NewVolume();
        var transport = new InMemoryMetaTransport();

        await using (var wal = await TierWalOptions.Default
                       .WithMetaPolicyKind(MetaPolicyKind.Transport)
                       .Builder(fs).WithMetaTransport(transport).StartAsync())
        {
            var batch = Enumerable.Range(1, 5).Select(i => (ReadOnlyMemory<byte>)new byte[64]).ToList();
            await wal.AppendBatchAsync(batch, default);
            await wal.WriteMetaAsync(new byte[] { 0xAB, 0xCD }, default);
            wal.AllocatedIndex.Should().Be(5);
        }
        transport.WriteCount.Should().BeGreaterThan(0, "提交水位必须经传输持久化（meta block 写入自定义介质）");

        // 重启：同一传输实例——meta 经 ReadLastBlock 读回
        await using var wal2 = await TierWalOptions.Default
            .WithMetaPolicyKind(MetaPolicyKind.Transport)
            .Builder(fs).WithMetaTransport(transport).StartAsync();
        wal2.AllocatedIndex.Should().Be(5, "opaque 水位经传输恢复");
        wal2.PersistedIndex.Should().Be(5);
        wal2.ReadMeta().ToArray().Should().Equal(new byte[] { 0xAB, 0xCD }, "raft 元数据经传输恢复");
    }

    [Fact]
    public async Task Transport_NoInjection_FallsBackToMetaHost()
    {
        using var fs = NewVolume();

        // 不注入传输——回落 MetaHost（meta entry 嵌入 log 流）
        await using (var wal = await TierWalOptions.Default
                       .WithMetaPolicyKind(MetaPolicyKind.Transport)
                       .Builder(fs).StartAsync())
        {
            var batch = Enumerable.Range(1, 5).Select(i => (ReadOnlyMemory<byte>)new byte[64]).ToList();
            await wal.AppendBatchAsync(batch, default);
            await wal.CommitAsync(default);
        }

        await using var wal2 = await TierWalOptions.Default
            .WithMetaPolicyKind(MetaPolicyKind.Transport)
            .Builder(fs).StartAsync();
        // meta entry 嵌入 log 流（占物理位置但不占业务 index——恢复扫描/重放口径一致）
        wal2.AllocatedIndex.Should().Be(5, "回落路径：meta entry 不占业务 index，重启水位 = 业务水位");
        wal2.PersistedIndex.Should().Be(5);
        var entries = await TierWalTests.ReadAll(wal2, 1, default);
        entries.Should().HaveCount(5, "重放面只含业务 entry（meta entry 不混入 raft 重放）");
        entries.Select(e => e.Index).Should().Equal(new long[] { 1, 2, 3, 4, 5 }, "业务 index 连续（meta 占位不破坏 raft index 语义）");
    }
}
