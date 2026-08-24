using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Runtime.Tests;

/// <summary>
/// 引擎启动冒烟测试（放在非隔离的测试根目录——Storage/Structures 被排除编译）。
/// 验证"引擎 own SegmentTable + 段协调 worker + CPU 限流（CpuSampler 进 Resources）"
/// 这套新接线后，引擎能干净 Initialize → Ready → Append/Read → Dispose。全面恢复测试前的最小门槛。
/// </summary>
public class EngineStartupSmokeTests
{
    /// <summary>内存引擎：初始化→就绪→写→读→释放，全程无异常。</summary>
    [Fact]
    public void MemoryEngine_Startup_Append_Read_Dispose_Clean()
    {
        var options = new StorageEngineOptions("smoke", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var vol = new TestVolume();                  // 公共组合根：默认 mem，随 TC_TEST_FS_SPEC 平权切介质
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        Assert.Equal(RecoveryPhase.Completed, dev.RecoveryState.Phase);

        var addr = dev.Append(new byte[64]);  // AppendLease + EnsureCpuCapacity（factor=0 放行）
        var buf = new byte[64];
        var read = dev.Read(addr, buf);
        Assert.Equal(64, read);
        // using → Dispose：CpuSampler（Resources 统一释放）+ WorkerLoop + SegmentTable 干净退出，无 hang/抛
    }

    /// <summary>多段写冒烟：4KB 段写满多次 → 触发 OnSegmentFull + 预建下一段 → worker 建段链路通。</summary>
    [Fact]
    public void MemoryEngine_MultiSegment_Works()
    {
        var options = new StorageEngineOptions("smoke2", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var vol = new TestVolume();
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        for (int i = 0; i < 64; i++)
            dev.Append(new byte[64]);   // 64 × 64B = 4KB → 恰好填满 seg0，尾停驻 (0,4096)（区间统一）

        dev.Append(new byte[64]);       // 第 65 次真正跨进 seg1
        Assert.True(dev.AllocatedTail.SegId >= 1, "应已跨段（seg1+）——worker 建段链路工作");
    }
}
