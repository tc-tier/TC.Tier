using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// SegmentTable 持久化验证（SegmentTable.Checkpoint.cs）。
/// 覆盖：LoadAddressTable（三段式恢复建段 + footer 水位）、SaveAddressTable（写头/段/尾）、
/// round-trip（Save 后 Load 回来状态一致）。
/// </summary>
public class SegmentTableCheckpointTests
{
    // ── 内存 reader/writer（测试 helper）──

    internal sealed record SegmentRecord(int SegId, long MinOffset, long GrowthLimit, long MaxOffset, StableState State);

    internal sealed class MemoryReader : IAddressTableReader
    {
        private readonly long _growthLimit;
        private readonly List<SegmentRecord> _segments;
        private readonly LogicalAddress? _committedTail, _allocatedTail;
        private int _idx;

        internal MemoryReader(long growthLimit, List<SegmentRecord> segments,
            LogicalAddress? committedTail = null, LogicalAddress? allocatedTail = null)
        {
            _growthLimit = growthLimit;
            _segments = segments;
            _committedTail = committedTail;
            _allocatedTail = allocatedTail;
        }

        public bool ReadHeader(out long growthLimit) { growthLimit = _growthLimit; return true; }

        public bool ReadSegment(out int segId,out SegmentSpec spec)
        {
            if (_idx >= _segments.Count)
            {
                segId = 0;
                spec = default;
                return false;
            }
            var s = _segments[_idx++];
            segId = s.SegId;
            spec = new SegmentSpec(s.MinOffset, s.GrowthLimit, s.MaxOffset, s.State);
            return true;
        }

        public bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail)
        {
            committedTail = _committedTail;
            allocatedTail = _allocatedTail;
            return true;
        }
    }

    private sealed class MemoryWriter : IAddressTableWriter
    {
        internal long GrowthLimit;
        internal int MinSegId, SegCount;
        internal readonly List<SegmentRecord> Segments = new();
        internal LogicalAddress CommittedTail, AllocatedTail;

        public void WriteHeader(int minSegId, int segCount, long growthLimit)
        { MinSegId = minSegId; SegCount = segCount; GrowthLimit = growthLimit; }

        public void WriteSegment(in int segId, in SegmentSpec spec)
            => Segments.Add(new SegmentRecord(segId, spec.MinOffset, spec.GrowthLimit, spec.MaxOffset, spec.StableState));

        public void WriteFooter(LogicalAddress committedTail, LogicalAddress allocatedTail)
        { CommittedTail = committedTail; AllocatedTail = allocatedTail; }
    }

    // ── LoadAddressTable ──

    [Fact]
    public void LoadAddressTable_RebuildsSegments_FromReader()
    {
        var segments = new List<SegmentRecord>
        {
            new(0, 0, 1000, 500, StableState.Ready),
            new(1, 0, 1000, 300, StableState.Ready),
        };
        var reader = new MemoryReader(1000, segments);
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));

        table.LoadAddressTable(reader);

        Assert.Equal(2, table.SegCount);
        Assert.Equal(1, table.MaxSegId);
        Assert.True(table.GetSegment(0).IsValid);
        Assert.True(table.GetSegment(1).IsValid);
    }

    [Fact]
    public void LoadAddressTable_FooterWatermarks_AppliedToTails()
    {
        var segments = new List<SegmentRecord>
        {
            new(0, 0, 1000, 500, StableState.Ready),
        };
        var reader = new MemoryReader(1000, segments,
            committedTail: new LogicalAddress(0, 300),
            allocatedTail: new LogicalAddress(0, 500));
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));

        table.LoadAddressTable(reader);

        Assert.Equal(new LogicalAddress(0, 300), table.CommittedTail);
        Assert.Equal(new LogicalAddress(0, 500), table.AllocatedTail);
    }

    [Fact]
    public void LoadAddressTable_EmptyFooter_KeepsDefaultTails()
    {
        // footer 给 null → 用默认（LoadAddressTable 里 LoadTail(... ?? LogicalAddress.Empty)）
        var segments = new List<SegmentRecord>
        {
            new(0, 0, 1000, 0, StableState.Ready),
        };
        var reader = new MemoryReader(1000, segments);   // 无 footer
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));

        table.LoadAddressTable(reader);

        Assert.Equal(new LogicalAddress(0, 0), table.AllocatedTail);
        Assert.Equal(new LogicalAddress(0, 0), table.CommittedTail);
    }

    [Fact]
    public void LoadAddressTable_EmptyDevice_SynthesizesSeg0Empty()
    {
        // 空设备（无段）→ 合成 seg0(Empty)（SegmentTable.Checkpoint.cs:46-61）
        var reader = new MemoryReader(1000, new List<SegmentRecord>());
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));

        table.LoadAddressTable(reader);

        Assert.Equal(1, table.SegCount);
        Assert.Equal(StableState.Empty, table.GetSegment(0).StableState);   // 空设备合成 Empty 段
    }

    [Fact]
    public void LoadAddressTable_PreservesSegmentState()
    {
        // 恢复时段的 StableState 应按 reader 给的值
        // ★ 旧夹具 seg1 (min=1000, max=500) 是矛盾数据（min > max）——被 SegmentScanEntry 强校验
        //   当场抓获（第 5 处位序/取值错误实例）。修正为合法值：min=0、max=500。
        var segments = new List<SegmentRecord>
        {
            new(0, 1000, 1000, 1000, StableState.Full),
            new(1, 0, 1000, 500, StableState.Ready),
        };
        var reader = new MemoryReader(1000, segments);
        var table = new SegmentTable(new SegmentTableSettings(1000, 0, 8));

        table.LoadAddressTable(reader);

        Assert.Equal(StableState.Full, table.GetSegment(0).StableState);
        Assert.Equal(StableState.Ready, table.GetSegment(1).StableState);
    }

    // ── SaveAddressTable ──

    /// <summary>
    /// 6.2 回归：MinOffset>0 的段（头部回收后）Save→Load 往返一致性。
    /// 之前 Save 误传 MinOffset 进 RealSize 槽位，Load 反推 maxOffset-realSize 导致
    /// MinOffset 被错误放大、RealSize 被压缩（头部回收段恢复后语义损坏）。
    /// </summary>
    [Fact]
    public void SaveLoad_RoundTrip_PreservesMinOffset_WhenMinOffsetPositive()
    {
        // 构造 MinOffset>0 的段：minOffset=700, maxOffset=1000（头部回收后）
        var segments = new List<SegmentRecord>
        {
            new(0, 700, 1000, 1000, StableState.Ready),   // (SegId, MinOffset, GrowthLimit, MaxOffset, State)
        };
        var reader1 = new MemoryReader(1000, segments);
        var table1 = new SegmentTable(new SegmentTableSettings(1000, 0, 8));
        table1.LoadAddressTable(reader1);

        // Load 端：直接读 minOffset=700（无需反推），RealSize=maxOffset-minOffset=300
        var seg1 = table1.GetSegment(0);
        Assert.Equal(700, seg1.MinOffset);
        Assert.Equal(300, seg1.RealSize);
        Assert.Equal(1000, seg1.MaxOffset);

        // Save 写出
        var writer = new MemoryWriter();
        table1.SaveAddressTable(writer);
        // ★ 核心断言：writer 收到的第 2 参（minOffset 槽位）= MinOffset(700)，直接存取无需反推
        Assert.Equal(700, writer.Segments[0].MinOffset);

        // 用 writer 数据 Load 回来
        var reader2 = new MemoryWriterBackedReader(writer);
        var table2 = new SegmentTable(new SegmentTableSettings(1000, 0, 8));
        table2.LoadAddressTable(reader2);

        // ★ 往返一致：恢复后 MinOffset 仍为 700
        var seg2 = table2.GetSegment(0);
        Assert.Equal(700, seg2.MinOffset);
        Assert.Equal(300, seg2.RealSize);
        Assert.Equal(1000, seg2.MaxOffset);
    }

    /// <summary>用 MemoryWriter 的数据当 reader（round-trip 桥接）。</summary>
    private sealed class MemoryWriterBackedReader : IAddressTableReader
    {
        private readonly MemoryWriter _w;
        private int _idx;
        internal MemoryWriterBackedReader(MemoryWriter w) => _w = w;

        public bool ReadHeader(out long growthLimit) { growthLimit = _w.GrowthLimit; return true; }

        public bool ReadSegment(out int segId,out SegmentSpec spec)
        {
            if (_idx >= _w.Segments.Count)
            { segId = 0; spec = default; return false; }
            var s = _w.Segments[_idx++];
            segId = s.SegId;
            spec = new SegmentSpec(s.MinOffset, s.GrowthLimit, s.MaxOffset, s.State);
            return true;
        }

        public bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail)
        {
            committedTail = _w.CommittedTail;
            allocatedTail = _w.AllocatedTail;
            return true;
        }
    }
}
// ═══════════════════════════════════════════════════════════════
//  VII-3 extent 级保真——ExtentSummaryCodec 编解码 + LoadAddressTable 安装
// ═══════════════════════════════════════════════════════════════

public static class ExtentSummaryCodecTests
{
    [Fact]
    public static void Codec_RoundTrip_PreservesRecords()
    {
        var records = new List<ExtentRecord>
        {
            new(0, 100, ExtentStateCode.Committed, sparse: false),
            new(100, 300, ExtentStateCode.Committed, sparse: true),
            new(300, 400, ExtentStateCode.Wasted),
            new(400, 500, ExtentStateCode.Aborted),
        };

        var payload = ExtentSummaryCodec.Encode(records);
        payload.Should().NotBeNull("4 条终态记录在容量内");
        var decoded = ExtentSummaryCodec.Decode(payload!);
        decoded.Should().NotBeNull();

        decoded!.Count.Should().Be(4);
        decoded[0].Start.Should().Be(0); decoded[0].End.Should().Be(100);
        decoded[0].State.Should().Be(ExtentStateCode.Committed);
        decoded[0].Sparse.Should().BeFalse();
        decoded[1].Sparse.Should().BeTrue("sparse 位往返保持");
        decoded[2].State.Should().Be(ExtentStateCode.Wasted);
        decoded[3].State.Should().Be(ExtentStateCode.Aborted);
        decoded[3].End.Should().Be(500);
    }

    [Fact]
    public static void Codec_OverCapacity_ReturnsNull()
    {
        var records = new List<ExtentRecord>();
        for (var i = 0; i <= ExtentSummaryCodec.MaxRecords; i++)
            records.Add(new ExtentRecord(i * 10, i * 10 + 10, ExtentStateCode.Committed));
        ExtentSummaryCodec.Encode(records).Should().BeNull("超容量降级粗粒度（不截断）");
    }

    [Fact]
    public static void Codec_Garbage_ReturnsNull()
    {
        ExtentSummaryCodec.Decode(new byte[] { 1, 2, 3 }).Should().BeNull();
        ExtentSummaryCodec.Decode(new byte[] { 0xE1, 0xFF, 0, 0, 0 }).Should().BeNull("版本不符");
    }
}

public class ExtentSummaryInstallTests
{
    private sealed class SummaryReader : IAddressTableReader, IExtentSummaryProvider
    {
        private bool _header;
        private bool _read;
        public IReadOnlyDictionary<int, byte[]>? ExtentSummaries { get; set; }

        public bool ReadHeader(out long growthLimit) { _header = true; growthLimit = 4096; return true; }

        public bool ReadSegment(out int segId, out SegmentSpec spec)
        {
            if (!_header || _read || ExtentSummaries is null) { segId = 0; spec = default; return false; }
            _read = true;   // 只吐一段
            segId = 0;
            spec = new SegmentSpec(0, 4096, 1000, StableState.Ready);
            return true;
        }

        public bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail)
        {
            committedTail = null; allocatedTail = null; return true;
        }
    }

    [Fact]
    public void LoadAddressTable_InstallsExtentSummary()
    {
        using var table = new SegmentTable(new SegmentTableSettings(4096, 0, 8, SpinMilliseconds: 2000));
        // 摘要：[0,400) Committed + [400,1000) Committed+sparse（洞布局）
        var payload = ExtentSummaryCodec.Encode(new List<ExtentRecord>
        {
            new(0, 400, ExtentStateCode.Committed, sparse: false),
            new(400, 1000, ExtentStateCode.Committed, sparse: true),
        })!;
        var reader = new SummaryReader { ExtentSummaries = new Dictionary<int, byte[]> { [0] = payload } };

        table.LoadAddressTable(reader);

        table.ExtentCount(0).Should().Be(2, "摘要应安装为 2 条区间记录");
        table.ExtentStateAt(0, 400).Should().Be(ExtentStateCode.Committed);
    }

    [Fact]
    public void LoadAddressTable_NoProvider_FallsBackCoarse()
    {
        // 无摘要 provider（内存引擎路径）——行为与旧粗粒度等价，不抛
        using var table = new SegmentTable(new SegmentTableSettings(4096, 0, 8, SpinMilliseconds: 2000));
        var reader = new SegmentTableCheckpointTests.MemoryReader(4096,
            new List<SegmentTableCheckpointTests.SegmentRecord> { new(0, 0, 4096, 1000, StableState.Ready) });
        FluentActions.Invoking(() => table.LoadAddressTable(reader)).Should().NotThrow();
    }
}
