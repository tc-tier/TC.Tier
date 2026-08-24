using FluentAssertions;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.Metadata.Contracts;

namespace TC.Tier.Runtime.Tests.Structures.Metadata;

/// <summary>
/// VersionedMetadata 完整单元测试。
/// 生命周期：new + Initialize()（后台恢复）+ WaitForReady()（等就绪）；Write(data) 写数据 + 返回版本号；Read(dst) 读当前版本；AsSpan() 读路径 0-copy 视图。
/// <para>★ 接入形态（当前 API）：组合根 TestVolume（TierFs spec 介质平权）+ VersionedMetadata(vol.Fs, settings)。</para>
/// </summary>
public class VersionedMetadataTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    private static byte[] MakePayload(int size, params (int index, byte val)[] markers)
    {
        var b = new byte[size];
        foreach (var (i, v) in markers) if (i < size) b[i] = v;
        return b;
    }

    [Fact]
    public void Initialize_WaitForReady_Ready()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings();
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();
            meta.IsReady.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Returns_VersionNumber()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings, persistencePolicy: new SyncPersistencePolicy());
            meta.Initialize();
            meta.WaitForReady();
            var v1 = meta.Write(MakePayload(64, 0xDE));
            v1.Should().Be(1);
            var v2 = meta.Write(MakePayload(64, 0xAD));
            v2.Should().Be(2);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Read_Roundtrip()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings, persistencePolicy: new SyncPersistencePolicy());
            meta.Initialize();
            meta.WaitForReady();
            var data = MakePayload(64, (0, 0xDE), (1, 0xAD), (63, 0xEF));
            meta.Write(data);

            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0xDE);
            dst[1].Should().Be(0xAD);
            dst[63].Should().Be(0xEF);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_MultipleVersions_ReadLatest()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 32);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings, persistencePolicy: new SyncPersistencePolicy());
            meta.Initialize();
            meta.WaitForReady();

            meta.Write(MakePayload(32, 0x11));
            var dst1 = new byte[32]; meta.Read(dst1);
            dst1[0].Should().Be(0x11);

            meta.Write(MakePayload(32, 0x22));
            var dst2 = new byte[32]; meta.Read(dst2);
            dst2[0].Should().Be(0x22, "读应返回最新版本");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void AsSpan_IsReadPath_ZeroCopy()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings, persistencePolicy: new SyncPersistencePolicy());
            meta.Initialize();
            meta.WaitForReady();
            meta.Write(MakePayload(64, 0x42));

            // AsSpan 返回 0-copy 视图给调用方读
            var span = meta.AsSpan();
            span[0].Should().Be(0x42);
            span.Length.Should().Be(64);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Prepare_ConfirmCommitted_2PC()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            meta.Prepare(seq: 1);
            ((ITransactionParticipant)meta).LastPreparedSeq.Should().Be(1);
            ((ITransactionParticipant)meta).LastCommittedSeq.Should().Be(-1);

            meta.ConfirmCommitted(seq: 1);
            ((ITransactionParticipant)meta).LastCommittedSeq.Should().Be(1);

            var dst = new byte[64];
            meta.Read(dst);
            dst.Should().AllBeEquivalentTo(0, "Prepare 没写新数据（空 payload），读回应是初始零");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Prepare_ConfirmCommitted_2PC()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            // Write 写数据
            meta.Write(MakePayload(64, 0xAA));
            // Prepare 落盘
            meta.Prepare(seq: 1);
            ((ITransactionParticipant)meta).LastPreparedSeq.Should().Be(1);

            meta.ConfirmCommitted(seq: 1);

            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0xAA);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Prepare_Abort_RollbackZeroIO()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            // 先提交 v1（0x11）
            meta.Write(MakePayload(64, 0x11));
            meta.Prepare(seq: 1);
            meta.ConfirmCommitted(seq: 1);

            // Write v2（0x22）→ Prepare → Abort
            meta.Write(MakePayload(64, 0x22));
            meta.Prepare(seq: 2);
            ((ITransactionParticipant)meta).LastPreparedSeq.Should().Be(2);

            meta.Abort(seq: 2);
            ((ITransactionParticipant)meta).LastPreparedSeq.Should().Be(1);

            // 读回应回退到 v1（0x11）
            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0x11, "Abort 后应回退到上一已提交版本");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_Idempotent()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 32);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();
            meta.Write(MakePayload(32, 0x11));
            meta.Prepare(seq: 1);
            meta.ConfirmCommitted(1);

            meta.Write(MakePayload(32, 0x22));
            meta.Prepare(seq: 2);

            meta.Abort(seq: 2);
            meta.Abort(seq: 2);
            meta.Abort(seq: 2);
            ((ITransactionParticipant)meta).LastPreparedSeq.Should().Be(1);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void OnCommitted_ImmediateTrigger_IfAlreadyCommitted()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 32);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();
            meta.Write(MakePayload(32, 0x11));
            meta.Prepare(seq: 5);
            meta.ConfirmCommitted(5);

            bool fired = false;
            ((ITransactionParticipant)meta).OnCommitted(3, () => fired = true);
            fired.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void EnsureReady_Throws_BeforeInitialize()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 32);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            // 未 Initialize，读写抛异常
            var act = () => meta.Read(new byte[32]);
            act.Should().Throw<InvalidOperationException>().WithMessage("*尚未完成恢复*");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Full_2PC_With_TransactionLog()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();
            meta.Write(MakePayload(64, 0xCC));

            using var txVol = new TestVolume();
            using var txEngine = new StorageEngineOptions("tx-log", 4096, enableSegmentation: false).WithDeleteOnClose(true).Builder(txVol.Fs).Start();
            txEngine.WaitForReady();
            txEngine.Allocate(4096);
            var txLog = new TC.Tier.Runtime.Transactions.TransactionLog(txEngine);
            txLog.Register("meta", meta);

            long seq = await txLog.CommitAsync(CancellationToken.None);
            seq.Should().Be(1);
            ((ITransactionParticipant)meta).LastCommittedSeq.Should().Be(1);

            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0xCC);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void CrossInstance_Recovery_SamePayloadSize()
    {
        using var vol = new TestVolume();
        // 实例 1：写入（DeleteOnClose=false 留数据）
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, (0, 0x42), (1, 0x43)));
        }

        // 实例 2：同卷同引擎名重开（扫盘恢复版本链），DeleteOnClose=true 清理
        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0x42);
        dst[1].Should().Be(0x43);
    }

    /// <summary>
    /// ★ 冷热分离（用户裁定）：PayloadSize 只参与本次运行的版本几何（Write/Prepare 追加的 record），
    /// 历史版本恢复按盘上真实大小完整交付。扩容启动（64→128）——加载版本按真实 64B 交付
    /// （不补零到当前 PayloadSize），首次 Write 后切到当前配置 128B。
    /// </summary>
    [Fact]
    public void Recovery_PayloadSizeGrow_HistoryServedAtTrueSize_NoPadding()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, (0, 0xAA), (1, 0xBB), (63, 0xCC)));
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 128, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        // 历史版本按真实 64B 交付——不补零，Read 返回 64
        var dst = new byte[128];
        meta2.Read(dst).Should().Be(64, "加载版本按盘上真实大小交付，不补零到当前 PayloadSize");
        dst[0].Should().Be(0xAA);
        dst[1].Should().Be(0xBB);
        dst[63].Should().Be(0xCC);
        meta2.AsSpan().Length.Should().Be(64);

        // 首次 Write 后当前内容切到热区（本次运行配置 128B）
        meta2.Write(MakePayload(128, 0xEE));
        var dst2 = new byte[128];
        meta2.Read(dst2).Should().Be(128, "Write 后按当前 PayloadSize 交付");
        dst2[0].Should().Be(0xEE);
        dst2[127].Should().Be(0xEE);
    }

    /// <summary>
    /// ★ 冷热分离：缩容启动（128→64）——历史版本完整交付 128B（不截断），
    /// 首次 Write 后切到当前配置 64B。
    /// </summary>
    [Fact]
    public void Recovery_PayloadSizeShrink_HistoryServedInFull_NoTruncation()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 128, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(128, (0, 0x11), (127, 0x77)));
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        var dst = new byte[128];
        meta2.Read(dst).Should().Be(128, "缩容启动不得截断历史——完整交付盘上真实大小");
        dst[0].Should().Be(0x11);
        dst[127].Should().Be(0x77);
        meta2.AsSpan().Length.Should().Be(128);

        // 首次 Write 后切到热区（当前配置 64B）
        meta2.Write(MakePayload(64, 0x22));
        var dst2 = new byte[64];
        meta2.Read(dst2).Should().Be(64, "Write 后按当前 PayloadSize 交付");
        dst2[0].Should().Be(0x22);
    }

    /// <summary>
    /// ★ 缩容零覆写回归：恢复载入后无 Write 直接 Prepare+Commit（纯 seq 固化）——不得把热区
    /// （零内容）当新版本追加进链（冷热分离后热区不再持有历史镜像）。跨重启链头必须仍是
    /// 完整的历史版本。
    /// </summary>
    [Fact]
    public void Recovery_NoWrite_PrepareCommit_HistoricalHeadPreserved()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 128, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(128, (0, 0x11), (127, 0x77)));
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: false);
        using (var meta2 = new VersionedMetadata(vol.Fs, settings2))
        {
            meta2.Initialize();
            meta2.WaitForReady();
            meta2.CurrentVersion.Should().Be(1);
            meta2.Prepare(seq: 5);          // 无新 Write——内容未变，不得追加零内容新版本
            meta2.ConfirmCommitted(seq: 5);
            ((ITransactionParticipant)meta2).LastCommittedSeq.Should().Be(5);
            var dst = new byte[128];
            meta2.Read(dst).Should().Be(128, "纯 seq 固化不得覆盖加载镜像");
            dst[127].Should().Be(0x77);
        }

        var settings3 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta3 = new VersionedMetadata(vol.Fs, settings3);
        meta3.Initialize();
        meta3.WaitForReady();

        meta3.CurrentVersion.Should().Be(1, "链头必须仍是历史 128B 版本（零覆写会变成 64B 零内容新版本）");
        var dst3 = new byte[128];
        meta3.Read(dst3).Should().Be(128);
        dst3[0].Should().Be(0x11);
        dst3[127].Should().Be(0x77);
    }

    /// <summary>
    /// ★ 恢复后单次写即 Abort：回退到加载的完整历史版本（不是截断后的当前配置大小）。
    /// 无 SyncPolicy——Write 仅内存，Prepare 落盘悬干，Abort 尾回收 + 指回加载版本。
    /// </summary>
    [Fact]
    public void Recovery_Shrink_WriteThenAbort_RollbackToFullHistory()
    {
        using var vol = new TestVolume();
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 128, deleteOnClose: false);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(128, (0, 0x11), (127, 0x77)));
        }

        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64, deleteOnClose: true);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        meta2.Write(MakePayload(64, 0x22));
        meta2.Prepare(seq: 2);
        meta2.Abort(seq: 2);

        var dst = new byte[128];
        meta2.Read(dst).Should().Be(128, "Abort 回退到加载的历史版本（完整大小，不截断到当前 PayloadSize）");
        dst[0].Should().Be(0x11);
        dst[127].Should().Be(0x77);
    }

    /// <summary>
    /// ★ 混尺寸链头截断回归（用户原则审计揪出）：PayloadSize 跨重启变更跨扇区对齐档
    /// （64→600：record 512B→1024B）后，ReclaimOldVersions 必须逐 record 自身几何推进——
    /// 旧代码统一 _recordSize 步进落在旧 record 中段，ReclaimHead 掐半活 record，
    /// 下次重启扫盘断链、整个链头之后静默丢失。
    /// </summary>
    [Fact]
    public void MixedSizeChain_ReclaimOldVersions_CrossRestart_Intact()
    {
        var vol = new TestVolume();
        try
        {
            // 启动1：PayloadSize=600（record=42+600→1024B），提交 v1
            var s1 = new VersionedMetadataSettings(
                new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false))
            { PayloadSize = 600 };
            using (var m1 = new VersionedMetadata(vol.Fs, s1))
            {
                m1.Initialize();
                m1.WaitForReady();
                var big = new byte[600];
                Array.Fill(big, (byte)0xA1);
                m1.Write(big);
                m1.Prepare(1);
                m1.ConfirmCommitted(1);
            }

            // 启动2：PayloadSize=64（record=42+64→512B）——混尺寸链；写 v2 提交触发 ReclaimOldVersions
            var s2 = new VersionedMetadataSettings(
                new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false))
            { PayloadSize = 64 };
            using (var m2 = new VersionedMetadata(vol.Fs, s2))
            {
                m2.Initialize();
                m2.WaitForReady();
                m2.CurrentVersion.Should().Be(1, "v1 按自身几何恢复");
                m2.Write(MakePayload(64, 0xB2));
                m2.Prepare(2);
                m2.ConfirmCommitted(2);   // 触发 N=2 头截断——旧 bug：统一步进掐半 v1 record
                m2.CurrentVersion.Should().Be(2);
                var dst = new byte[64];
                m2.Read(dst);
                dst[0].Should().Be(0xB2);
            }

            // 启动3：重开——链必须完好（v2 可恢复；旧 bug 下扫盘从掐半处断链=全丢）
            var s3 = new VersionedMetadataSettings(
                new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true))
            { PayloadSize = 64 };
            using var m3 = new VersionedMetadata(vol.Fs, s3);
            m3.Initialize();
            m3.WaitForReady();
            m3.CurrentVersion.Should().Be(2, "混尺寸链经正确头截断后跨重启完好");
            var dst3 = new byte[64];
            m3.Read(dst3);
            dst3[0].Should().Be(0xB2);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void SameInstance_Persist_ThenRead_StillValid()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings, persistencePolicy: new SyncPersistencePolicy());
            meta.Initialize();
            meta.WaitForReady();
            meta.Write(MakePayload(64, (0, 0x42), (63, 0x99)));

            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0x42);
            dst[63].Should().Be(0x99);
        }
        finally { vol.Dispose(); }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Write_Read_FullPayload(int payloadSize)
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: payloadSize);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings, persistencePolicy: new SyncPersistencePolicy());
            meta.Initialize();
            meta.WaitForReady();
            var data = new byte[payloadSize];
            for (int i = 0; i < payloadSize; i++) data[i] = (byte)(i & 0xFF);
            meta.Write(data);

            var dst = new byte[payloadSize];
            meta.Read(dst);
            for (int i = 0; i < payloadSize; i++)
                dst[i].Should().Be((byte)(i & 0xFF), $"byte {i}");
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // === Transport meta（宿主流嵌入）跨实例恢复 / Abort 无泄漏 / 窗口语义 ===
    // ════════════════════════════════════════════════════════════

    /// <summary>Transport meta（宿主流嵌入）模式：Write+Prepare+Commit 落盘水位 meta record，跨实例 Initialize 从版本链恢复水位。</summary>
    [Fact]
    public void Embedded_MetaPolicy_CrossInstance_RecoverRoundtrip()
    {
        using var vol = new TestVolume();
        // 实例 1：Transport meta（宿主流嵌入），写入并提交（水位持久化为版本链中的 meta record）
        var settings1 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64,
            metaKind: MetaPolicyKind.Transport, deleteOnClose: false, payloadCapacity: 64);
        using (var meta1 = new VersionedMetadata(vol.Fs, settings1, persistencePolicy: new SyncPersistencePolicy()))
        {
            meta1.Initialize();
            meta1.WaitForReady();
            meta1.Write(MakePayload(64, (0, 0x42), (63, 0x99)));
            meta1.Prepare(seq: 1);
            meta1.ConfirmCommitted(seq: 1);
        }

        // 实例 2：同 Transport meta（宿主流嵌入），跨实例恢复——水位从版本链 meta record 恢复
        var settings2 = TestMetadataSettingsFactory.CreateSettings(vol, payloadSize: 64,
            metaKind: MetaPolicyKind.Transport, deleteOnClose: true, payloadCapacity: 64);
        using var meta2 = new VersionedMetadata(vol.Fs, settings2);
        meta2.Initialize();
        meta2.WaitForReady();

        // 水位 LastCommittedSeq 从 Transport meta record 恢复（=1），数据 0x42/0x99 从链头版本恢复
        ((ITransactionParticipant)meta2).LastCommittedSeq.Should().Be(1, "Transport meta record 恢复水位");
        var dst = new byte[64];
        meta2.Read(dst);
        dst[0].Should().Be(0x42);
        dst[63].Should().Be(0x99);
    }

    /// <summary>Abort 尾截断 ReclaimTail：Abort 后 _highestVersionAddress 回退到 Prepare 前，无悬干泄漏。
    /// <para>★ 用默认落盘策略（无 SyncPolicy）——Write 只更新内存，Prepare 才持久化。
    ///   这样 Prepare(v2) 落盘的悬干新版本可被 Abort 回退；SyncPolicy 下 Write 已自动持久化，Abort 无法回退 Write。</para>
    /// </summary>
    [Fact]
    public void Abort_ReclaimsTail_NoLeak()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);  // ★ 无 SyncPolicy：Write 仅内存，Prepare 才落盘
            meta.Initialize();
            meta.WaitForReady();

            // 先提交 v1（Prepare 落盘 v1）
            meta.Write(MakePayload(64, 0x11));
            meta.Prepare(seq: 1);
            meta.ConfirmCommitted(seq: 1);
            var allocatedBeforeV2 = meta.HighestVersionAddress;  // = v1 地址

            // Write v2（仅内存）→ Prepare（落盘 v2 悬干）→ Abort
            meta.Write(MakePayload(64, 0x22));
            meta.Prepare(seq: 2);
            meta.Abort(seq: 2);

            // Abort 后链头回退到 v1（Prepare 前 _highestVersionAddress）
            meta.HighestVersionAddress.Should().Be(allocatedBeforeV2,
                "Abort 应回退链头到 Prepare 前水位（丢弃悬干 v2）");
            // 读回应回退到 v1
            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0x11, "Abort 后数据回退到 v1");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>窗口语义：Write(v1)+Prepare+Commit+Write(v2)+Prepare+Abort 回退到 v1（确认 SlideMemoryWindow 在 Write 覆盖前正确保留上一版本）。</summary>
    [Fact]
    public void SlideMemoryWindow_RetainsPreviousVersion()
    {
        var (settings, vol) = TestMetadataSettingsFactory.CreateSettings(payloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);  // 无 SyncPolicy
            meta.Initialize();
            meta.WaitForReady();

            // v1 提交
            meta.Write(MakePayload(64, 0x11));
            meta.Prepare(seq: 1);
            meta.ConfirmCommitted(seq: 1);

            // Write v2（覆盖 [0]，SlideMemoryWindow 应把 v1 保留到 [1]）
            meta.Write(MakePayload(64, 0x22));

            // Prepare v2 → Abort → 应回退到 v1
            meta.Prepare(seq: 2);
            meta.Abort(seq: 2);

            var dst = new byte[64];
            meta.Read(dst);
            dst[0].Should().Be(0x11, "SlideMemoryWindow 在 Write 覆盖 [0] 前应保留上一版本到 [1]");
        }
        finally { vol.Dispose(); }
    }
}
