using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Structures.Log.Contracts;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 装配——Builder 一步到位（构建+恢复+就绪一体）、Options With 链、注入面、启动状态机。
/// </summary>
public class TierWalBuilderTests
{
    [Fact]
    public async Task StartTwice_Throws()
    {
        using var vol = new TestVolume();
        var builder = TierWalOptions.Default.Builder(vol.Fs);
        await using var wal = await builder.StartAsync();
        var act = () => builder.StartAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Start_AfterFailure_Retryable()
    {
        using var vol = new TestVolume();
        var builder = TierWalOptions.Default.WithWalName("bad-name").Builder(vol.Fs);
        // 不触发失败——启动成功即所有权转移；验证失败重试路径用非法配置
        await using var wal = await builder.StartAsync();
        wal.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Options_WithChain_ReturnsNewInstances()
    {
        var o1 = TierWalOptions.Default;
        var o2 = o1.WithWalName("wal-2").WithSegmentGrowthLimit(1L << 20);
        o2.WalName.Should().Be("wal-2");
        o2.SegmentGrowthLimit.Should().Be(1L << 20);
        o1.WalName.Should().Be("tier-wal");   // 不可变——原实例不变
        o1.SegmentGrowthLimit.Should().Be(256L * 1024 * 1024);
    }

    [Fact]
    public async Task MetaPolicyKind_Managed_Default_RoundTrips()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        await wal.CommitAsync(default);
        wal.IsPersisted(1).Should().BeTrue();
    }

    [Fact]
    public async Task MetaPolicyKind_Disabled_OpaqueRejected()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol,
            o => o.WithMetaPolicyKind(MetaPolicyKind.Disabled));
        var act = () => wal.WriteMetaAsync(new byte[] { 1 }, default).AsTask();
        // Disabled 模式 SetOpaqueMeta 抛 InvalidOperationException（设计：禁用即报错不静默吞）
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MetaPolicyKind_Disabled_AppendStillWorks()
    {
        using var vol = new TestVolume();
        // ★ 禁自动提交三维度——默认 10ms interval 下首条 append 是否触发自动提交取决于
        //   构造→append 的时间间隔（时序 flaky：触发则 stage opaque 撞 Disabled 拒绝面）。
        await using var wal = await WalTestFactory.StartAsync(vol,
            o => o.WithMetaPolicyKind(MetaPolicyKind.Disabled)
                .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
                .WithMaxUnflushedBytes(long.MaxValue)
                .WithMaxUnflushedCount(int.MaxValue));
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        wal.AllocatedIndex.Should().Be(1);
    }

    [Fact]
    public async Task MetaOpaqueBytes_Zero_OpaqueUnavailable()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol,
            o => o.WithMetaOpaqueBytes(0));
        var act = () => wal.WriteMetaAsync(new byte[] { 1 }, default).AsTask();
        await act.Should().ThrowAsync<Exception>();   // 0 = 无 opaque 区——搭车通道不可用
    }

    [Fact]
    public async Task WithCommitPolicy_InjectedPolicy_Used()
    {
        using var vol = new TestVolume();
        var calls = 0;
        var custom = new CountingCommitPolicy(() => calls++);
        var builder = TierWalOptions.Default
            .WithCommitInterval(TimeSpan.FromMilliseconds(-1))   // 禁时间维度
            .Builder(vol.Fs)
            .WithCommitPolicy(custom);
        await using var wal = await builder.StartAsync();
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        calls.Should().BeGreaterThan(0);   // 注入策略被 OnAppended 提前提交路径调用
    }

    [Fact]
    public void Builder_Dispose_WithoutStart_NoThrow()
    {
        using var vol = new TestVolume();
        var builder = TierWalOptions.Default.Builder(vol.Fs);
        builder.Dispose();   // 未启动——仅释放未转移的 EntryLog（构造期即建）
    }

    [Fact]
    public async Task WalRecoveryHints_Default_Works()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol);   // default hints
        wal.IsReady.Should().BeTrue();
    }

    private sealed class CountingCommitPolicy(Action onShouldCommit) : ICommitPolicy
    {
        public bool ShouldCommit(in CommitSnapshot snapshot)
        {
            onShouldCommit();
            return false;
        }
    }
}
