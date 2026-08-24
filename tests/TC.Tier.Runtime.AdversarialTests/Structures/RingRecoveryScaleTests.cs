using System.Diagnostics;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 大规模恢复测试——验证四级恢复在 100MB/500MB/1GB 数据量下的正确性边界 + 耗时扩展性。
/// <para>★ 标 [Trait("Category","Scale")]：大规模测试耗时 + 磁盘占用大，CI 默认跳过，手动/Nightly 触发
///   （按 Trait Category=Scale 过滤）。编译宿主 = AdversarialTests（测试拆分裁定：大规格永不进单测）。</para>
/// <para>★ 复跑：dotnet test --filter "Category=Scale"（Adversarial 套件）。</para>
/// <para>★ 填补 RingRecoveryTests（仅 0.1KB 规模）的空白——验证恢复耗时是否恒定（Managed O(1)）
///   还是随数据量增长（Transport 自流嵌入 O(N) 倒扫）。</para>
/// <para>★ 恢复路径：空 hints（new RingRecoveryHints()），走真实 tier-2(meta) → tier-3 → tier-4 回退，
///   不用 hints 绕过——大规模恢复的真实路径才有验证意义。</para>
/// <para>★ 当前 API 形态：TestVolume 组合根 + StorageEngineOptions + DeleteOnClose=false 跨实例重开
///   （引擎自恢复）；本文件自包含（不依赖单测程序集 internal 工厂——Adversarial 只链接本文件）。</para>
/// </summary>
[Trait("Category", "Scale")]
[Collection("LargeScaleIO")]
public class RingRecoveryScaleTests
{
    private const int RingPageSize = AlignmentConst.Alignment1M;        // 1MB 页
    private const long RingMemorySize = AlignmentConst.Alignment256M;  // 256MB 内存池（触发驱逐+flush）
    private const long RingSegmentSize = AlignmentConst.Alignment2G;   // 2GB 段（单段容纳 1GB 数据）
    private const int PayloadSize = 4 * 1024;                       // 4KB payload
    private const int RecordHeaderOverhead = 32;                    // header + alignment

    /// <summary>批量写指定数据量（MB）的 4KB record，返回写入的 record 数。</summary>
    private static int RecordsForSize(int sizeMB)
        => (int)((sizeMB * 1024L * 1024) / (PayloadSize + RecordHeaderOverhead));

    /// <summary>大规模配置：2GB 单段 + 1MB 页 + 256MB 池 + 不预分配磁盘（大规模预分配慢）+ 跨实例保留文件。</summary>
    private static BlittableRingSettings MakeScaleSettings(MetaPolicyKind policy)
        => new(new StorageEngineOptions("ring.0", RingSegmentSize,
                enableSegmentation: true, preallocateFile: false, deleteOnClose: false))
        {
            PageSize = RingPageSize,
            MemorySize = RingMemorySize,
            MutableFraction = 0.5,
            Preallocate = false,
            MetaPolicyKind = policy,
        };

    /// <summary>构造 + Initialize（空 hints，走真实恢复路径）+ WaitForReady。</summary>
    private static BlittableRing<long> OpenScaleRing(TestVolume vol, BlittableRingSettings settings)
    {
        var ring = new BlittableRing<long>(settings, vol.Fs);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }

    /// <summary>
    /// ★ 大规模恢复正确性 + 耗时记录：三策略 × 三规模，验证恢复后 TailAddress 精确匹配。
    /// </summary>
    [Theory]
    [InlineData(100, MetaPolicyKind.Transport)]
    [InlineData(100, MetaPolicyKind.Managed)]
    [InlineData(100, MetaPolicyKind.Disabled)]
    [InlineData(500, MetaPolicyKind.Transport)]
    [InlineData(500, MetaPolicyKind.Managed)]
    [InlineData(500, MetaPolicyKind.Disabled)]
    [InlineData(1024, MetaPolicyKind.Transport)]
    [InlineData(1024, MetaPolicyKind.Managed)]
    [InlineData(1024, MetaPolicyKind.Disabled)]
    public void RecoveryScale_RestoresTailAddress_Correctly(int sizeMB, MetaPolicyKind policy)
    {
        using var vol = new TestVolume();
        var settings = MakeScaleSettings(policy);
        int recordCount = RecordsForSize(sizeMB);
        var payload = new byte[PayloadSize];
        payload.AsSpan().Fill(0xAB);
        LogicalAddress dataTail;

        // === 实例 1：批量写 + Prepare 落盘（数据 + meta）===
        var writeSw = Stopwatch.StartNew();
        using (var ring1 = OpenScaleRing(vol, settings))
        {
            for (int i = 0; i < recordCount; i++)
                ring1.Write(i, payload);
            dataTail = ring1.TailAddress;
            ring1.Prepare(seq: 1);   // 落盘数据 + meta
        }
        writeSw.Stop();

        // === 实例 2：重开即恢复（空 hints 走真实 tier-2/3/4，CAS 闸门保证幂等）===
        var recoverSw = Stopwatch.StartNew();
        using var ring2 = OpenScaleRing(vol, settings);
        recoverSw.Stop();

        // ★ 核心断言：
        // - Transport(自流嵌入)/Managed：走 tier-2(meta)，恢复后 TailAddress 精确匹配 dataTail
        // - Disabled：走 tier-3/tier-4（引擎尾/扫盘），恢复后可继续写入
        if (policy is MetaPolicyKind.Transport or MetaPolicyKind.Managed)
        {
            ring2.TailAddress.Should().Be(dataTail,
                $"{policy} 走 tier-2 meta 恢复 {sizeMB}MB 后 TailAddress 应精确匹配写入前的 dataTail");
        }
        else
        {
            // Disabled 无 meta——tier-3(引擎 CommittedTail)/tier-4(扫盘) 恢复。
            // 验证恢复后可继续写入（内存页池就绪）
            ring2.TailAddress.Should().NotBe(LogicalAddress.Empty, "Disabled tier-3/4 恢复后 TailAddress 应为有效值");
            ring2.Write(recordCount, payload);   // 恢复后可继续写（不抛即通过）
        }

        // 耗时记录（数据收集，非断言）
        Console.WriteLine(
            $"[Scale] policy={policy} size={sizeMB}MB records={recordCount} " +
            $"write={writeSw.Elapsed.TotalSeconds:F1}s recover={recoverSw.Elapsed.TotalSeconds:F3}s " +
            $"dataTail={dataTail} recoveredTail={ring2.TailAddress}");
    }

    /// <summary>
    /// ★ 循环恢复正确性：启动恢复→内存就绪→批量写入→重启→再恢复。
    /// 验证恢复后 Ring 可继续正常写入（内存页池就绪，水位可推进）。
    /// </summary>
    [Fact]
    public void RecoveryScale_CyclicRestart_WritesAfterRecovery()
    {
        using var vol = new TestVolume();
        var settings = MakeScaleSettings(MetaPolicyKind.Transport);
        int batch1Records = RecordsForSize(100);   // 第一批 100MB
        int batch2Records = RecordsForSize(50);    // 第二批 50MB
        var payload = new byte[PayloadSize];
        payload.AsSpan().Fill(0xCD);

        // === 周期 1：写 100MB → Prepare → Dispose ===
        LogicalAddress tailAfterBatch1;
        using (var ring1 = OpenScaleRing(vol, settings))
        {
            for (int i = 0; i < batch1Records; i++)
                ring1.Write(i, payload);
            tailAfterBatch1 = ring1.TailAddress;
            ring1.Prepare(seq: 1);
        }

        // === 周期 2：Recover → 再写 50MB → Prepare → Dispose ===
        LogicalAddress tailAfterBatch2;
        using (var ring2 = OpenScaleRing(vol, settings))
        {
            ring2.TailAddress.Should().Be(tailAfterBatch1,
                "第一次恢复后 TailAddress 应等于第一批写入末尾");
            // ★ 验证内存就绪：恢复后可继续写入
            for (int i = 0; i < batch2Records; i++)
                ring2.Write(i + 1_000_000, payload);
            tailAfterBatch2 = ring2.TailAddress;
            ring2.TailAddress.Should().BeGreaterThan(tailAfterBatch1,
                "第二批写入应推进 TailAddress");
            ring2.Prepare(seq: 2);
        }

        // === 周期 3：再 Recover → 验证包含两批数据 ===
        using var ring3 = OpenScaleRing(vol, settings);
        ring3.TailAddress.Should().Be(tailAfterBatch2,
            "第二次恢复后 TailAddress 应包含两批数据（精确到第二批末尾）");

        Console.WriteLine(
            $"[Scale-Cyclic] tailBatch1={tailAfterBatch1} tailBatch2={tailAfterBatch2} " +
            $"deltaBytes={vol.Fs.GetType().Name}（两批共 150MB）");
    }
}
