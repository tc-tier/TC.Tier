using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// DefaultCompactor 错误深水区契约测试（台账 L4，2026-08-21）——promote 瞬态/耗尽、
/// marker IO 失败、搬迁（拷贝）失败的确定性注入。
/// <para>★ 注入面：FaultInjectingFileSystem（fs 级 + 句柄级 op 匹配；路径 pattern = 精确串或
///   <c>"*"</c> 全匹配——非 glob）——按精确路径区分阶段：</para>
/// <para> - <c>fault/fault.{segId}.compact</c>：临时段（拷贝 Write / promote Move 的源）</para>
/// <para> - <c>fault/fault.compact.marker.tmp</c>：commit marker 临时文件（Phase 2 写/换名）</para>
/// <para>★ 契约锚点：Commit 前失败（拷贝/marker）→ 回滚，段表未变，旧数据原地址完整可读；
///   promote 瞬态失败 → 退避重试吸收；promote 耗尽（Commit 后失败）→ op.Failed 可见 +
///   引擎活性保持 + 重开经 marker 恢复回到旧布局（数据不丢，Compact 白做）。</para>
/// </summary>
public sealed class CompactorFaultInjectionTests : StorageEngineTestBase
{
    private const string EngineName = "fault";

    private static (IStorageEngine Dev, FaultInjectingFileSystem Fi) NewEngine(
        FaultInjectingFileSystem? fi = null)
    {
        fi ??= new FaultInjectingFileSystem(TierFs.New("memory:"));
        var options = new StorageEngineOptions(EngineName, segmentGrowthLimit: 1024).WithPreallocateFile(false);
        var dev = options.Builder(fi).Start();
        dev.WaitForReady();
        return (dev, fi);
    }

    /// <summary>写跨 3 段稠密数据（6×512B / 1KB 段）并记录（源地址 → 数据）。</summary>
    private static List<(LogicalAddress Addr, byte[] Data)> Seed(IStorageEngine dev)
    {
        var records = new List<(LogicalAddress, byte[])>();
        for (var i = 0; i < 6; i++)
            records.Add((dev.Append(MakePattern(512, (byte)(0x30 + i))), MakePattern(512, (byte)(0x30 + i))));
        records.Count.Should().Be(6);
        dev.AllocatedTail.SegId.Should().BeGreaterThanOrEqualTo(2, "跨 3 段");
        return records;
    }

    private static void AssertIntact(IStorageEngine dev, List<(LogicalAddress Addr, byte[] Data)> records)
    {
        var buf = new byte[512];
        foreach (var (addr, data) in records)
        {
            dev.Read(addr, buf).Should().Be(512, $"addr={addr} 完整可读");
            buf.Should().Equal(data, $"addr={addr} 逐字节一致");
        }
    }

    [Fact]
    public async Task Compact_CopyWriteFault_RollsBack_DataIntact_RetrySucceeds()
    {
        var (dev, fi) = NewEngine();
        using var _ = dev;
        var records = Seed(dev);

        // 拷贝阶段：写临时段（*.compact）第 2 次注入失败（第 1 次成功 = 首 chunk 拷贝完成）
        fi.AddRule("*", "Write", IOError.IOFailure, failAtCallIndex: 2);

        var op = dev.StartCompact();
        var wait = async () => await op.WaitAsync(CancellationToken.None);
        await wait.Should().ThrowAsync<Exception>("搬迁写临时段失败 → op.Failed");

        // 回滚契约：段表未变——旧数据原地址完整可读；无 .compact 残留（DeleteAllTemps）
        AssertIntact(dev, records);
        fi.EnumerateFiles(EngineName, "*.compact").Should().BeEmpty("失败路径清理临时段");

        // 重试（瞬态故障排除后）成功
        fi.ClearRules();
        var result = await dev.StartCompact().WaitAsync();
        result.MigrationMap.Should().NotBeEmpty("重试 Compact 成功搬迁");
        var buf = new byte[512];
        foreach (var (addr, data) in records)
        {
            if (result.MigrationMap.TryGetValue(addr, out var moved) && moved is { } m)
            {
                dev.Read(m, buf).Should().Be(512);
                buf.Should().Equal(data, "搬迁后数据完整");
            }
        }
    }

    [Fact]
    public async Task Compact_MarkerWriteFault_RollsBack_DataIntact()
    {
        var (dev, fi) = NewEngine();
        using var _ = dev;
        var records = Seed(dev);

        // Phase 2 前置：marker 临时文件写失败（路径收窄——拷贝写不受影响）
        fi.AddRule($"{EngineName}/{EngineName}.compact.marker.tmp", "Write", IOError.DiskFull, failAtCallIndex: 1);

        var op = dev.StartCompact();
        var wait = async () => await op.WaitAsync(CancellationToken.None);
        await wait.Should().ThrowAsync<Exception>("marker 写失败 → op.Failed（lease 未提交，全量回滚）");

        // 回滚契约：旧数据原地址完整；marker 不残留
        AssertIntact(dev, records);
        fi.EnumerateFiles(EngineName, "*marker*").Should().BeEmpty("失败路径无 marker 残留");

        fi.ClearRules();
        await dev.StartCompact().WaitAsync();
    }

    [Fact]
    public async Task Compact_PromoteRenameTransient_RetriesToSuccess_DataIntact()
    {
        var (dev, fi) = NewEngine();
        using var _ = dev;
        var records = Seed(dev);

        // promote：rename（Move *.compact → 段文件）首两次瞬态失败 → 退避重试吸收（attempt<60 × 100ms）
        fi.AddRule($"{EngineName}/{EngineName}.0.compact", "Move", IOError.AccessDenied, failAtCallIndex: 1);
        fi.AddRule($"{EngineName}/{EngineName}.0.compact", "Move", IOError.AccessDenied, failAtCallIndex: 2);

        var result = await dev.StartCompact().WaitAsync();

        // 瞬态失败被重试吸收：Compact 正常完成 + 搬迁后数据完整
        result.MigrationMap.Should().NotBeEmpty();
        var buf = new byte[512];
        foreach (var (addr, data) in records)
        {
            if (result.MigrationMap.TryGetValue(addr, out var moved) && moved is { } m)
            {
                dev.Read(m, buf).Should().Be(512);
                buf.Should().Equal(data, "重试 promote 后数据完整");
            }
        }
        fi.EnumerateFiles(EngineName, "*.compact").Should().BeEmpty("成功后无临时段残留");
    }

    [Fact]
    public async Task Compact_PromoteRenameExhaustion_Fails_DataSurvivesAfterReopen()
    {
        var (dev, fi) = NewEngine();
        var records = Seed(dev);

        // promote：rename 永久失败（概率 1.0）→ 60×100ms 退避耗尽 → 抛 → op.Failed
        fi.AddRule($"{EngineName}/{EngineName}.0.compact", "Move", IOError.AccessDenied, probability: 1.0);

        var op = dev.StartCompact();
        var wait = async () => await op.WaitAsync(CancellationToken.None);
        await wait.Should().ThrowAsync<Exception>("rename 重试耗尽（60×100ms）→ op.Failed");
        fi.ClearRules();

        // ★ 同实例活性钉住：Commit 后失败——旧地址可能已 Invalid（迁移语义）或可读，引擎不得挂死
        var probe = new byte[512];
        foreach (var (addr, data) in records)
        {
            try
            {
                if (dev.Read(addr, probe) == 512)
                    probe.Should().Equal(data, "可读旧地址 = 源数据");
            }
            catch (PartitionInvalidException)
            { /* 已迁移旧地址失效合法 */ }
        }

        dev.Dispose();

        // ★ 重开（同卷）：marker 恢复——临时段已被失败路径清理 = "当作已 promote"，
        //   段表按物理文件（旧布局）重建——数据不丢（Compact 白做），引擎可用。
        var (reopened, _) = NewEngine(fi);
        using var __ = reopened;
        AssertIntact(reopened, records);
        reopened.Append(MakePattern(512, 0x99)).Should().BeGreaterThan(reopened.MinAddress, "重开后引擎继续可用");
    }
}
