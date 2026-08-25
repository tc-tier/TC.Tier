using FluentAssertions;
using TC.Tier.Runtime.Structures.Metadata;

namespace TC.Tier.Runtime.Tests.Structures.Metadata;

/// <summary>
/// MetadataBase 恢复部分专属契约测试（1:1 对应 src/.../Metadata/MetadataBase.Recovery.cs）。
/// <para>钉住恢复契约：三级回退优先级（hints → meta → 扫盘）、扫盘链头语义（最高地址=链头，非版本号）、
/// 同版本重复持久化去重、撕裂尾容错（magic 不匹配=链结束）、Failed 可观测（WaitForReady 重抛）、
/// 取消后重试（RecoveryBase.Reset + InitializeMetaPolicy 幂等）、Initialize 幂等。</para>
/// <para>基础跨实例 happy path 见 <see cref="VersionedMetadataTests"/>（CrossInstance/Grow/Shrink/Embedded_MetaPolicy）。</para>
/// </summary>
public class MetadataBaseRecoveryTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    // ════════════════════════════════════════════════════════════
    // === 状态机契约（RecoveryBase 模板编排，Metadata 接线）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void RecoveryState_NotStarted_BeforeInitialize()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.RecoveryState.Phase.Should().Be(RecoveryPhase.NotStarted);
            meta.IsReady.Should().BeFalse();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void EmptyVolume_Recovery_CompletesWithEmptyState()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            meta.IsReady.Should().BeTrue();
            meta.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);
            meta.RecoveryState.Percent.Should().Be(100);
            // 空卷：无版本——CurrentVersion=0，链头地址=Empty（合法地址空间的起点），镜像全零
            meta.CurrentVersion.Should().Be(0);
            meta.HighestVersionAddress.Should().Be(LogicalAddress.Empty);
            var dst = new byte[64];
            meta.Read(dst);
            dst.Should().AllBeEquivalentTo(0);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Initialize_AfterReady_IsIdempotentNoOp()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            var act = () => meta.Initialize(); // CAS 幂等闸门——重复调静默返回
            act.Should().NotThrow();
            meta.IsReady.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // === 三级回退优先级（hints → meta → 扫盘）===
    // ════════════════════════════════════════════════════════════

    /// <summary>第一级 hints 优先于扫盘：注入旧版本地址，恢复加载旧版本（扫盘本会找到更新的链头）。</summary>
    [Fact]
    public void Hints_HeadAddress_WinsOverScan()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        var addrV1 = LogicalAddress.Empty;
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, 0x11));
            addrV1 = meta1.HighestVersionAddress; // v1 链头
            meta1.Write(MakePayload(64, 0x22)); // v2（扫盘本会找到它）
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize(new MetadataRecoveryHints { HighestVersionAddress = addrV1 });
        meta2.WaitForReady();

        // hints 地址胜出：加载的是 v1 而非扫盘可见的 v2
        meta2.CurrentVersion.Should().Be(1, "hints 指向 v1");
        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0x11, "hints 优先于扫盘——加载 v1 而非更新的 v2");
    }

    /// <summary>第一级 hints 的提交点：只注入 LastCommittedSeq（无地址），链头走扫盘，seq 从 hints 恢复。</summary>
    [Fact]
    public void Hints_LastCommittedSeq_RestoredThroughScan()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, 0x42));
            meta1.Prepare(seq: 7);
            meta1.ConfirmCommitted(seq: 7);
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize(new MetadataRecoveryHints { LastCommittedSeq = 7 });
        meta2.WaitForReady();

        ((ITransactionParticipant)meta2).LastCommittedSeq.Should().Be(7, "hints 注入的提交点应恢复");
        ((ITransactionParticipant)meta2).LastPreparedSeq.Should().Be(7, "恢复后 prepared 归位到 committed");
        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0x42, "链头数据走扫盘恢复");
    }

    // ════════════════════════════════════════════════════════════
    // === 扫盘语义（链头=最高地址；同版本去重；撕裂尾容错）===
    // ════════════════════════════════════════════════════════════

    /// <summary>Prepare 内容未变不追加：Sync 落盘 v1 后 Prepare（无新 Write）不得重复追加同版本
    /// record——链上仅一份（链头=链尾）。旧行为无条件追加 → 链上两份同版本 record。</summary>
    [Fact]
    public void Prepare_UnchangedContent_SkipsAppend_ChainSingleRecord()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, 0xAA));   // Sync 落盘 v1 @A
            meta1.Prepare(seq: 1);                // 内容未变——不得追加 v1@B
            meta1.ConfirmCommitted(seq: 1);
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        meta2.CurrentVersion.Should().Be(1);
        meta2.HighestVersionAddress.Should().Be(meta2.LowestVersionAddress,
            "未变内容 Prepare 不追加——链上仅一份 record（旧行为最高地址=重复追加的那份）");
        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0xAA);
    }

    /// <summary>同版本号多份 record（崩溃场景：追加后 meta 未及更新）——扫盘取最高地址为链头，
    /// 版本号不重复累计。用公共 codec 手工构造第二份同版本 record 制造重复（公共 API 已去重）。</summary>
    [Fact]
    public void SameVersion_DuplicateRecords_ScanTakesHighestAddress()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, 0xAA));   // v1 @A
        }

        // 手工追加第二份同版本（v1，内容 0xBB）@B——同引擎名/段几何，构造合法 record 裸追加
        var engineOptions = new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false)
            .WithDeleteOnClose(false);
        using (var engine = engineOptions.Builder(vol.Fs).Start())
        {
            engine.WaitForReady();
            const int headerSize = 42;
            var sectorSize = (int)engine.SectorSize;
            int paddingLen = (sectorSize - (headerSize + 64) % sectorSize) % sectorSize;
            var rec = new byte[headerSize + 64 + paddingLen];
            MetadataHeaderCodec.Write(rec, new MetadataBase.MetadataHeader
            {
                MagicValue = MetadataBase.MetadataHeader.Magic,
                Version = MetadataBase.MetadataHeader.CurrentVersion,
                Flags = MetadataBase.MetadataHeader.DefaultFlags,
                PayloadLength = 64,
                PaddingLength = (ushort)paddingLen,
                PreviousVersion = LogicalAddress.Empty,
                MetadataVersion = 1,   // 与 v1 同版本号
            });
            rec.AsSpan(headerSize, 64).Fill(0xBB);
            RecordCodec.FillCrc(rec, MetadataBase.MetadataHeader.DefaultFlags, rec.Length,
                MetadataHeaderCodec.Offset_Crc);
            engine.Append(rec);
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        meta2.CurrentVersion.Should().Be(1, "同版本多份 record 不重复累计版本号");
        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0xBB, "链头=最高地址的第二份 record");
    }

    /// <summary>撕裂尾容错：链尾后的垃圾数据（magic 不匹配）= 链结束，恢复定位到最后一条合法 record。</summary>
    [Fact]
    public void TornTail_GarbageAfterLastVersion_ScanStopsAtLastGoodRecord()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, 0x42));
        }

        // 模拟撕裂写：裸引擎在链尾后追加一段非 magic 垃圾（与工厂同引擎名/段几何——同一引擎子目录）
        var garbageOptions = new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false)
            .WithDeleteOnClose(false);
        using (var engine = garbageOptions.Builder(vol.Fs).Start())
        {
            engine.WaitForReady();
            engine.Append(new byte[512]); // 全零——首 4B 即 magic 不匹配
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        meta2.CurrentVersion.Should().Be(1, "垃圾尾不算版本");
        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0x42, "撕裂尾前最后一条合法 record 是链头");
    }

    // ════════════════════════════════════════════════════════════
    // === 恢复失败/取消/重试（RecoveryBase 模板契约经 Metadata 接线验证）===
    // ════════════════════════════════════════════════════════════

    /// <summary>恢复失败：RecoveryState.Failed + Error 可观测；WaitForReady 重抛（不让"已失败"返回成功）。</summary>
    [Fact]
    public void InjectedRecovery_Failure_ObservableAndWaitForReadyRethrows()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            var scripted = new ScriptedRecovery((run, ct) => throw new InvalidOperationException("boom"));
            using var meta = new VersionedMetadata(vol.Fs, settings, recovery: scripted);
            meta.Initialize();

            SpinWait.SpinUntil(() => meta.RecoveryState.Phase == RecoveryPhase.Failed, 120000)
                .Should().BeTrue("恢复算法抛出后状态机应进入 Failed");
            meta.IsReady.Should().BeFalse();
            meta.RecoveryState.Error.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("boom");

            var act = () => meta.WaitForReady();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*恢复任务已失败*")
                .WithInnerException<InvalidOperationException>()
                .Which.Message.Should().Be("boom");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>取消后可重试：CancelRecovery → Failed（取消并入 Failed）；再次 Initialize → 恢复重跑（同一注入实例），达 Ready。</summary>
    [Fact]
    public void CancelledRecovery_CanRetryInitialize_ReachReady()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            // 第 1 跑挂起等 ct（取消即 OCE）；第 2 跑立即完成
            var scripted = new ScriptedRecovery((run, ct) =>
                run == 1 ? new ValueTask(Task.Delay(Timeout.Infinite, ct)) : ValueTask.CompletedTask);
            using var meta = new VersionedMetadata(vol.Fs, settings, recovery: scripted);
            meta.Initialize();
            meta.CancelRecovery();

            SpinWait.SpinUntil(() => meta.RecoveryState.Phase == RecoveryPhase.Failed, 120000)
                .Should().BeTrue("取消并入 Failed（lifecycle.md §3 失败语义）");
            meta.IsReady.Should().BeFalse();

            var retry = () => meta.Initialize(); // 取消回退 _initialized=0——允许重试
            retry.Should().NotThrow();
            SpinWait.SpinUntil(() => meta.IsReady, 120000).Should().BeTrue("重试后恢复完成");
            scripted.Runs.Should().Be(2, "同一恢复实例被重跑（Reset 语义）");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>脚本化恢复——RecoveryBase 模板派生（对齐 DefaultMetadataRecovery 的正确接法），按跑次执行脚本。</summary>
    private sealed class ScriptedRecovery(Func<int, CancellationToken, ValueTask> script)
        : RecoveryBase<MetadataRecoveryHints>
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        protected override ValueTask OnRecoveryCoreAsync(MetadataRecoveryHints hints, CancellationToken ct)
        {
            var run = Interlocked.Increment(ref _runs);
            return script(run, ct);
        }
    }
}
