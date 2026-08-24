using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Tests.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// HashIndex 主存储（Builtin——后台 dump 三段式帧 + 恢复三级回退中间级）契约测试。
/// <para>★ 契约面：净重开零重放/增量重放（W&lt;尾）/无帧回退全量/PersistenceKind=None 关闭开关/
///   覆写最新写胜出跨帧/多版本轮替回收。</para>
/// <para>★ W = ring 已落盘水位（FlushedUntilAddress——组合层契约：Insert 先于落盘，已落盘必已入索引）；
///   dump 前须 FlushUntil(Tail) 推进落盘水位，恢复重放 (W, End] 补齐 dump 后新写。</para>
/// </summary>
public class HashIndexMainStorageTests
{
    static BlittableRingSettings RingSettings(TestVolume vol)
        => TestRingSettingsFactory.On(vol, "ms-ring", deleteOnClose: false,
            metaKind: MetaPolicyKind.Managed);

    static HashIndexSettings HashSettings(TestVolume vol, bool builtin = true)
        => TestProbingIndexSettingsFactory.On(vol, "ms-hash", deleteOnClose: false,
            persistenceKind: builtin ? ProbingIndexPersistenceKind.Builtin : ProbingIndexPersistenceKind.None,
            persistencePolicy: new ProbingIndexPersistencePolicy
            {
                Interval = TimeSpan.FromMinutes(10),   // 策略不自动触发——测试显式 TryDump
                EntryDeltaThreshold = long.MaxValue,
            });

    [Fact]
    public void MainStorageRecovery_ZeroReplay_AllFound()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring);
            for (long k = 1; k <= 200; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k * 7 + 1));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);   // ★ 推进已落盘水位 W=尾：dump 后重开零重放
            index.TryDump().Should().BeTrue();
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeTrue("帧有效且 W=尾——走主存储零重放路径");
        index2.EntryCount.Should().Be(200);
        var buf = new byte[16];
        for (long k = 1; k <= 200; k += 17)
        {
            var addr = index2.Find(k);
            addr.Should().NotBe(LogicalAddress.Empty, $"key {k} 帧载入后必命中");
            ring2.GetValue(addr, buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k * 7 + 1);
        }
        index2.Dispose();
    }

    [Fact]
    public void MainStorageRecovery_IncrementalReplay_DeltaApplied()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring);
            for (long k = 1; k <= 150; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);   // W=150 条处
            index.TryDump().Should().BeTrue();

            // dump 后增量：再写 50 条（不在帧内——重放 (W, End) 补齐）
            for (long k = 151; k <= 200; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.Prepare(seq: 2);
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeTrue("帧有效（W∈[Begin,End)）——增量路径");
        index2.EntryCount.Should().Be(200, "帧 150 + 增量重放 50");
        var buf = new byte[16];
        for (long k = 148; k <= 200; k += 3)   // 重点打帧/增量边界两侧
        {
            var addr = index2.Find(k);
            addr.Should().NotBe(LogicalAddress.Empty);
            ring2.GetValue(addr, buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k);
        }
        index2.Dispose();
    }

    [Fact]
    public void NoFrame_FallsBackToFullReplay()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring);
            for (long k = 1; k <= 50; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.Prepare(seq: 1);
            index.Dispose();   // ★ 从未 TryDump——主存储无帧
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeFalse("无帧=fail-safe 全量重放");
        index2.EntryCount.Should().Be(50);
        index2.Find(42).Should().NotBe(LogicalAddress.Empty);
        index2.Dispose();
    }

    [Fact]
    public void PersistenceKindNone_DisabledStorage()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol, builtin: false), ring);
            for (long k = 1; k <= 50; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeFalse("PersistenceKind=None——主存储关闭，不落帧");
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol, builtin: false), ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeFalse("关闭开关——纯重放恢复");
        index2.EntryCount.Should().Be(50);
        index2.Find(42).Should().NotBe(LogicalAddress.Empty);
        index2.Dispose();
    }

    [Fact]
    public void MainStorageAcrossOverwrite_LatestWins()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring);
            for (long k = 1; k <= 60; k++)
                index.Insert(k, ring.Write(k, BitConverter.GetBytes(k)), LogicalAddress.Empty);
            for (long k = 1; k <= 60; k += 2)   // 覆写一半
                index.Insert(k, ring.Write(k, BitConverter.GetBytes(-k)), LogicalAddress.Empty);
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeTrue();
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));
        var buf = new byte[16];

        index2.MainStorageAppliedLastRecovery.Should().BeTrue();
        index2.EntryCount.Should().Be(60, "帧保留覆写折叠结果");
        for (long k = 1; k <= 60; k++)
        {
            var addr = index2.Find(k);
            addr.Should().NotBe(LogicalAddress.Empty);
            ring2.GetValue(addr, buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k % 2 == 0 ? k : -k, "最新写胜出跨帧成立");
        }
        index2.Dispose();
    }

    [Fact]
    public void FuzzyDump_ConcurrentWriters_RecoveryConverges()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring);

            // 单写者预置 200 条 + 落盘水位推进
            for (long k = 1; k <= 200; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);

            // ★ fuzzy dump：dump 进行中并发插入 300 条（跨换代 + 在途 Tentative 竞态全开）
            var more = Task.Run(() =>
            {
                for (long k = 201; k <= 500; k++)
                {
                    var addr = ring.Write(k, BitConverter.GetBytes(k));
                    index.Insert(k, addr, LogicalAddress.Empty);
                }
            });
            index.TryDump().Should().BeTrue();   // dump 与并发写同跑——帧可能含 >W 新条目或缺在途
            more.Wait();

            ring.Prepare(seq: 2);   // 全部落盘（W 推进到 500 条处）
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, HashSettings(vol), ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        // ★ 收敛性：帧（可能 fuzzy/缺条目）+ 重放 (W, End] → 最终 500 条全命中
        index2.EntryCount.Should().Be(500, "fuzzy 帧 + 幂等重放收敛到全量");
        var buf = new byte[16];
        for (long k = 1; k <= 500; k += 37)
        {
            var addr = index2.Find(k);
            addr.Should().NotBe(LogicalAddress.Empty, $"key {k} fuzzy 收敛后必命中");
            ring2.GetValue(addr, buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k);
        }
        index2.Dispose();
    }

    [Fact]
    public void VersionRotation_KeepsNewestFrame_ReclaimsOldest()
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            var settings = TestProbingIndexSettingsFactory.On(vol, "rot-hash", deleteOnClose: false,
                persistenceKind: ProbingIndexPersistenceKind.Builtin,
                persistencePolicy: new ProbingIndexPersistencePolicy
                {
                    Interval = TimeSpan.FromMinutes(10),
                    EntryDeltaThreshold = long.MaxValue,
                },
                persistenceKeepVersions: 2);
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, settings, ring);

            // 三代 dump：每代之间新增条目，验证恢复取最新帧（只含最新代折叠）
            for (long k = 1; k <= 50; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeTrue();            // 代 1

            for (long k = 51; k <= 100; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeTrue();            // 代 2（N=2：代 1 应已被回收）

            for (long k = 101; k <= 150; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeTrue();            // 代 3（代 2 回收，保留 代 2-3）
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol,
            TestProbingIndexSettingsFactory.On(vol, "rot-hash", deleteOnClose: false),
            ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeTrue("最新帧（代 3）有效");
        index2.EntryCount.Should().Be(150, "最新帧折叠 150 条（回收不丢数据——帧仅加速，重放兜底）");
        index2.Find(150).Should().NotBe(LogicalAddress.Empty);
        index2.Find(1).Should().NotBe(LogicalAddress.Empty);
        index2.Dispose();
    }
}
