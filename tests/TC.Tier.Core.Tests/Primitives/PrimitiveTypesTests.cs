using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// 基础类型枚举/常量验证测试——确保枚举值、常量值符合预期，防止重构时意外变更。
/// </summary>
public sealed class PrimitiveTypesTests
{
    // === RecordMagic（静态常量） ===

    [Fact]
    public void RecordMagic_Constants_AreNonZero()
    {
        RecordMagic.DeltaLogEntry.Should().NotBe(0u);
        RecordMagic.EntryLogEntry.Should().NotBe(0u);
        RecordMagic.StreamBlockHeader.Should().NotBe(0u);
        RecordMagic.StreamBlockFooter.Should().NotBe(0u);
        RecordMagic.IndexMirror.Should().NotBe(0u);
        RecordMagic.FixedBlock.Should().NotBe(0u);
        RecordMagic.PageMirror.Should().NotBe(0u);
        RecordMagic.StreamMeta.Should().NotBe(0u);
        RecordMagic.LogMeta.Should().NotBe(0u);
        RecordMagic.BlittableRing.Should().NotBe(0u);
        RecordMagic.OverflowRecord.Should().NotBe(0u);
        RecordMagic.LogPageFrame.Should().NotBe(0u);
        RecordMagic.RingPageFrame.Should().NotBe(0u);
        RecordMagic.BlobPageFrame.Should().NotBe(0u);
        RecordMagic.RingMeta.Should().NotBe(0u);
    }

    // === RecordFlags（静态常量 + 工具方法） ===

    [Fact]
    public void RecordFlags_GetCrcLen_ReturnsCorrectSizes()
    {
        RecordFlags.GetCrcLen(RecordFlags.FLAG_CRC_NONE).Should().Be(0);
        RecordFlags.GetCrcLen(RecordFlags.FLAG_CRC32).Should().Be(4);
        RecordFlags.GetCrcLen(RecordFlags.FLAG_CRC32C).Should().Be(4);
        RecordFlags.GetCrcLen(RecordFlags.FLAG_CRC64).Should().Be(8);
    }

    [Fact]
    public void RecordFlags_GetCrcLen_UnknownFlag_ReturnsZero()
    {
        // 0x10 & FLAG_CRC_MASK(0x03) = 0x00 = FLAG_CRC_NONE → 0
        RecordFlags.GetCrcLen(0x10).Should().Be(0);
    }

    [Fact]
    public void RecordFlags_MaskValues_AreCorrect()
    {
        RecordFlags.FLAG_CRC_MASK.Should().Be((ushort)0x03);
        RecordFlags.FLAG_PAYLOAD_MASK.Should().Be((ushort)0x18);
        RecordFlags.FLAG_META_MASK.Should().Be((ushort)0x60);
    }

    // === RecoveryPhase ===

    [Fact]
    public void RecoveryPhase_HasFourValues()
    {
        Enum.GetValues<RecoveryPhase>().Should().HaveCount(4);
    }

    // === RecoveryState ===

    [Fact]
    public void RecoveryState_Completed_IsCompletedTrue()
    {
        var state = new RecoveryState { Phase = RecoveryPhase.Completed };
        state.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void RecoveryState_NotStarted_IsCompletedFalse()
    {
        var state = new RecoveryState { Phase = RecoveryPhase.NotStarted };
        state.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void RecoveryState_Failed_IsCompletedFalse()
    {
        var state = new RecoveryState { Phase = RecoveryPhase.Failed };
        state.IsCompleted.Should().BeFalse();
    }

    // === SnapshotMode ===

    [Fact]
    public void SnapshotMode_HasTwoValues()
    {
        Enum.GetValues<SnapshotMode>().Should().HaveCount(2);
    }

    // === CompactStatus ===

    [Fact]
    public void CompactStatus_HasFiveValues()
    {
        Enum.GetValues<CompactStatus>().Should().HaveCount(5);
    }

    // === CompactResult（struct） ===

    [Fact]
    public void CompactResult_Default_PropertiesAreDefault()
    {
        var result = new CompactResult();
        result.NewLowWaterMark.Should().Be(LogicalAddress.Empty);
        result.NewHighWaterMark.Should().Be(LogicalAddress.Empty);
        result.MigrationMap.Should().BeNull();
    }

    [Fact]
    public void CompactResult_WithValues_PropertiesSet()
    {
        var map = new Dictionary<LogicalAddress, LogicalAddress?>
        {
            [new LogicalAddress(0, 0)] = new LogicalAddress(1, 0),
        };
        var result = new CompactResult
        {
            NewLowWaterMark = new LogicalAddress(0, 0),
            NewHighWaterMark = new LogicalAddress(1, 1024),
            MigrationMap = map,
        };

        result.NewLowWaterMark.Should().Be(new LogicalAddress(0, 0));
        result.NewHighWaterMark.Should().Be(new LogicalAddress(1, 1024));
        result.MigrationMap.Should().HaveCount(1);
    }

    // === ReadDirection ===

    [Fact]
    public void ReadDirection_HasTwoValues()
    {
        Enum.GetValues<ReadDirection>().Should().HaveCount(2);
    }

    // === MagicLocation（record struct——MagicLocator 粗定位结果） ===

    [Fact]
    public void MagicLocation_NotFound_HasInvalidAddresses()
    {
        MagicLocation.NotFound.Found.Should().BeFalse();
        MagicLocation.NotFound.MagicAddress.Should().Be(LogicalAddress.Invalid,
            "未命中 = Invalid（-1）——Empty 是合法 seg0@0 不能当'没有值'哨兵");
        MagicLocation.NotFound.PageAddress.Should().Be(LogicalAddress.Invalid);
    }

    [Fact]
    public void MagicLocation_Constructor_SetsValues()
    {
        var magic = new LogicalAddress(2, 0x380);
        var page = new LogicalAddress(2, 0x200);
        var loc = new MagicLocation(true, magic, page);
        loc.Found.Should().BeTrue();
        loc.MagicAddress.Should().Be(magic);
        loc.PageAddress.Should().Be(page);
    }
}
