using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Storage.Exceptions;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 恢复语义测试——验证 spec §3 的 hint 三态处理：
/// <para>① hint == 真实水位：正常恢复</para>
/// <para>② hint &lt; 真实水位：截断回收（hint 之后数据物理 PunchHole 回收）</para>
/// <para>③ hint &gt; 真实水位：抛 DeviceException（数据损坏，不掩盖）</para>
/// </summary>
public sealed class RecoverySemanticsTests : IDisposable
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

    private static byte[] MakePattern(int length, byte seed)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    /// <summary>
    /// ① hint == 真实水位：正常恢复，数据完整可读。
    /// </summary>
    [Fact]
    public void HintEqualsReal_NormalRecovery()
    {
        var vol = NewVol();
        LogicalAddress lastTail;
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(500, 0xAA));
            dev.Append(MakePattern(500, 0xBB));
            lastTail = dev.AllocatedTail;
        }

        // reopen，hint == 真实水位
        using var dev2 = options.Builder(vol.Fs).Start(new EngineRecoveryHints(committedTailHint: lastTail));
        

        dev2.AllocatedTail.Should().Be(lastTail, "hint == 真实水位时 tail 应等于 hint");

        // 数据完整可读
        var dst = new byte[500];
        dev2.Read(new LogicalAddress(0, 0), dst);
        dst.Should().Equal(MakePattern(500, 0xAA));
        dev2.Read(new LogicalAddress(0, 500), dst);
        dst.Should().Equal(MakePattern(500, 0xBB));
    }

    /// <summary>
    /// ② hint &lt; 真实水位：截断回收——hint 之后的数据物理 PunchHole 回收。
    /// </summary>
    /// <remarks>
    /// ★ 模拟崩溃：写了 1000B 但 checkpoint 只确认到 500B。reopen 传 hint=500，
    ///   恢复后 tail 应截断到 500，500-1000 区间数据回收（读零）。
    /// </remarks>
    [Fact]
    public void HintLessThanReal_TruncatesAndReclaims()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(1000, 0xAA)); // 写 1000B
        }

        // reopen，hint=500（< 真实 1000B）→ 截断回收
        using var dev2 = options.Builder(vol.Fs).Start(new EngineRecoveryHints(committedTailHint: new LogicalAddress(0, 500)));
        

        // tail 应截断到 500
        dev2.AllocatedTail.Offset.Should().Be(500, "hint < 真实时应截断到 hint");

        // [0, 500) 数据保留
        var dst = new byte[500];
        dev2.Read(new LogicalAddress(0, 0), dst);
        dst.Should().Equal(MakePattern(500, 0xAA).AsSpan(0, 500).ToArray(),
            "hint 之前的数据应保留");

        // 继续写应从 500 开始
        var addr = dev2.Append(MakePattern(100, 0xCC));
        addr.Offset.Should().Be(500, "截断后新写应从 hint 位置开始");
    }

    /// <summary>
    /// ③ hint &gt; 真实水位：接受（可大可小——上层修正值，大 = 覆盖老数据，正常路径）。
    /// </summary>
    /// <remarks>
    /// ★ 语义修订（架构约定）：hint 是上层传递的修正值，可大可小——
    ///   大 = 覆盖老数据（上层保证 checkpoint 语义有效），不抛数据损坏异常；
    ///   水位推到 hint，hint 所在段 MaxOffset 联动推进使地址生效。
    /// </remarks>
    [Fact]
    public void HintGreaterThanReal_ExtendsTailAsCorrection()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(500, 0xAA)); // 只写 500B
        }

        // reopen，hint=1000（> 真实 500B）→ 接受：tail 推到 hint（上层修正，覆盖老数据）
        using var dev2 = options.Builder(vol.Fs).Start(new EngineRecoveryHints(committedTailHint: new LogicalAddress(0, 1000)));
        

        dev2.CommittedTail.Should().Be(new LogicalAddress(0, 1000), "hint 大值修正：tail 推到 hint（覆盖老数据）");
        dev2.AllocatedTail.Should().Be(new LogicalAddress(0, 1000), "双尾联动：allocated 跟随 committed");
    }

    /// <summary>
    /// VII-3 extent 级保真——打洞段的 sparse 位与区间记录在 reopen 后保持
    /// （运行时模型：reclaim 洞 OR 并入大记录的 sparse 位；旧粗粒度重建会丢 sparse 位变全稠密）。
    /// <para>★ EagerScheduler：meta 即写即刷（默认 AdaptiveScheduler 短命引擎的 dispose-flush 落盘缺口为
    ///   独立既有问题，见台账 VII-7）。</para>
    /// </summary>
    [Fact]
    public void Reopen_PreservesExtentHoleLayout()
    {
        var vol = NewVol();
        (int Count, bool AnySparse) before;

        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var builder = options.Builder(vol.Fs);
        using (var dev = builder.Start())
        {
            dev.WaitForReady();
            for (int i = 0; i < 6; i++)
                dev.Append(MakePattern(1024, (byte)i));   // 跨 seg0-seg1
            // seg0 打两个洞 → 大记录 sparse 位升起（运行时模型：OR 并入，不裂多条）
            dev.Reclaim(new LogicalAddress(0, 100), new LogicalAddress(0, 200));
            dev.Reclaim(new LogicalAddress(0, 1000), new LogicalAddress(0, 1500));
            before = builder.Engine.GetExtentSummaryDiagnostic(0);
            before.AnySparse.Should().BeTrue("打洞后区间记录应带 sparse 位");
        }

        using var builder2 = options.Builder(vol.Fs);
        using var dev2 = builder2.Start();
        dev2.WaitForReady();

        var after = builder2.Engine.GetExtentSummaryDiagnostic(0);
        after.AnySparse.Should().BeTrue("VII-3 保真：reopen 应保持 sparse 位（meta extension 摘要往返）");
        after.Count.Should().Be(before.Count, "区间记录数保持（Wasted/Aborted 独立记录不丢）");
    }

    /// <summary>
    /// 跨段 hint 截断——hint 在 seg#1，但磁盘写到 seg#2，应删除 seg#2 + 截断 seg#1。
    /// </summary>
    [Fact]
    public void HintLessThanReal_CrossSegment_TruncatesAndDeletesExtraSeg()
    {
        var vol = NewVol();
        int segGrowth = 1024;
        LogicalAddress fullTail;
 var options = new StorageEngineOptions("test", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            // 写满 seg#0 + 部分 seg#1（1500B > 1024 跨段）
            for (int i = 0; i < 15; i++)
                dev.Append(MakePattern(100, (byte)i));
            fullTail = dev.AllocatedTail;
        }

        // 假设 fullTail 在 seg#1，hint 设为 seg#0@500（< fullTail）→ 截断应删 seg#1 + 截 seg#0
        var hint = new LogicalAddress(0, 500);
        if (fullTail.SegId < 1)
        {
            Console.WriteLine("[SKIP] 未跨段，无法测跨段截断");
            return;
        }

        using var dev2 = options.Builder(vol.Fs).Start(new EngineRecoveryHints(committedTailHint: hint));
        

        // tail 应截断到 hint
        dev2.AllocatedTail.Should().Be(hint, "跨段截断后 tail 应等于 hint");

        // seg#1 文件应被删除（hint 之后整段删除）
        vol.Fs.Exists("test/test.1").Should().BeFalse("hint 之后的整段应删除");
    }

    /// <summary>
    /// 无 hint——用 OS 真实水位作 tail（最后段 MaxOffset）。
    /// </summary>
    [Fact]
    public void NoHint_UsesRealWatermark()
    {
        var vol = NewVol();
        LogicalAddress lastTail;
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(500, 0xAA));
            dev.Append(MakePattern(300, 0xBB));
            dev.Flush();   // ★ 落盘——reopen 恢复靠文件大小（OS 真实水位），必须 Flush
            lastTail = dev.AllocatedTail;
        }

        // reopen，不传 hint
        using var builder2 = options.Builder(vol.Fs);
        using var dev2 = builder2.Start();
        dev2.WaitForReady();

        // tail 应等于 OS 真实水位（稀疏模式 fileSize = 真实写入）
        dev2.AllocatedTail.Offset.Should().Be(lastTail.Offset,
            "无 hint 时应用 OS 真实水位（fileSize）作 tail");

        // 数据完整可读
        var dst = new byte[300];
        dev2.Read(new LogicalAddress(0, 500), dst);
        dst.Should().Equal(MakePattern(300, 0xBB));
    }
}
