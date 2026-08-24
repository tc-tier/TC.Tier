namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 一步到位工厂契约测试（CreateAndInitialize / CreateAndInitializeAsync——StorageEngine.Static.cs）。
/// <para>★ 契约：Create → Initialize（后台恢复）→ WaitForReady 三合一，返回时引擎即就绪可读写；
///   对已有数据的卷，恢复在工厂内部完成（旧数据可见）。</para>
/// </summary>
public sealed class StorageEngineCreateAndInitializeTests : StorageEngineTestBase, IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static StorageEngineOptions Options(string name)
        => new StorageEngineOptions(name, segmentGrowthLimit: 8 * 1024).WithPreallocateFile(false);

    [Fact]
    public void CreateAndInitialize_ReturnsReadyEngine_UsableImmediately()
    {
        var vol = NewVol();
        using var dev = Options("cai-sync").Builder(vol.Fs).Start();

        dev.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed, "工厂内部已等恢复完成");
        dev.IsReady.Should().BeTrue();

        var addr = dev.Append(MakePattern(512, 0x31));
        var buf = new byte[512];
        dev.Read(addr, buf).Should().Be(512);
        buf.Should().Equal(MakePattern(512, 0x31));
    }

    [Fact]
    public async Task CreateAndInitializeAsync_ReturnsReadyEngine_UsableImmediately()
    {
        var vol = NewVol();
        await using var dev = await Options("cai-async").Builder(vol.Fs).StartAsync();

        dev.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);
        dev.IsReady.Should().BeTrue();

        var addr = dev.Append(MakePattern(256, 0x32));
        var buf = new byte[256];
        (await dev.ReadAsync(addr, buf, CancellationToken.None)).Should().Be(256);
        buf.Should().Equal(MakePattern(256, 0x32));
    }

    [Fact]
    public void CreateAndInitialize_OnExistingVolume_RecoversPreviousData()
    {
        var vol = NewVol();
        var options = Options("cai-recovery");

        LogicalAddress addr;
        byte[] data = MakeSequential(1024);
        using (var first = options.Builder(vol.Fs).Start())
        {
            addr = first.Append(data);
            first.Flush();   // 显式落盘（Hints=None 模式的持久化点）
        }

        // 第二个引擎同卷构造——恢复在工厂内部完成，返回即可读
        using var second = options.Builder(vol.Fs).Start();
        second.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);

        var buf = new byte[1024];
        second.Read(addr, buf).Should().Be(1024, "恢复后旧数据可见");
        buf.Should().Equal(data);

        // 且可在恢复出的地址空间上继续追加
        var next = second.Append(MakePattern(128, 0x33));
        second.GetDistance(addr, next).Should().BeGreaterThan(0, "新追加在恢复数据的之后");
    }

    [Fact]
    public void CreateAndInitialize_EngineHints_PassThroughRecovery()
    {
        var vol = NewVol();
        // hints 只带恢复水位（双尾修正）——空 hints 与显式 default 等价可用
        using var dev = Options("cai-hints").Builder(vol.Fs).Start(hints: new EngineRecoveryHints());
        dev.IsReady.Should().BeTrue();
        var a = dev.Append(MakePattern(64, 0x34));
        dev.GetDistance(a, dev.CommittedTail).Should().BeGreaterThanOrEqualTo(64, "追加后已写水位越过记录");
    }
}
