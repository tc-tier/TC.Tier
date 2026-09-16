using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO.Testing;

/// <summary>
/// FaultInjectingFileSystem 规则族扩展测试（故障注入面补全设计 件三——IO 缝三类扩展）：
/// 腐败（写转发成功后翻转落盘字节）、慢 IO（前置延迟）、挂起（永不到达内层）、乱序（异步写族延后提交）。
/// <para>★ 与既有错误族（AddRule/AddExceptionRule）互补——六族可叠加，各管各的注入维度。</para>
/// </summary>
public sealed class FaultInjectingFileSystemRuleFamilyTests
{
    private static FileOpenOptions Opts() =>
        new() { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate };

    // ═══════════════ CorruptRule（静默腐败）═══════════════

    [Fact]
    public void Corrupt_WriteThenFlip_ReadBackShowsMaskedBytes()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddCorruptRule("data", "Write", offset: 8, mask: new byte[] { 0xFF, 0x00, 0x01 });

        var payload = new byte[32];
        for (var i = 0; i < payload.Length; i++) payload[i] = 0xAB;
        using (var h = fi.Open("data", Opts()))
        {
            h.Write(0, payload);
        }

        using var verify = inner.Open("data", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting });
        var onDisk = new byte[32];
        verify.Read(0, onDisk);
        onDisk[7].Should().Be(0xAB, "offset 之前不触碰");
        onDisk[8].Should().Be((byte)(0xAB ^ 0xFF), "掩码第 0 字节循环应用");
        onDisk[9].Should().Be(0xAB, "掩码第 1 字节 = 0x00 不翻转");
        onDisk[10].Should().Be((byte)(0xAB ^ 0x01), "掩码第 2 字节循环应用");
        onDisk[11].Should().Be(0xAB, "区间之后不触碰");
    }

    [Fact]
    public void Corrupt_AtCallIndex_DeterministicFiring()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddCorruptRule("*", "Append", offset: 0, mask: new byte[] { 0xFF },
            failAtCallIndex: 2);   // 第 2 次 Append 才腐败

        using var h = fi.Open("det", Opts());
        h.Append(new byte[] { 1 });
        byte[] probe = [1];
        h.Read(0, probe);
        probe[0].Should().Be(1, "首次追加不腐败");

        h.Append(new byte[] { 2 });
        h.Read(0, probe);
        probe[0].Should().Be((byte)(1 ^ 0xFF), "第二次追加命中——翻转规则偏移 0 的介质现存字节（首次追加落盘的 1）");
        h.Read(1, probe);
        probe[0].Should().Be(2, "第二次追加自身字节在偏移 1——不在掩码区间，不触碰");
    }

    [Fact]
    public void Corrupt_ClearRules_StopsCorruption()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddCorruptRule("*", "Write", offset: 0, mask: new byte[] { 0xFF });
        fi.ClearRules();

        using var h = fi.Open("x", Opts());
        h.Write(0, new byte[] { 7 });
        byte[] probe = [7];
        h.Read(0, probe);
        probe[0].Should().Be(7, "规则清空后写不再腐败");
    }

    // ═══════════════ DelayRule（慢 IO）═══════════════

    [Fact]
    public void Delay_SyncOp_PrependsLatency()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddDelayRule("slow", "Write", TimeSpan.FromMilliseconds(80));

        using var h = fi.Open("slow", Opts());
        var sw = Stopwatch.StartNew();
        h.Write(0, new byte[4]);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(70), "前置延迟生效（时钟粒度留 10ms 余量）");
    }

    [Fact]
    public async Task Delay_AsyncOp_PrependsLatency()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddDelayRule("slow", "Write", TimeSpan.FromMilliseconds(80));

        await using var h = fi.Open("slow", Opts());
        var sw = Stopwatch.StartNew();
        await h.WriteAsync(0, new byte[4], CancellationToken.None);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(70), "异步路径前置延迟同样生效");
    }

    // ═══════════════ HangRule（挂起）═══════════════

    [Fact]
    public async Task Hang_OpNeverReachesInner_UntilClearRules()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddHangRule("stuck", "Write");

        using var h = fi.Open("stuck", Opts());
        var writeTask = Task.Run(() => h.Write(0, new byte[4]));
        await Task.Delay(50);
        writeTask.IsCompleted.Should().BeFalse("挂起规则 = 操作永不到达内层");

        fi.ClearRules();   // 拆除放行（被挂线程不跨 Dispose 阻塞）
        (await Task.WhenAny(writeTask, Task.Delay(2000))).Should().Be(writeTask, "ClearRules 置位释放门");
        writeTask.IsCompletedSuccessfully.Should().BeTrue("放行后操作照常完成");
    }

    [Fact]
    public async Task Hang_AsyncOp_CancellationIsTheBoundedWaitPath()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddHangRule("*", "Read");

        await using var h = fi.Open("c", Opts());
        using var cts = new CancellationTokenSource(80);
        var act = () => h.ReadAsync(0, new byte[4], cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>("上层有界等待 = 取消路径先行处置");
    }

    // ═══════════════ ReorderRule（乱序提交）═══════════════

    [Fact]
    public async Task Reorder_AsyncWriteDeferred_LaterWriteLandsFirst()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        // 第一次 Write 被扣住 150ms；其后的 Write 不受影响照常落盘
        fi.AddReorderRule("ro", "Write", TimeSpan.FromMilliseconds(150));

        await using var h = fi.Open("ro", Opts());
        var held = h.WriteAsync(0, new byte[] { 1 }, CancellationToken.None);
        await Task.Delay(30);
        held.IsCompleted.Should().BeFalse("命中乱序规则——延后提交");

        h.Write(8, new byte[] { 2 });   // 后到达的操作先落盘（受控乱序）
        byte[] probe = [0];
        h.Read(8, probe);
        probe[0].Should().Be(2, "后写先达");

        await held;   // 被扣操作延后提交
        h.Read(0, probe);
        probe[0].Should().Be(1, "hold 到期后提交到位");
    }

    [Fact]
    public async Task Reorder_AppendAsync_ResultTransparent()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddReorderRule("*", "Append", TimeSpan.FromMilliseconds(40));

        await using var h = fi.Open("ra", Opts());
        var start = await h.AppendAsync(new byte[] { 9 }, CancellationToken.None);
        start.Should().Be(0, "乱序延后不改变返回值（预留起始偏移透传）");
        h.Length.Should().Be(1);
    }

    [Fact]
    public void Reorder_SyncOp_NeverMatches()
    {
        using var inner = MemoryFileSystem.New();
        using var fi = new FaultInjectingFileSystem(inner);
        fi.AddReorderRule("*", "Write", TimeSpan.FromMilliseconds(100));

        using var h = fi.Open("sync", Opts());
        var act = () => h.Write(0, new byte[4]);
        act.Should().NotThrow("乱序注入仅异步写族生效——同步操作不命中（文档契约）");
    }
}
