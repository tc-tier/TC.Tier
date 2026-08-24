using System.Buffers.Binary;
using TC.Tier.Contracts.Meta;
using TC.Tier.Runtime.Meta;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Runtime.Tests.Meta;

/// <summary>
/// Meta 三策略（Managed/Transport/Disabled）统一契约矩阵测试。
/// <para>★ 契约依据：IMetaPolicy / IMetaLayout / IMetaTransport（Contracts.Meta）——
///   统一布局 [Header 纯规范][Payload 结构化水位 + opaque][Footer Crc32C]；
///   未 Load 读返回 null/Empty；Commit 后同实例可读；重新 Load 全量重置；Dispose 幂等。</para>
/// <para>★ 测试件自包含：TestMetaLayout（手写 12B/24B+64B 布局）+ 单槽传输桩 + 追加流传输桩——
///   不依赖任何具体结构（Ring/Log）实现。</para>
/// </summary>
public class MetaPolicyTests
{

    // ════════════════════════════════════════════════════════════
    //  通用契约（三策略同构验证）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Managed_Roundtrip_CommitThenReadable()
        => AssertRoundtrip(NewManaged());

    [Fact]
    public void Transport_SingleSlot_Roundtrip()
        => AssertRoundtrip(NewTransport(new SingleSlotTransport()));

    [Fact]
    public void Transport_Stream_Roundtrip()
        => AssertRoundtrip(NewTransport(new StreamTransport()));

    private static void AssertRoundtrip(IMetaPolicy<TestMetaHeader, TestMetaPayload> policy)
    {
        using (policy)
        {
            policy.WriteHeader(TestMetaLayout.DefaultHeader());
            policy.WritePayload(NewPayload());
            policy.WritePayload(Opaque(7));
            policy.Commit();

            // ★ 契约：Commit 成功后同实例可读（_loaded 置位）
            Assert.Equal(TestMetaLayout.MagicValue, policy.ReadHeader()!.Value.Magic);
            Assert.Equal(NewPayload(), policy.ReadMetaPayload());
            Assert.True(policy.ReadPayload().SequenceEqual(Opaque(7)));
        }
    }

    [Fact]
    public void All_LoadOnEmpty_ReturnsFalse()
    {
        using var managed = NewManaged();
        using var transport = NewTransport(new SingleSlotTransport());
        using var disabled = new DisabledMetaPolicy<TestMetaHeader, TestMetaPayload>();

        Assert.False(managed.Load());
        Assert.False(transport.Load());
        Assert.False(disabled.Load());
    }

    [Fact]
    public void All_ReadWithoutLoad_ReturnsNullOrEmpty()
    {
        using var managed = NewManaged();
        using var transport = NewTransport(new SingleSlotTransport());

        IMetaPolicy<TestMetaHeader, TestMetaPayload>[] policies = { managed, transport };
        foreach (var p in policies)
        {
            Assert.Null(p.ReadHeader());
            Assert.Null(p.ReadMetaPayload());
            // ★ 契约：未 Load 读 opaque 返回 Empty
            Assert.True(p.ReadPayload().IsEmpty);
        }
    }

    [Fact]
    public void All_OpaqueOverCapacity_Throws()
    {
        using var managed = NewManaged();
        using var transport = NewTransport(new SingleSlotTransport());

        var oversized = new byte[TestMetaLayout.OpaqueCapacityValue + 1];
        Assert.Throws<ArgumentException>(() => managed.WritePayload(oversized));
        Assert.Throws<ArgumentException>(() => transport.WritePayload(oversized));
    }

    [Fact]
    public void All_DisposeTwice_DoesNotThrow()
    {
        var managed = NewManaged();
        managed.Dispose();
        managed.Dispose();

        var transport = NewTransport(new SingleSlotTransport());
        transport.Dispose();
        transport.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  Managed 专属（独立引擎 + 固定块 + Magic/CRC 校验）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Managed_BlockPersistsInEngine_NewPolicyInstanceLoads()
    {
        var engine = NewMemoryEngine();
        using (engine)
        {
            using (var policy = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine))
            {
                policy.WriteHeader(TestMetaLayout.DefaultHeader());
                policy.WritePayload(NewPayload());
                policy.WritePayload(Opaque(7));
                policy.Commit();
            }

            // 同引擎上的新策略实例 = 模拟重开：读回全部三段
            using (var reloaded = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine))
            {
                Assert.True(reloaded.Load());
                Assert.Equal(TestMetaLayout.MagicValue, reloaded.ReadHeader()!.Value.Magic);
                Assert.Equal(NewPayload(), reloaded.ReadMetaPayload());
                Assert.True(reloaded.ReadPayload().SequenceEqual(Opaque(7)));
            }
        }
    }

    [Fact]
    public void Managed_CorruptedCrc_LoadReturnsFalse()
    {
        var engine = NewMemoryEngine();
        using (engine)
        {
            CommitDefaultBlock(engine);

            var blockSize = (Layout.HeaderSize + Layout.PayloadSize + 4).AlignUp(4096);
            var buf = new byte[blockSize];
            engine.Read(LogicalAddress.Empty, buf);
            buf[Layout.HeaderSize + 8] ^= 0xFF;
            engine.Write(LogicalAddress.Empty, buf);

            using var victim = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine);
            Assert.False(victim.Load());
        }
    }

    [Fact]
    public void Managed_CorruptedMagic_LoadReturnsFalse()
    {
        var engine = NewMemoryEngine();
        using (engine)
        {
            CommitDefaultBlock(engine);

            var blockSize = (Layout.HeaderSize + Layout.PayloadSize + 4).AlignUp(4096);
            var buf = new byte[blockSize];
            engine.Read(LogicalAddress.Empty, buf);
            BinaryPrimitives.WriteUInt32LittleEndian(buf, 0xDEADBEEF);
            engine.Write(LogicalAddress.Empty, buf);

            using var victim = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine);
            Assert.False(victim.Load());
        }
    }

    [Fact]
    public void Managed_WriteHeaderValidate_WrongVersion_Throws()
    {
        var engine = NewMemoryEngine();
        using (engine)
        using (var policy = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine))
        {
            var header = TestMetaLayout.DefaultHeader();
            header.Version = (ushort)(TestMetaLayout.CurrentVersionValue + 1);
            Assert.Throws<InvalidOperationException>(() => policy.WriteHeader(header));
        }
    }

    [Fact]
    public async Task Managed_AsyncCommit_ThenNewInstanceLoads()
    {
        var engine = NewMemoryEngine();
        using (engine)
        {
            using (var policy = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine))
            {
                policy.WriteHeader(TestMetaLayout.DefaultHeader());
                policy.WritePayload(NewPayload());
                policy.WritePayload(Opaque(3));
                await policy.CommitAsync(CancellationToken.None);
            }

            using (var reloaded = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine))
            {
                Assert.True(reloaded.Load());
                Assert.Equal(NewPayload(), reloaded.ReadMetaPayload());
                Assert.True(reloaded.ReadPayload().SequenceEqual(Opaque(3)));
            }
        }
    }

    private void CommitDefaultBlock(IStorageEngine engine)
    {
        using var policy = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(Layout, engine);
        policy.WriteHeader(TestMetaLayout.DefaultHeader());
        policy.WritePayload(NewPayload());
        policy.Commit();
    }

    // ════════════════════════════════════════════════════════════
    //  Transport 专属（统一传输：单槽 / 追加流两种放置）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Transport_WriteHeader_UsesProvidedHeader()
    {
        var sink = new SingleSlotTransport();
        using var policy = NewTransport(sink);

        var header = TestMetaLayout.DefaultHeader();
        header.Flags = 0x1234;
        policy.WriteHeader(header);
        policy.WritePayload(NewPayload());
        policy.Commit();

        var written = sink.Stored!;
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(6)));
    }

    [Fact]
    public void Transport_CommitWritesExactLengthBlock_NoPadding()
    {
        var sink = new SingleSlotTransport();
        using (var policy = NewTransport(sink))
        {
            policy.WriteHeader(TestMetaLayout.DefaultHeader());
            policy.WritePayload(NewPayload());   // 无 opaque——块 = header + struct + footer
            policy.Commit();
        }

        var expected = Layout.HeaderSize + TestMetaLayout.StructPayloadValue + 4;
        Assert.Equal(expected, sink.Stored!.Length);
    }

    [Fact]
    public void Transport_RoundtripThroughSingleSlot()
    {
        var sink = new SingleSlotTransport();
        using (var policy = NewTransport(sink))
        {
            policy.WriteHeader(TestMetaLayout.DefaultHeader());
            policy.WritePayload(NewPayload());
            policy.WritePayload(Opaque(5));
            policy.Commit();
        }

        using var reloaded = NewTransport(sink);
        Assert.True(reloaded.Load());
        Assert.Equal(NewPayload(), reloaded.ReadMetaPayload());
        Assert.True(reloaded.ReadPayload().SequenceEqual(Opaque(5)));
    }

    [Fact]
    public void Transport_StreamTakesLastBlock()
    {
        var stream = new StreamTransport();
        using (var first = NewTransport(stream))
        {
            first.WriteHeader(TestMetaLayout.DefaultHeader());
            first.WritePayload(NewPayload());
            first.Commit();
        }

        using (var second = NewTransport(stream))
        {
            second.WriteHeader(TestMetaLayout.DefaultHeader());
            var newer = NewPayload();
            newer.RecordCount = 999;
            second.WritePayload(newer);
            second.Commit();
        }

        using var reloaded = NewTransport(stream);
        Assert.True(reloaded.Load());
        Assert.Equal(999, reloaded.ReadMetaPayload()!.Value.RecordCount);
    }

    [Fact]
    public void Transport_ReloadWithoutOpaque_ClearsStaleOpaque()
    {
        // ★ 契约回归：重新 Load 全量重置——第一块带 opaque，第二块不带，旧 opaque 不得残留
        var stream = new StreamTransport();
        using (var first = NewTransport(stream))
        {
            first.WriteHeader(TestMetaLayout.DefaultHeader());
            first.WritePayload(NewPayload());
            first.WritePayload(Opaque(11));
            first.Commit();
        }
        using (var second = NewTransport(stream))
        {
            second.WriteHeader(TestMetaLayout.DefaultHeader());
            second.WritePayload(NewPayload());
            second.Commit();
        }

        using var reloaded = NewTransport(stream);
        Assert.True(reloaded.Load());
        Assert.Equal(NewPayload(), reloaded.ReadMetaPayload());
        Assert.True(reloaded.ReadPayload().IsEmpty);
    }

    [Fact]
    public void Transport_ReadPayloadBeforeLoad_ReturnsEmpty_EvenAfterWrite()
    {
        using var policy = NewTransport(new SingleSlotTransport());
        policy.WritePayload(Opaque(9));
        Assert.True(policy.ReadPayload().IsEmpty);
    }

    [Fact]
    public void Transport_CorruptedCrc_LoadReturnsFalse()
    {
        var sink = new SingleSlotTransport();
        using (var policy = NewTransport(sink))
        {
            policy.WriteHeader(TestMetaLayout.DefaultHeader());
            policy.WritePayload(NewPayload());
            policy.Commit();
        }

        sink.Stored![Layout.HeaderSize + 2] ^= 0xFF;

        using var victim = NewTransport(sink);
        Assert.False(victim.Load());
    }

    [Fact]
    public void Transport_CorruptedMagic_LoadReturnsFalse()
    {
        var sink = new SingleSlotTransport();
        using (var policy = NewTransport(sink))
        {
            policy.WriteHeader(TestMetaLayout.DefaultHeader());
            policy.WritePayload(NewPayload());
            policy.Commit();
        }

        BinaryPrimitives.WriteUInt32LittleEndian(sink.Stored!, 0xDEADBEEF);

        using var victim = NewTransport(sink);
        Assert.False(victim.Load());
    }

    [Fact]
    public void Transport_TooShortBlock_LoadReturnsFalse()
    {
        var sink = new SingleSlotTransport { Stored = new byte[4] };
        using var policy = NewTransport(sink);
        Assert.False(policy.Load());
    }

    [Fact]
    public void Transport_EmptySpanFromTransport_LoadReturnsFalse()
    {
        // ★ 传输契约：Empty 视图 = 无数据（不引入 null）
        var sink = new SingleSlotTransport { Stored = null };
        using var policy = NewTransport(sink);
        Assert.False(policy.Load());
    }

    [Fact]
    public async Task Transport_AsyncRoundtrip_MatchesSync()
    {
        var sink = new SingleSlotTransport();
        using (var policy = NewTransport(sink))
        {
            policy.WriteHeader(TestMetaLayout.DefaultHeader());
            policy.WritePayload(NewPayload());
            policy.WritePayload(Opaque(2));
            await policy.CommitAsync(CancellationToken.None);
        }

        using var reloaded = NewTransport(sink);
        Assert.True(await reloaded.LoadAsync(CancellationToken.None));
        Assert.True(reloaded.ReadPayload().SequenceEqual(Opaque(2)));
    }

    // ════════════════════════════════════════════════════════════
    //  Disabled 专属（no-op）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Disabled_AllOperationsAreNoOp()
    {
        using var policy = new DisabledMetaPolicy<TestMetaHeader, TestMetaPayload>();

        Assert.Equal(0, policy.PayloadSize);
        policy.WriteHeader(TestMetaLayout.DefaultHeader());
        policy.WritePayload(NewPayload());
        policy.Commit();
        Assert.Null(policy.ReadHeader());
        Assert.Null(policy.ReadMetaPayload());
        Assert.True(policy.ReadPayload().IsEmpty);
    }

    [Fact]
    public void Disabled_NonEmptyOpaque_Throws()
    {
        using var policy = new DisabledMetaPolicy<TestMetaHeader, TestMetaPayload>();
        Assert.Throws<ArgumentException>(() => policy.WritePayload(Opaque(1)));
    }

    // ════════════════════════════════════════════════════════════
    //  测试件
    // ════════════════════════════════════════════════════════════

    private const string EngineName = "meta-tests";

    private static TestMetaLayout Layout { get; } = new();

    // ════════════════════════════════════════════════════════════
    //  ★ 自描述块契约（设计决策 ）：容量（MetaOpaqueBytes）零参与盘上几何——
    //    footer/CRC 位由 header.PayloadLength（水位+实际 opaque）自述；跨重启容量随便调。
    // ════════════════════════════════════════════════════════════

    /// <summary>可变容量布局（opaque 插槽大小由构造决定——模拟跨启动 MetaOpaqueBytes 调整）。</summary>
    private static TestMetaLayout LayoutWith(int opaqueCapacity) => new(opaqueCapacity);

    [Fact]
    public void Managed_CapacityGrowAcrossRestart_WatermarkSurvives()
    {
        // 启动1 容量 64：写水位+opaque(32) → 启动2 容量 512：水位与 opaque 按盘自述恢复
        var engine = NewMemoryEngine();
        using (var p1 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(64), engine))
        {
            p1.WriteHeader(TestMetaLayout.DefaultHeader());
            p1.WritePayload(NewPayload());
            p1.WritePayload(Opaque(32));
            p1.Commit();
        }
        using var p2 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(512), engine);
        Assert.True(p2.Load(), "扩容启动：块自述可解读（容量零参与盘上几何）");
        Assert.Equal(NewPayload(), p2.ReadMetaPayload());
        Assert.True(p2.ReadPayload().SequenceEqual(Opaque(32)), "opaque 按盘自述交付");
    }

    [Fact]
    public void Managed_CapacityShrinkAcrossRestart_WatermarkSurvives_OpaqueOverflow()
    {
        // 启动1 容量 512：写 opaque(200) → 启动2 容量 64（盘上 opaque 超新容量）
        var engine = NewMemoryEngine();
        using (var p1 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(512), engine))
        {
            p1.WriteHeader(TestMetaLayout.DefaultHeader());
            p1.WritePayload(NewPayload());
            p1.WritePayload(Opaque(200));
            p1.Commit();
        }
        using var p2 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(64), engine);
        Assert.True(p2.Load(), "缩容启动：水位无条件恢复");
        Assert.Equal(NewPayload(), p2.ReadMetaPayload());
        Assert.True(p2.ReadPayload().SequenceEqual(Opaque(200)), "盘上 opaque 按自述交付（超本启动容量也交付）");

        // 水位 Commit（写侧归零 opaque——新容量 64 装不下 200）→ 再缩容内写入合法
        p2.WritePayload(NewPayload() with { RecordCount = 43 });
        p2.Commit();
        Assert.Equal(43, p2.ReadMetaPayload()!.Value.RecordCount);
        Assert.True(p2.ReadPayload().IsEmpty, "新写周期 opaque 归零（写侧受新容量约束）");
    }

    [Fact]
    public void Managed_ShrinkCommitThenReopen_AtNewGeometry()
    {
        // 缩容 Commit 后重开（仍是小容量）：水位在新几何下可读——旧大块的 stale 尾巴无害
        var engine = NewMemoryEngine();
        using (var p1 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(512), engine))
        {
            p1.WriteHeader(TestMetaLayout.DefaultHeader());
            p1.WritePayload(NewPayload());
            p1.WritePayload(Opaque(200));
            p1.Commit();
        }
        using (var p2 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(64), engine))
        {
            Assert.True(p2.Load());
            p2.WritePayload(NewPayload());
            p2.Commit();
        }
        using var p3 = new ManagedMetaPolicy<TestMetaHeader, TestMetaPayload>(LayoutWith(64), engine);
        Assert.True(p3.Load(), "stale 大块尾部不影响自描述解读");
        Assert.Equal(NewPayload(), p3.ReadMetaPayload());
        Assert.True(p3.ReadPayload().IsEmpty);
    }

    private static TestMetaPayload NewPayload() => new() { WriteCursor = 1000, CommitCursor = 900, RecordCount = 42 };

    private static byte[] Opaque(int len)
    {
        var data = new byte[len];
        for (var i = 0; i < len; i++) data[i] = (byte)(0x40 + i);
        return data;
    }

    private IStorageEngine NewMemoryEngine()
    {
        var engine = new StorageEngineOptions(EngineName).Builder(TierFs.New("memory:")).Start();
        engine.WaitForReady();   // 恢复完成门禁——未就绪引擎不可读写
        return engine;
    }

    private ManagedMetaPolicy<TestMetaHeader, TestMetaPayload> NewManaged(StorageEngine? engine = null)
        => new(Layout, engine ?? NewMemoryEngine());

    private TransportMetaPolicy<TestMetaHeader, TestMetaPayload> NewTransport(IMetaTransport transport)
        => new(Layout, transport);

    /// <summary>测试布局：12B header + 24B 结构化水位 + 64B opaque 插槽。</summary>
    private sealed class TestMetaLayout(int opaqueCapacity = 64) : IMetaLayout<TestMetaHeader, TestMetaPayload>
    {
        public const uint MagicValue = 0x54455354;      // "TEST"
        public const ushort CurrentVersionValue = 3;
        public const int OpaqueCapacityValue = 64;
        public const int StructPayloadValue = 24;

        private readonly int _opaqueCapacity = opaqueCapacity;   // 可变容量——模拟跨启动 MetaOpaqueBytes 调整

        public int HeaderSize => 12;
        public int PayloadSize => StructPayloadValue + _opaqueCapacity;
        public int PayloadOpaqueSize => _opaqueCapacity;

        public uint Magic => MagicValue;
        public ushort CurrentVersion => CurrentVersionValue;
        public ushort DefaultFlags => 0xA5A5;

        public static TestMetaHeader DefaultHeader() => new()
        { Magic = MagicValue, Version = CurrentVersionValue, Flags = 0xA5A5, PayloadLength = 0 };

        public void WriteHeader(Span<byte> dst, in TestMetaHeader header, bool validate)
        {
            if (validate && (header.Magic != Magic || header.Version != CurrentVersion))
                throw new InvalidOperationException($"header Magic/Version 不符：{header.Magic:X8}/v{header.Version}");
            BinaryPrimitives.WriteUInt32LittleEndian(dst, header.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[4..], header.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], header.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], header.PayloadLength);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[10..], header.Reserved);
        }

        public TestMetaHeader ReadHeader(ReadOnlySpan<byte> src) => new()
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(src),
            Version = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]),
            PayloadLength = BinaryPrimitives.ReadUInt16LittleEndian(src[8..]),
            Reserved = BinaryPrimitives.ReadUInt16LittleEndian(src[10..]),
        };

        public void WritePayload(Span<byte> dst, in TestMetaPayload payload)
        {
            BinaryPrimitives.WriteInt64LittleEndian(dst, payload.WriteCursor);
            BinaryPrimitives.WriteInt64LittleEndian(dst[8..], payload.CommitCursor);
            BinaryPrimitives.WriteInt32LittleEndian(dst[16..], payload.RecordCount);
        }

        public TestMetaPayload ReadPayload(ReadOnlySpan<byte> src) => new()
        {
            WriteCursor = BinaryPrimitives.ReadInt64LittleEndian(src),
            CommitCursor = BinaryPrimitives.ReadInt64LittleEndian(src[8..]),
            RecordCount = BinaryPrimitives.ReadInt32LittleEndian(src[16..]),
        };

        public uint GetMagicValue(in TestMetaHeader header) => header.Magic;
        public ushort GetVersion(in TestMetaHeader header) => header.Version;
        public ushort GetPayloadLength(in TestMetaHeader header) => header.PayloadLength;
        public TestMetaHeader WithPayloadLength(in TestMetaHeader header, ushort payloadLength)
        { var h = header; h.PayloadLength = payloadLength; return h; }
        public TestMetaHeader CreateDefaultHeader() => DefaultHeader();
    }

    private struct TestMetaHeader
    {
        public uint Magic;
        public ushort Version;
        public ushort Flags;
        public ushort PayloadLength;
        public ushort Reserved;
    }

    private struct TestMetaPayload : IEquatable<TestMetaPayload>
    {
        public long WriteCursor;
        public long CommitCursor;
        public int RecordCount;

        public bool Equals(TestMetaPayload other) =>
            WriteCursor == other.WriteCursor && CommitCursor == other.CommitCursor && RecordCount == other.RecordCount;

        public override bool Equals(object? obj) => obj is TestMetaPayload p && Equals(p);

        public override int GetHashCode() => HashCode.Combine(WriteCursor, CommitCursor, RecordCount);
    }

    /// <summary>单槽传输桩——每次写覆盖，读回最后一条（空 = 无）。</summary>
    private sealed class SingleSlotTransport : IMetaTransport
    {
        public byte[]? Stored;

        public void WriteBlock(ReadOnlySpan<byte> block) => Stored = block.ToArray();

        public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
        { Stored = block.ToArray(); return default; }

        public ReadOnlySpan<byte> ReadLastBlock() => Stored is null ? ReadOnlySpan<byte>.Empty : Stored;

        public ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
            => Stored is null ? new(ReadOnlyMemory<byte>.Empty) : new(Stored);
    }

    /// <summary>追加流传输桩——块按序累积，读回最后一条（模拟结构自身主流）。</summary>
    private sealed class StreamTransport : IMetaTransport
    {
        public List<byte[]> Blocks { get; } = new();

        public void WriteBlock(ReadOnlySpan<byte> block) => Blocks.Add(block.ToArray());

        public ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
        { Blocks.Add(block.ToArray()); return default; }

        public ReadOnlySpan<byte> ReadLastBlock()
            => Blocks.Count > 0 ? Blocks[^1] : ReadOnlySpan<byte>.Empty;

        public ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
            => Blocks.Count > 0 ? new(Blocks[^1]) : new(ReadOnlyMemory<byte>.Empty);
    }
}
