using FluentAssertions;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Mirror;

namespace TC.Tier.Runtime.Tests.Structures.Mirror;

/// <summary>
/// MirrorBase 恢复部分专属契约测试（1:1 对应 src/.../Mirror/MirrorBase.Recovery.cs）。
/// 钉住恢复契约：三级回退优先级、扫盘链头语义（最高地址）、撕裂尾容错、
/// 悬干裁决（Transport meta prepared&gt;committed → 按会话版本号尾截断）、
/// Failed 可观测、取消重试。
/// </summary>
public class MirrorBaseRecoveryTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    private static void WriteCheckpoint(WholeMirror mirror, long totalSize, byte fill)
    {
        mirror.BeginSession();
        var chunk = MakePayload(4096, fill);
        long off = 0;
        while (off < totalSize)
        {
            int n = (int)Math.Min(chunk.Length, totalSize - off);
            mirror.AppendChunk(chunk.AsSpan(0, n));
            off += n;
        }
        mirror.EndSession();
    }

    [Fact]
    public void RecoveryState_NotStarted_BeforeInitialize()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.RecoveryState.Phase.Should().Be(RecoveryPhase.NotStarted);
            mirror.IsReady.Should().BeFalse();
        }
        finally { vol.Dispose(); }
    }

    /// <summary>第一级 hints 优先于扫盘：三轮会话后注入 v2（保留窗口内）地址，链头落在 v2 而非扫盘可见的 v3。</summary>
    [Fact]
    public void Hints_HeadAddress_WinsOverScan()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        LogicalAddress v2Addr = LogicalAddress.Empty;
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            for (int s = 1; s <= 3; s++)
            {
                WriteCheckpoint(mirror1, 4096, (byte)(0x10 + s));
                mirror1.Prepare(s);
                mirror1.ConfirmCommitted(s);
                if (s == 2) v2Addr = mirror1.HighestVersionAddress;
            }
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize(new MirrorRecoveryHints { HighestVersionAddress = v2Addr });
        mirror2.WaitForReady();

        // hints 指向 v2（保留窗口内、N=2 未回收）——胜过扫盘本会找到的 v3
        mirror2.HighestVersionAddress.Should().Be(v2Addr, "hints 优先于扫盘——链头指向 v2");
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x12);
        vol.Dispose();
    }

    /// <summary>
    /// 前缀洞回归（N=2 头截断 + 跨实例恢复）：三轮会话后 v1 被段内 PunchHole 清零且 MinAddress 不后移——
    /// 恢复扫盘必须跳过全零前缀洞，从 v2 起步重建（v2/v3 完整可见）。
    /// </summary>
    [Fact]
    public void LeadingHole_AfterN2Reclaim_ScanSkipsHoleAndRebuilds()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            for (int s = 1; s <= 3; s++)
            {
                WriteCheckpoint(mirror1, 4096, (byte)(0x10 + s));
                mirror1.Prepare(s);
                mirror1.ConfirmCommitted(s); // 第 3 轮触发 N=2：v1（@Empty）被段内打洞
            }
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(3, "洞后 v2/v3 应被扫到——前缀洞不阻断重建");
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x13, "链头是 v3");
        mirror2.Verify(mirror2.HighestVersionAddress).Should().BeTrue("v3 CRC64 完整");
        vol.Dispose();
    }

    /// <summary>撕裂尾容错：链尾后的垃圾数据（magic 不匹配）= 链结束，恢复定位最后一条合法 record。</summary>
    [Fact]
    public void TornTail_GarbageAfterLastVersion_ScanStopsAtLastGoodRecord()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            WriteCheckpoint(mirror1, 4096, 0x42);
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
        }

        // 模拟撕裂写：裸引擎在链尾后追加非 magic 垃圾
        using (var engine = new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false).Builder(vol.Fs).Start())
        {
            engine.WaitForReady();
            engine.Append(new byte[512]); // 全零——首 4B 即 magic 不匹配
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(1, "垃圾尾不算版本");
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x42, "撕裂尾前最后一条合法 record 是链头");
        vol.Dispose();
    }

    /// <summary>
    /// 悬干裁决（Transport meta）：实例 1 写完 v2 + Prepare(2) 后模拟崩溃（未 Commit）——
    /// 实例 2 恢复按 meta prepared(2) &gt; committed(1) 尾截断 v2 会话，链头回 v1。
    /// </summary>
    [Fact]
    public void DanglingSession_TruncatedOnRecovery_WithTransportMeta()
    {
        var vol = new TestVolume();
        var opts = new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false);
        var settings1 = new WholeMirrorSettings(opts)
        {
            MetaPolicyKind = MetaPolicyKind.Transport,
            MetaOpaqueBytes = 64,
        };
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            // v1 提交（meta: committed=1, prepared=1）
            WriteCheckpoint(mirror1, 4096, 0x11);
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
            // v2 写完 + Prepare(2)——此后模拟崩溃（不 Commit 不 Abort）
            WriteCheckpoint(mirror1, 4096, 0x22);
            mirror1.Prepare(2);
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true))
        {
            MetaPolicyKind = MetaPolicyKind.Transport,
            MetaOpaqueBytes = 64,
        };
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(1, "悬干会话 v2 被恢复裁决尾截断");
        ((ITransactionParticipant)mirror2).LastCommittedSeq.Should().Be(1, "meta 恢复提交点");
        ((ITransactionParticipant)mirror2).LastPreparedSeq.Should().Be(1, "prepared 归位到 committed");
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x11, "链头回退到已提交 v1");
        vol.Dispose();
    }

    /// <summary>恢复失败：RecoveryState.Failed + Error 可观测；WaitForReady 重抛（不让"已失败"返回成功）。</summary>
    [Fact]
    public void InjectedRecovery_Failure_ObservableAndWaitForReadyRethrows()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            var scripted = new ScriptedRecovery((run, ct) => throw new InvalidOperationException("boom"));
            using var mirror = new WholeMirror(vol.Fs, settings, recovery: scripted);
            mirror.Initialize();

            SpinWait.SpinUntil(() => mirror.RecoveryState.Phase == RecoveryPhase.Failed, 5000)
                .Should().BeTrue("恢复算法抛出后状态机应进入 Failed");
            mirror.IsReady.Should().BeFalse();
            mirror.RecoveryState.Error.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("boom");

            var act = () => mirror.WaitForReady();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*恢复任务已失败*")
                .WithInnerException<InvalidOperationException>()
                .Which.Message.Should().Be("boom");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>取消后可重试：CancelRecovery → Failed（取消并入 Failed）；再次 Initialize 重跑同一恢复实例达 Ready。</summary>
    [Fact]
    public void CancelledRecovery_CanRetryInitialize_ReachReady()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            var scripted = new ScriptedRecovery((run, ct) =>
                run == 1 ? new ValueTask(Task.Delay(Timeout.Infinite, ct)) : ValueTask.CompletedTask);
            using var mirror = new WholeMirror(vol.Fs, settings, recovery: scripted);
            mirror.Initialize();
            mirror.CancelRecovery();

            SpinWait.SpinUntil(() => mirror.RecoveryState.Phase == RecoveryPhase.Failed, 5000)
                .Should().BeTrue("取消并入 Failed");
            mirror.IsReady.Should().BeFalse();

            var retry = () => mirror.Initialize();
            retry.Should().NotThrow();
            SpinWait.SpinUntil(() => mirror.IsReady, 30000).Should().BeTrue("重试后恢复完成");
            scripted.Runs.Should().Be(2, "同一恢复实例被重跑（Reset 语义）");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>
    /// Managed meta 大容量回归：MetaOpaqueBytes 2MB（远超旧硬编码 1MB 单段上限）——
    /// meta 引擎单段容量按块几何计算（align4K(header+水位+容量+footer)），跨实例恢复水位无损。
    /// </summary>
    [Fact]
    public void ManagedMeta_LargeOpaqueCapacity_EngineSizedByGeometry()
    {
        var vol = new TestVolume();
        var mk = (bool del) => new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(del))
        {
            MetaPolicyKind = MetaPolicyKind.Managed,
            MetaOpaqueBytes = 2 * 1024 * 1024,   // ★ 2MB——旧 1MB 硬编码单段直接 Address space exhausted
        };
        try
        {
            using (var mirror1 = new WholeMirror(vol.Fs, mk(false)))
            {
                mirror1.Initialize();
                mirror1.WaitForReady();
                WriteCheckpoint(mirror1, 4096, 0x42);
                mirror1.Prepare(1);
                mirror1.ConfirmCommitted(1);
            }

            using var mirror2 = new WholeMirror(vol.Fs, mk(true));
            mirror2.Initialize();
            mirror2.WaitForReady();
            mirror2.CurrentVersion.Should().Be(1, "水位经 Managed meta（2MB 容量块）恢复");
            ((ITransactionParticipant)mirror2).LastCommittedSeq.Should().Be(1);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>脚本化恢复——RecoveryBase 模板派生，按跑次执行脚本。</summary>
    private sealed class ScriptedRecovery(Func<int, CancellationToken, ValueTask> script)
        : RecoveryBase<MirrorRecoveryHints>
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        protected override ValueTask OnRecoveryCoreAsync(MirrorRecoveryHints hints, CancellationToken ct)
        {
            var run = Interlocked.Increment(ref _runs);
            return script(run, ct);
        }
    }
}
