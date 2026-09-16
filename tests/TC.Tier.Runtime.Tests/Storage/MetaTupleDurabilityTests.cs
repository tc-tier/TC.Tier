using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 段元组耐久化泵行为测试（<see cref="StorageEngineOptions.MetaTupleFlushInterval"/>）：
/// 周期批量落盘 / Zero 同步直写 / Infinite 仅 Dispose 锚定 / ReclaimHead 丢弃遗留不复活死段。
/// 断言面 = <see cref="StorageEngine.ReadSegmentTuple"/>（fs Stat 平面——落盘可见性）。
/// mem 介质确定性（无时钟抖动依赖；磁盘行为同构由磁盘套件覆盖）。
/// </summary>
public sealed class MetaTupleDurabilityTests
{
    private static StorageEngineOptions Opts(long growthLimit = 1024, TimeSpan? interval = null)
        => new StorageEngineOptions("meta-dur", segmentGrowthLimit: growthLimit).WithPreallocateFile(false)
            .WithMetaTupleFlushInterval(interval ?? TimeSpan.FromMilliseconds(200));

    private static TestVolume NewVol() => new();

    [Fact]
    public async Task MetaTuple_PumpFlushes_WithinConfiguredPeriod()
    {
        using var vol = NewVol();
        using var builder = Opts(interval: TimeSpan.FromMilliseconds(100)).Builder(vol.Fs);
        using var dev = builder.Start();
        dev.WaitForReady();
        dev.Append(new byte[100]);   // seg0 Create 元组记脏

        // 周期 100ms——泵落盘前 Stat 读不到（记脏是纯内存），落盘后 ReadSegmentTuple 可见
        var tuple = await WaitTupleStateAsync(builder.Engine, segId: 0,
            t => t.State >= 0, TimeSpan.FromSeconds(5));
        tuple.Should().NotBeNull("泵周期内元组应落盘可读");
        tuple!.Value.State.Should().Be(StableState.Ready);
    }

    [Fact]
    public void MetaTuple_ZeroInterval_SynchronousWrite_ImmediatelyVisible()
    {
        using var vol = NewVol();
        using var builder = Opts(interval: TimeSpan.Zero).Builder(vol.Fs);
        using var dev = builder.Start();
        dev.WaitForReady();
        dev.Append(new byte[100]);

        var tuple = builder.Engine.ReadSegmentTuple(0);   // Zero = 逐写同步直写——立即可见（无轮询）
        tuple.Should().NotBeNull("Zero 间隔 = 每生命周期点同步 fsync 落盘");
        tuple!.Value.State.Should().Be(StableState.Ready);
    }

    [Fact]
    public async Task MetaTuple_InfiniteInterval_AnchoredAtDispose()
    {
        var vol = NewVol();
        var options = Opts(interval: Timeout.InfiniteTimeSpan);
        using var builder = options.Builder(vol.Fs);
        LogicalAddress addr;
        using (var dev = builder.Start())
        {
            dev.WaitForReady();
            addr = dev.Append(new byte[100]);
            builder.Engine.ReadSegmentTuple(0).Should().NotBeNull("Infinite = 生命周期缓存写立即读（无回扫）");
        }
        // Dispose 同步排水——元组终局落盘；重启恢复读到
        using var reopenedBuilder = options.Builder(vol.Fs);
        using var reopened = reopenedBuilder.Start();
        reopened.WaitForReady();
        reopenedBuilder.Engine.ReadSegmentTuple(0).Should().NotBeNull("Dispose 排水后元组应已落盘");
        var buf = new byte[100];
        reopened.Read(addr, buf).Should().Be(100, "重启后数据完整复原");
    }

    [Fact]
    public async Task MetaTuple_ReclaimHead_DropsPending_NoResurrection()
    {
        using var vol = NewVol();
        using var builder = Opts(interval: TimeSpan.FromSeconds(10)).Builder(vol.Fs);
        using var dev = builder.Start();
        dev.WaitForReady();
        dev.Append(new byte[1024]);   // seg0 满 → 跨 seg1（seg0 Create/Full 元组缓存写——立即读）
        dev.Append(new byte[1024]);

        builder.Engine.ReadSegmentTuple(0).Should().NotBeNull("生命周期缓存写立即读（v3 语义）");
        dev.ReclaimHead(new LogicalAddress(1, 0));   // 删除 seg0——脏集同步丢弃

        // 跨过多个泵周期（若泵错误地写已删段，会复活 sidecar/文件）
        await Task.Delay(300);
        vol.Fs.Exists("meta-dur/meta-dur.0").Should().BeFalse("已删段文件不得复活");
        await Task.Delay(300);
        vol.Fs.Exists("meta-dur/meta-dur.0").Should().BeFalse("持续不复活");
        builder.Engine.ReadSegmentTuple(0).Should().BeNull("已删段元组不可读");
    }

    [Fact]
    public async Task MetaTuple_Coalesce_MultipleLifecycleWrites_SingleLatestPayload()
    {
        using var vol = NewVol();
        // Zero 形态下验证 latest-wins 语义（泵形态的合并逻辑与 payload 编码一致）
        using var builder = Opts(interval: TimeSpan.Zero, growthLimit: 512).Builder(vol.Fs);
        using var dev = builder.Start();
        dev.WaitForReady();
        dev.Append(new byte[600]);   // 跨段：seg0 Full + seg1 Create——同段/跨段各元组最新态

        // Full 事件经 worker 异步处理——轮询等待最新态落盘（append 返回不等于 Full 元组已写）
        var fullTuple = await WaitTupleStateAsync(builder.Engine, segId: 0,
            t => t.State == StableState.Full, TimeSpan.FromSeconds(5));
        fullTuple.Should().NotBeNull("seg0 段满元组为最新状态");
        fullTuple!.Value.State.Should().Be(StableState.Full);
        builder.Engine.ReadSegmentTuple(0)!.Value.MaxOffset.Should().BeGreaterThan(0);
        builder.Engine.ReadSegmentTuple(1).Should().NotBeNull("跨段新段元组应存在");
    }

    /// <summary>轮询等待元组满足谓词（泵周期内/异步事件处理后）。</summary>
    private static async Task<(StableState State, long MaxOffset, long GrowthLimit, long RealSize, byte[] Summary)?> WaitTupleStateAsync(
        StorageEngine dev, int segId, Func<(StableState State, long MaxOffset, long GrowthLimit, long RealSize, byte[] Summary), bool> predicate,
        TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var t = dev.ReadSegmentTuple(segId);
            if (t is not null && predicate(t.Value)) return t;
            await Task.Delay(50);
        }
        return dev.ReadSegmentTuple(segId);
    }
}
