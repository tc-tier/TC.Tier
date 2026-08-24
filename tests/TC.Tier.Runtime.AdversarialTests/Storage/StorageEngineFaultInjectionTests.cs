using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 引擎错误路径契约测试（FaultInjectingFileSystem——Core IO 测试替身，InternalsVisibleTo 开放）。
/// <para>★ 异常面统一承诺：引擎透传 Core 的 FileIOException（IOError 语义码）——本组以确定性注入
///   （failAtCallIndex）验证语义码不丢失 + StartReclaim 的 Failed 事件/断点协议。</para>
/// </summary>
public sealed class StorageEngineFaultInjectionTests : StorageEngineTestBase
{
    private static IStorageEngine NewEngine(FaultInjectingFileSystem fi, long? spinMs = null)
    {
        var options = new StorageEngineOptions("fault", segmentGrowthLimit: 4096)
            .WithPreallocateFile(false);
        if (spinMs is { } ms)
            options = options.WithOptimization(options.Optimization with { SpinMilliseconds = ms });   // 负测试缩短 AcquireExtent 自旋到期（默认 30s 太慢）
        var dev = options.Builder(fi).Start();
        dev.WaitForReady();
        return dev;
    }

    [Fact]
    public void Append_WriteFault_ThrowsFileIOExceptionWithSemanticCode()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi);
        dev.Append(MakePattern(256, 0x11));            // 无规则期先写一笔（计数基准）

        fi.AddRule("*", "Write", IOError.DiskFull, failAtCallIndex: 1);

        var act = () => dev.Append(MakePattern(256, 0x22));
        act.Should().Throw<FileIOException>("引擎透传 Core 语义异常")
           .Which.Error.Should().Be(IOError.DiskFull, "IOError 语义码不丢失");
    }

    [Fact]
    public void Flush_FlushFault_PropagatesSemanticCode()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi);
        dev.Append(MakePattern(256, 0x11));

        fi.AddRule("*", "Flush", IOError.IOFailure, failAtCallIndex: 1);

        var act = () => dev.Flush();
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.IOFailure);
    }

    [Fact]
    public async Task StartReclaim_PunchHoleFault_RaisesFailedWithLastPunchedOffset()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi);
        dev.Append(MakeSequential(4096));              // 垫满 seg0
        dev.Append(MakeSequential(1000));              // (1, 0)
        var keep = dev.Append(MakePattern(1000, 0x5C)); // (1, 1000)——区间外保留锚点

        // 打洞域 [(0,1000)..(1,500))：chunk1 = seg0[1000..4096]，chunk2 = seg1[0..500)
        fi.AddRule("*", "PunchHole", IOError.IOFailure, failAtCallIndex: 2);   // 第 2 次打洞注入

        var op = dev.StartReclaim(new LogicalAddress(0, 1000), new LogicalAddress(1, 500),
            CancellationToken.None);
        var progresses = new List<double>();
        op.Progress += (_, ratio) => progresses.Add(ratio);
        Exception? failed = null;
        op.Failed += (_, ex) => failed = ex;
        bool missedByRace = op.IsCompleted;            // 订阅后查态（竞态防护，见 StartReclaim 测试）

        var wait = () => op.WaitAsync(CancellationToken.None).AsTask();
        await wait.Should().ThrowAsync<FileIOException>("第 2 chunk 打洞失败 → tcs 落异常");

        if (!missedByRace)
        {
            failed.Should().NotBeNull("Failed 事件必发（事件先于 tcs 落定的次序契约）");
            failed.Should().BeOfType<FileIOException>();
            // ★ Progress 计数不硬断言——订阅前后台首 chunk 可能已触发（StartReclaim 返回与订阅间的
            //   固有竞态；本质断言是下面的 lastPunchedOffset——它来自 op 内部状态，无竞态）
            progresses.Should().NotBeNull();
            progresses.Count.Should().BeLessThanOrEqualTo(1, "首 chunk 打洞成功的 Progress 至多一次");
            var lastPunched = (LogicalAddress?)failed!.Data["lastPunchedOffset"];
            lastPunched.Should().Be(new LogicalAddress(0, 4096),
                "断点 = 首 chunk 打洞完成处——调用方据此重试剩余区间");
        }

        // 已打洞的不可回退：seg0[1000..4096] 已归零；seg1[0..500) 回滚落 Aborted（终态不可读——
        // punch/commit 非原子窗口保守拒读），读它须快速失败而非挂死（活性守卫）
        var punched = new byte[500];
        dev.Read(new LogicalAddress(0, 1000), punched).Should().Be(500);
        punched.Should().OnlyContain(b => b == 0, "首 chunk 已物理打洞（PunchHole 不可回退）");
        var act = () => dev.Read(new LogicalAddress(1, 0), new byte[500]);
        act.Should().Throw<PartitionInvalidException>(
            "Abort 区间终态不可读——快速失败（曾无限自旋挂死，StartReclaim 部分失败后引擎楔死）");
        var kept = new byte[1000];
        dev.Read(keep, kept).Should().Be(1000);
        kept.Should().Equal(MakePattern(1000, 0x5C), "区间外数据保留");
    }

    // ═══════════════════════════════════════════════════════════════
    //  L1（2026-08-21）：Abort 区间重占——Reclaim 族可幂等重占 Aborted，
    //  Failed.lastPunchedOffset 断点重试由此可达（治愈全部毒化区）。
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartReclaim_PunchFault_RetryFromLastPunched_HealsPoisonedRange()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi);
        dev.Append(MakeSequential(4096));              // 垫满 seg0
        dev.Append(MakeSequential(1000));              // (1, 0)
        var keep = dev.Append(MakePattern(1000, 0x5C)); // (1, 1000)——区间外保留锚点

        var from = new LogicalAddress(0, 1000);
        var to = new LogicalAddress(1, 500);
        fi.AddRule("*", "PunchHole", IOError.IOFailure, failAtCallIndex: 2);   // chunk2（seg1 打洞）失败

        var op = dev.StartReclaim(from, to, CancellationToken.None);
        Exception? failed = null;
        op.Failed += (_, ex) => failed = ex;
        var wait = () => op.WaitAsync(CancellationToken.None).AsTask();
        await wait.Should().ThrowAsync<FileIOException>();
        var lastPunched = (LogicalAddress?)failed!.Data["lastPunchedOffset"];
        lastPunched.Should().Be(new LogicalAddress(0, 4096), "断点 = 首 chunk 打洞完成处");

        // 毒化区现状：[lastPunched, to) 全部落 Aborted——读拒绝（活性守卫快速失败）
        var actRead = () => dev.Read(new LogicalAddress(1, 0), new byte[500]);
        actRead.Should().Throw<PartitionInvalidException>("毒化区终态不可读");

        // ★ L1 核心：断点重试可达——Reclaim 族可幂等重占 Aborted（再 punch 两分支收敛同终态）
        fi.ClearRules();
        var retry = dev.StartReclaim(lastPunched!.Value, to, CancellationToken.None);
        await retry.WaitAsync(CancellationToken.None);

        // 毒化区治愈：全程读零（Committed+sparse），不再 PartitionInvalid
        var buf = new byte[500];
        dev.Read(new LogicalAddress(1, 0), buf).Should().Be(500);
        buf.Should().OnlyContain(b => b == 0, "重试成功 = 毒化区变 Committed+sparse（读零）");
        dev.Read(new LogicalAddress(0, 1000), buf).Should().Be(500);
        buf.Should().OnlyContain(b => b == 0, "首 chunk 打洞结果保持");
        var kept2 = new byte[1000];
        dev.Read(keep, kept2).Should().Be(1000);
        kept2.Should().Equal(MakePattern(1000, 0x5C), "区间外数据保留");

        // 治愈后区间可再占（正常占用语义恢复）
        dev.Write(new LogicalAddress(0, 1000), MakePattern(100, 0x77)).Should().Be(new LogicalAddress(0, 1000));
    }

    [Fact]
    public void Reclaim_SyncPunchFault_Retry_Heals()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi);
        dev.Append(MakeSequential(4096));
        dev.Append(MakeSequential(1000));

        fi.AddRule("*", "PunchHole", IOError.DiskFull, failAtCallIndex: 1);
        var act = () => dev.Reclaim(new LogicalAddress(0, 0), new LogicalAddress(0, 2000));
        act.Should().Throw<FileIOException>("同步路径打洞失败直抛");

        // 同步失败同样毒化 [0, 2000) → Aborted；重试治愈
        var readPoisoned = () => dev.Read(new LogicalAddress(0, 0), new byte[100]);
        readPoisoned.Should().Throw<PartitionInvalidException>("毒化区读拒绝");

        fi.ClearRules();
        var retryAct = () => dev.Reclaim(new LogicalAddress(0, 0), new LogicalAddress(0, 2000));
        retryAct.Should().NotThrow("断点重试可达（L1）");

        var buf = new byte[2000];
        dev.Read(new LogicalAddress(0, 0), buf).Should().Be(2000);
        buf.Should().OnlyContain(b => b == 0, "治愈后读零");
    }

    [Fact]
    public void ReclaimHead_ThroughPoisonedRange_Succeeds()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi);
        dev.Append(MakeSequential(4096));
        dev.Append(MakeSequential(4096));
        dev.Append(MakePattern(500, 0x33));            // (2, 0) 段外锚点

        // 毒化 seg0 全段
        fi.AddRule("*", "PunchHole", IOError.IOFailure, failAtCallIndex: 1);
        var act = () => dev.Reclaim(new LogicalAddress(0, 0), new LogicalAddress(0, 2000));
        act.Should().Throw<FileIOException>();
        fi.ClearRules();

        // ★ ReclaimHead（头族——同 SrcReclaim）越过毒化区推进：正常回收不应被毒化区卡死
        var headAct = () => dev.ReclaimHead(new LogicalAddress(1, 0));
        headAct.Should().NotThrow("头截断穿过毒化段（Reclaim 族重占 Aborted）");
        dev.MinAddress.Should().Be(new LogicalAddress(1, 0), "MinAddress 推进过毒化段");

        var buf = new byte[500];
        dev.Read(new LogicalAddress(2, 0), buf).Should().Be(500);
        buf.Should().Equal(MakePattern(500, 0x33), "毒化段之后的数据保留");
    }

    [Fact]
    public void Write_OverPoisonedRange_StillRejected()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        using var dev = NewEngine(fi, spinMs: 300);
        dev.Append(MakeSequential(4096));
        dev.Append(MakeSequential(1000));

        fi.AddRule("*", "PunchHole", IOError.IOFailure, failAtCallIndex: 1);
        var act = () => dev.Reclaim(new LogicalAddress(0, 0), new LogicalAddress(0, 2000));
        act.Should().Throw<FileIOException>();
        fi.ClearRules();

        // ★ 负测试：占用矩阵 §7.2 不变——Write 对 Aborted 仍拒绝（AcquireExtent 自旋到超时）
        //   （不是 PartitionInvalid——是区间不可占的 TimeoutException）
        var actWrite = () => dev.Write(new LogicalAddress(0, 100), MakePattern(100, 0x99));
        actWrite.Should().Throw<TimeoutException>(
            "Write 不可占 Aborted——窄通道只对 Reclaim 族开放（写入者不得无感填掉被追踪的永久洞）");
    }
}
