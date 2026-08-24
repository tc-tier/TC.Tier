using FluentAssertions;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 引擎级 CalculationAddress ± 契约测试（1:1 对应 StorageEngine.IO.cs 的 CalculationAddress）。
/// <para>钉住：名字叫"计算"就得加减都算——正=前进（跨段进位），负=回退（跨段借位），
/// 回退越过 MinAddress 返回 Invalid；段表层 Advance/Retreat 方向原语分立不受影响
/// （SegmentTableAddressingTests 各自钉住）。</para>
/// </summary>
public class EngineCalculationAddressTests
{
    /// <summary>建分段小段引擎（growthLimit=4096，Allocate 跨段建段），供跨段进位/借位用。</summary>
    private static (IStorageEngine engine, TestVolume vol) NewSegmentedEngine(int segments)
    {
        var vol = new TestVolume();
        var engine = new StorageEngineOptions("test.0", 4096, enableSegmentation: true, preallocateFile: false)
                .WithDeleteOnClose(true).Builder(vol.Fs).Start();
        engine.WaitForReady();
        for (int i = 0; i < segments; i++)
            engine.Allocate(4096); // 每段填满触发下一段
        return (engine, vol);
    }

    [Fact]
    public void Plus_Forward_CrossSegment()
    {
        var (engine, vol) = NewSegmentedEngine(3);
        try
        {
            // (0, 3000) + 2000 → seg0 剩 1096 + seg1 走 904 → (1, 904)
            engine.CalculationAddress(new LogicalAddress(0, 3000), 2000)
                .Should().Be(new LogicalAddress(1, 904));
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Minus_Backward_CrossSegmentBorrow()
    {
        var (engine, vol) = NewSegmentedEngine(3);
        try
        {
            // (1, 100) - 200 → seg1 剩 100 → 借 seg0 末尾 100 → (0, 3996)
            engine.CalculationAddress(new LogicalAddress(1, 100), -200)
                .Should().Be(new LogicalAddress(0, 3996));
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void PlusMinus_Roundtrip_ReturnsOrigin()
    {
        var (engine, vol) = NewSegmentedEngine(4);
        try
        {
            var origin = new LogicalAddress(1, 2000);
            var advanced = engine.CalculationAddress(origin, 8192); // 跨两段
            advanced.Should().NotBe(origin);
            engine.CalculationAddress(advanced, -8192).Should().Be(origin, "先 + 后 - 跨段往返可逆");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Minus_BelowMinAddress_ReturnsInvalid()
    {
        var (engine, vol) = NewSegmentedEngine(1);
        try
        {
            var r = engine.CalculationAddress(new LogicalAddress(0, 100), -200);
            r.Should().Be(LogicalAddress.Invalid, "回退越过 MinAddress 返回 Invalid（段表 Retreat 语义）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Zero_ReturnsStart()
    {
        var (engine, vol) = NewSegmentedEngine(1);
        try
        {
            var start = new LogicalAddress(0, 123);
            engine.CalculationAddress(start, 0).Should().Be(start);
        }
        finally { vol.Dispose(); }
    }
}
