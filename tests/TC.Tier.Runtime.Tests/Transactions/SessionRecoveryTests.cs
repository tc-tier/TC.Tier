using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Tests.Structures.Log;

namespace TC.Tier.Runtime.Tests.Transactions;

/// <summary>
/// Session 恢复裁决契约测试（session-manager-design.md §7/§8.7——崩溃三窗口跨实例，真 EntryLog）：
/// <para>崩溃模拟形态（对齐 LogAbortTests）：结构正常 Dispose（悬干已由 Prepare 落盘持久），
/// Manager 不 Dispose（无裁决/排水副作用）——等价"进程停在窗口时点的持久状态"。</para>
/// <para>三窗口：①物化中（Prepare 前）——结构恢复截断，无悬干；②Prepare 后 Confirm 前——
/// 域声明裁决（forward-commit 前推缺省 / DropTail 截断）；③Confirm 后——水位重建自参与者。</para>
/// <para>跨实例 seq 持久化依赖 Managed meta（Disabled 下 seq 不持久化——meta.md §5）。</para>
/// </summary>
public class SessionRecoveryTests
{
    /// <summary>2PC 全接管形态的 EntryLogSettings（自动提交三维度全禁——同 TransactionLogContext）+ 跨实例重开（deleteOnClose:false）。</summary>
    private static EntryLogSettings EntrySettings(TestVolume vol, string name = "entry")
        => new(new StorageEngineOptions(name, 8L << 20, enableSegmentation: true,
            preallocateFile: false, deleteOnClose: false))
        {
            LogPageSizeBits = 22,
            MetaPolicyKind = MetaPolicyKind.Managed,   // seq 跨实例持久化
            CommitInterval = TimeSpan.FromMilliseconds(-1),
            MaxUnflushedBytes = long.MaxValue,
            MaxUnflushedCount = int.MaxValue,
        };

    private static int ReplayCount(EntryLog log)
    {
        int n = 0;
        log.Replay((payload, isMeta, addr) => { if (!isMeta) n++; });
        return n;
    }

    [Fact]
    public async Task CrashAfterConfirm_WatermarkRebuiltFromParticipant_SeqContinues()
    {
        // 窗口③：Confirm 后崩溃——恢复=参与者水位读取（无悬干），新域 seq 严格接续
        var vol = new TestVolume();
        var settings = EntrySettings(vol);
        try
        {
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                var m = SessionManager.Create(vol.Fs, "kv", participants: ("log", log));
                m.Initialize();
                m.WaitForReady();
                using var s = m.OpenSession();
                s.Stage(() => log.Append("A"u8.ToArray()));
                (await s.CommitAsync()).Should().Be(1);
                // 模拟崩溃：Confirm 完成后不再操作（Manager 不 Dispose——无裁决副作用）
            }

            using (var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                // 结构 2PC 语义：Confirm 不落盘（committed 的持久化由下一次 Prepare 的 meta 快照捎带）
                // ——Confirm 后崩溃恢复为悬干形态（prepared=1 > committed=-1）
                log2.LastPreparedSeq.Should().Be(1);
                log2.LastCommittedSeq.Should().Be(-1);

                var m2 = SessionManager.Create(vol.Fs, "kv", participants: ("log", log2));   // 缺省 forward-commit
                m2.Initialize();
                m2.WaitForReady();
                log2.LastCommittedSeq.Should().Be(1, "前推裁决：悬干推到 prepared seq");
                m2.LastCommittedSeq.Should().Be(1, "域起始水位=裁决后参与者已提交 max（ReconcileStartup）");

                using var s2 = m2.OpenSession();
                s2.Stage(() => log2.Append("B"u8.ToArray()));
                (await s2.CommitAsync()).Should().Be(2, "新域 seq 严格接续（大于一切已用 seq）");
                ReplayCount(log2).Should().Be(2);
                await m2.DisposeAsync();
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task CrashAfterPrepare_ForwardCommit_AdvancesDanglingToPrepared()
    {
        // 窗口②（缺省 forward-commit）：Prepare 后 Confirm 前崩溃——悬干前推到 prepared seq
        var vol = new TestVolume();
        var settings = EntrySettings(vol);
        try
        {
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                log.Append("A"u8.ToArray());
                log.Prepare(1);
                log.ConfirmCommitted(1);   // 建立提交边界

                log.Append("B"u8.ToArray());   // tx2 悬干
                log.Prepare(2);                // Prepare 落盘（悬干持久）
                // 不 Confirm——崩溃
            }

            using (var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                log2.LastPreparedSeq.Should().Be(2, "悬干跨实例可见");
                log2.LastCommittedSeq.Should().Be(1);

                var m2 = SessionManager.Create(vol.Fs, "kv", participants: ("log", log2));   // 缺省 forward-commit
                m2.Initialize();
                m2.WaitForReady();

                log2.LastCommittedSeq.Should().Be(2, "前推：悬干推到 prepared seq");
                m2.LastCommittedSeq.Should().Be(2, "域水位含前推结果");
                ReplayCount(log2).Should().Be(2, "悬干数据转正可见（宁可前推不丢）");
                await m2.DisposeAsync();
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task CrashAfterPrepare_DropTail_TruncatesDangling()
    {
        // 窗口②（水位一致档 DropTail）：悬干截断回已确认水位
        var vol = new TestVolume();
        var settings = EntrySettings(vol);
        try
        {
            LogicalAddress boundary;
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                log.Append("A"u8.ToArray());
                log.Prepare(1);
                log.ConfirmCommitted(1);
                boundary = log.TailAddress;

                log.Append("B"u8.ToArray());
                log.Prepare(2);
                // 崩溃
            }

            using (var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                var m2 = SessionManager.Create(vol.Fs, "kv", HangingResolution.DropTail, ("log", log2));
                m2.Initialize();
                m2.WaitForReady();

                log2.LastCommittedSeq.Should().Be(1, "丢尾：悬干被 Abort（回已确认水位）");
                log2.TailAddress.Should().Be(boundary, "悬干数据截断到提交边界");
                ReplayCount(log2).Should().Be(1, "丢尾后只剩已确认数据");
                m2.LastCommittedSeq.Should().Be(1);
                await m2.DisposeAsync();
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task CrashDuringMaterialize_StructureRecoveryTruncates_NoDangling()
    {
        // 窗口①：物化中（Prepare 前）崩溃——数据未进 meta，结构自身恢复截断；Manager 裁决无悬干
        var vol = new TestVolume();
        var settings = EntrySettings(vol);
        try
        {
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                var m = SessionManager.Create(vol.Fs, "kv", participants: ("log", log));
                m.Initialize();
                m.WaitForReady();
                using var s = m.OpenSession();
                s.Stage(() => log.Append("A"u8.ToArray()));
                await s.CommitAsync();   // seq=1 已提交边界

                log.Append("dangling"u8.ToArray());   // 模拟下一回合物化进行中——未 Prepare 即崩溃
            }

            using (var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                ReplayCount(log2).Should().Be(1, "未 Prepare 的物化尾不进 meta——结构恢复截断（Replay 到 committedOffset）");

                var m2 = SessionManager.Create(vol.Fs, "kv", participants: ("log", log2));   // seq=1 批的悬干形态同窗口③——前推
                m2.Initialize();
                m2.WaitForReady();
                log2.LastPreparedSeq.Should().Be(log2.LastCommittedSeq, "裁决后无悬干");
                m2.LastCommittedSeq.Should().Be(1, "前推后水位=已提交批 seq");
                await m2.DisposeAsync();
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task EndToEnd_MultiRoundAcrossInstances_DataCompleteSeqContinues()
    {
        // 端到端：三回合提交跨实例重建——数据完整 + seq 接续（域 seq 严格大于历史）
        var vol = new TestVolume();
        var settings = EntrySettings(vol);
        try
        {
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                var m = SessionManager.Create(vol.Fs, "kv", participants: ("log", log));
                m.Initialize();
                m.WaitForReady();
                using var s = m.OpenSession();
                foreach (var payload in new[] { "A", "B", "C" })
                {
                    var p = payload;
                    s.Stage(() => log.Append(System.Text.Encoding.UTF8.GetBytes(p)));
                    await s.CommitAsync();
                }
                m.LastCommittedSeq.Should().BeGreaterThanOrEqualTo(1);
            }

            using (var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                ReplayCount(log2).Should().Be(3, "三回合数据完整（meta 提交边界推进）");
                var m2 = SessionManager.Create(vol.Fs, "kv", participants: ("log", log2));
                m2.Initialize();
                m2.WaitForReady();
                using var s2 = m2.OpenSession();
                s2.Stage(() => log2.Append("D"u8.ToArray()));
                var seq = await s2.CommitAsync();
                seq.Should().Be(m2.LastCommittedSeq, "新回合 seq=当前域水位推进");
                ReplayCount(log2).Should().Be(4);
                await m2.DisposeAsync();
            }
        }
        finally { vol.Dispose(); }
    }
}
