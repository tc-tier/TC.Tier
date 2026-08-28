using FluentAssertions;
using TC.Tier.Contracts.Meta;
using TC.Tier.Runtime.Structures.Snapshot;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Runtime.Tests.Structures.Snapshot;

/// <summary>
/// IncrementalSnapshot 契约测试（增量快照结构——方案 A 段增量落地面）：
/// 段追加/拼接读 roundtrip、段表 meta 持久化恢复、首段孤儿转正、半写段忽略、合并、
/// 空存储。生命周期：new + Initialize()（后台恢复）+ WaitForReady()。
/// </summary>
public class IncrementalSnapshotTests
{
    private static IncrementalSnapshotSettings MakeSettings(string engineName = "test.inc",
        int compactThreshold = 100, MetaPolicyKind metaKind = MetaPolicyKind.Managed, int opaqueBytes = 4096,
        bool deleteOnClose = false)
        => new(new StorageEngineOptions(engineName, 1L << 24, enableSegmentation: false)
            .WithDeleteOnClose(deleteOnClose))
        {
            CompactSegmentThreshold = compactThreshold,
            MetaPolicyKind = metaKind,
            MetaOpaqueBytes = opaqueBytes,
        };

    private static async Task<byte[]> AppendChunkAsync(IncrementalSnapshot snap, long n0, int size, byte fill)
    {
        var data = new byte[size];
        Array.Fill(data, fill);
        await snap.AppendSegmentAsync(n0, OneChunk(data), default);
        return data;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> OneChunk(byte[] data)
    {
        yield return data;
    }

    private static async Task<byte[]> ReadAllAsync(IncrementalSnapshot snap)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in snap.ReadAllChunksAsync(default))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }

    [Fact]
    public async Task AppendSegments_ReadAll_Roundtrip_SkipsPrefix()
    {
        var vol = new TestVolume();
        try
        {
            using var snap = new IncrementalSnapshot(vol.Fs, MakeSettings());
            snap.Initialize();
            snap.WaitForReady();

            var d1 = await AppendChunkAsync(snap, 100, 3000, 0x11);
            var d2 = await AppendChunkAsync(snap, 200, 5000, 0x22);
            var d3 = await AppendChunkAsync(snap, 300, 4000, 0x33);

            snap.SegmentCount.Should().Be(3);
            snap.LatestN0.Should().Be(300);
            snap.GetSegmentN0(0).Should().Be(100);
            snap.GetSegmentN0(2).Should().Be(300);

            var all = await ReadAllAsync(snap);
            all.Should().Equal(d1.Concat(d2).Concat(d3).ToArray(), "拼接全部段 = 最新快照（前缀被跳过）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Restart_SegmentTable_Recovered_ReadConsistent()
    {
        var vol = new TestVolume();
        try
        {
            byte[] expected;
            await using (var snap1 = new IncrementalSnapshot(vol.Fs, MakeSettings()))
            {
                snap1.Initialize();
                snap1.WaitForReady();
                var d1 = await AppendChunkAsync(snap1, 1000, 4096, 0x41);
                var d2 = await AppendChunkAsync(snap1, 2000, 8192, 0x42);
                expected = d1.Concat(d2).ToArray();
            }

            using var snap2 = new IncrementalSnapshot(vol.Fs, MakeSettings());
            snap2.Initialize();
            snap2.WaitForReady();

            snap2.SegmentCount.Should().Be(2, "段表从 opaque meta O(1) 恢复");
            snap2.LatestN0.Should().Be(2000);
            snap2.GetSegmentN0(0).Should().Be(1000);
            (await ReadAllAsync(snap2)).Should().Equal(expected);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task HalfWrittenSegment_AbortedOnFailure_OldSegmentsIntact()
    {
        // ★ 会话模式（失败即清理）：chunk 流异常 = 事务 Abort——尾截断回滚（新段物理清除），旧段完好
        var vol = new TestVolume();
        try
        {
            byte[] d1;
            await using (var snap1 = new IncrementalSnapshot(vol.Fs, MakeSettings()))
            {
                snap1.Initialize();
                snap1.WaitForReady();
                d1 = await AppendChunkAsync(snap1, 100, 4096, 0x61);

                // 第 2 段 chunk 流抛异常——Abort 回滚（WriteAddress 回退到旧段尾）
                var act = async () => await snap1.AppendSegmentAsync(200, BrokenChunks(), default);
                (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
                    .Contain("boom", "chunk 流异常传播");
                snap1.SegmentCount.Should().Be(1, "Abort 后段表不变（未提交段不注册）");
            }

            using var snap2 = new IncrementalSnapshot(vol.Fs, MakeSettings());
            snap2.Initialize();
            snap2.WaitForReady();

            snap2.SegmentCount.Should().Be(1, "悬干/未提交段不恢复——已提交段完好");
            snap2.LatestN0.Should().Be(100);
            (await ReadAllAsync(snap2)).Should().Equal(d1);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task ImportSegment_Replaces_OldSegmentsReclaimed()
    {
        // ★ 导入（替换语义）：新段 = 完整镜像（含旧内容）——段表替换为单段 + 旧段物理回收
        var vol = new TestVolume();
        try
        {
            byte[] expected;
            await using (var snap1 = new IncrementalSnapshot(vol.Fs, MakeSettings()))
            {
                snap1.Initialize();
                snap1.WaitForReady();
                var d1 = await AppendChunkAsync(snap1, 10, 2048, 0x11);
                var d2 = await AppendChunkAsync(snap1, 20, 2048, 0x22);   // 2 段

                // 导入完整镜像（内容 = 全部条目——替换 2 段为 1 段）
                expected = d1.Concat(d2).ToArray();
                await snap1.ImportSegmentAsync(20, OneChunk(expected), default);

                snap1.SegmentCount.Should().Be(1, "导入替换——段表 = 单段");
                snap1.LatestN0.Should().Be(20);
                (await ReadAllAsync(snap1)).Should().Equal(expected);
            }

            // 重启：替换后段表持久化（1 段）——读一致
            using var snap2 = new IncrementalSnapshot(vol.Fs, MakeSettings());
            snap2.Initialize();
            snap2.WaitForReady();
            snap2.SegmentCount.Should().Be(1);
            snap2.LatestN0.Should().Be(20);
            (await ReadAllAsync(snap2)).Should().Equal(expected);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task ImportSegment_Failure_Aborts_OldSnapshotIntact()
    {
        // ★ 会话模式（失败即清理）：导入 chunk 流异常 = Abort——新段清除，旧快照完好
        var vol = new TestVolume();
        try
        {
            byte[] old;
            await using (var snap1 = new IncrementalSnapshot(vol.Fs, MakeSettings()))
            {
                snap1.Initialize();
                snap1.WaitForReady();
                old = await AppendChunkAsync(snap1, 50, 4096, 0x5A);

                var act = async () => await snap1.ImportSegmentAsync(100, BrokenChunks(), default);
                (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("boom");
                snap1.SegmentCount.Should().Be(1, "导入失败——段表不变");
                snap1.LatestN0.Should().Be(50, "旧快照覆盖点保留");
                (await ReadAllAsync(snap1)).Should().Equal(old, "旧快照内容完好");
            }

            using var snap2 = new IncrementalSnapshot(vol.Fs, MakeSettings());
            snap2.Initialize();
            snap2.WaitForReady();
            snap2.SegmentCount.Should().Be(1, "重启后仍只旧快照（失败段已回滚清除）");
            snap2.LatestN0.Should().Be(50);
            (await ReadAllAsync(snap2)).Should().Equal(old);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Compact_Threshold_Triggered_ReadConsistent()
    {
        var vol = new TestVolume();
        try
        {
            byte[] expected;
            long n0;
            await using (var snap1 = new IncrementalSnapshot(vol.Fs,
                MakeSettings(compactThreshold: 3)))
            {
                snap1.Initialize();
                snap1.WaitForReady();
                var d1 = await AppendChunkAsync(snap1, 1, 2048, 0x01);
                var d2 = await AppendChunkAsync(snap1, 2, 2048, 0x02);
                var d3 = await AppendChunkAsync(snap1, 3, 2048, 0x03);   // 段数 ≥ 3 → 合并
                var d4 = await AppendChunkAsync(snap1, 4, 2048, 0x04);   // 合并后新段

                snap1.SegmentCount.Should().Be(2, "3 段触发合并 → 1 基线段 + 第 4 段");
                expected = d1.Concat(d2).Concat(d3).Concat(d4).ToArray();
                n0 = 4;
                (await ReadAllAsync(snap1)).Should().Equal(expected);
            }

            // 重启：合并后段表持久化（1 基线段 + 1 段）——读一致
            using var snap2 = new IncrementalSnapshot(vol.Fs, MakeSettings(compactThreshold: 3));
            snap2.Initialize();
            snap2.WaitForReady();
            snap2.SegmentCount.Should().Be(2);
            snap2.LatestN0.Should().Be(n0);
            (await ReadAllAsync(snap2)).Should().Equal(expected);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task EmptyStore_NoSegments_LatestN0Zero()
    {
        var vol = new TestVolume();
        try
        {
            await using (var snap1 = new IncrementalSnapshot(vol.Fs, MakeSettings()))
            {
                snap1.Initialize();
                snap1.WaitForReady();
                snap1.SegmentCount.Should().Be(0);
                snap1.LatestN0.Should().Be(0);
                (await ReadAllAsync(snap1)).Should().BeEmpty();
            }

            using var snap2 = new IncrementalSnapshot(vol.Fs, MakeSettings());
            snap2.Initialize();
            snap2.WaitForReady();
            snap2.SegmentCount.Should().Be(0);
            snap2.LatestN0.Should().Be(0);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void SegmentTable_Serialize_Deserialize_Roundtrip()
    {
        var segs = new[]
        {
            new IncrementalSnapshot.SegmentInfo(new LogicalAddress(0, 0), new LogicalAddress(0, 0), 10),
            new IncrementalSnapshot.SegmentInfo(new LogicalAddress(0, 1024), new LogicalAddress(0, 1536), 20),
        };
        var buf = IncrementalSnapshot.SerializeSegments(segs);
        var back = IncrementalSnapshot.DeserializeSegments(buf);
        back.Should().HaveCount(2);
        back[0].Should().Be(segs[0]);
        back[1].Should().Be(segs[1]);
    }

    /// <summary>chunk 流中途抛异常（模拟写中断——帧半写）。</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> BrokenChunks()
    {
        yield return new byte[1024];
        yield return new byte[1024];
        throw new InvalidOperationException("boom——模拟写中断");
    }
}
