using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// EntryLog 集成测试——页提交底层保证 / 单 entry 不跨页契约 / 双页交替正确性 / 提前提交 / 恢复 / Truncate / Replay。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewEntryLog 一步生命周期。</para>
/// </summary>
public class EntryLogTests
{
    // ═══════════════════════════════════════════════════════════════════
    // ★ 单 entry 不跨页契约（设计契约）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Append_EntryLargerThanPage_Throws()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 10);   // PageSize=1KB
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            int pageSize = log.PageSize;
            Assert.Throws<InvalidOperationException>(() =>
                log.Append(new byte[pageSize + 1]));
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Append_EntryExactlyPageSize_Succeeds()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 10);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            int payloadLen = log.PageSize - EntryLogHeaderCodec.StructSize;   // 16B header
            LogicalAddress addr = log.Append(new byte[payloadLen]);
            Assert.True(addr != LogicalAddress.Empty);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 底层页提交契约（恒成立，不可配置）——不注入任何策略，写满一页自动 commit
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void PageFlushed_AutoCommit_PageContract_NoPolicyInjected()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 10);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            LogicalAddress committedBefore = log.CommittedOffset;
            int pageSize = log.PageSize;
            int payloadLen = pageSize - EntryLogHeaderCodec.StructSize;   // 单 entry 占满一页

            log.Append(new byte[payloadLen]);
            LogicalAddress tailAfterFirst = log.TailAddress;
            log.Append(new byte[payloadLen]);   // 第二条触发跨页 → 第一页 flush + commit

            Assert.True(log.CommittedOffset >= tailAfterFirst,
                $"页提交契约失败：CommittedOffset={log.CommittedOffset} 应 ≥ 第一页末 {tailAfterFirst}（不注入策略也必须页提交）");
            Assert.True(log.CommittedOffset > committedBefore, "CommittedOffset 必须推进（页提交契约）");
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 双页交替正确性——连续写 N 页数据，OpenCursor 读回全部 entry 无丢失无错位
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AppendAsync_MultiPage_AllEntries_ReadBack()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 10);   // 1KB 页
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            int payloadLen = 100;
            int entries = 50;   // ≈ 6 页（多次双页交替）
            var payloads = new byte[entries][];
            for (int i = 0; i < entries; i++)
            {
                payloads[i] = new byte[payloadLen];
                Array.Fill(payloads[i], (byte)(i & 0xFF));
                await log.AppendAsync(payloads[i]);
            }
            await log.FlushAsync();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloadLen, cursor.CurrentEntryLength);
                Assert.False(cursor.CurrentIsMeta);
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Append_MultiPage_AllEntries_ReadBack()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 10);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            int entries = 30;
            var payloads = new byte[entries][];
            for (int i = 0; i < entries; i++)
            {
                payloads[i] = new byte[100];
                Array.Fill(payloads[i], (byte)(i + 1));
                log.Append(payloads[i]);
            }
            log.Flush();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 提前提交（注入 GroupCommitPolicy，页未满就推进 CommittedOffset）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EarlyCommit_InjectedPolicy_CommitsBeforePageFull()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);   // 16KB 页
        try
        {
            var policy = new GroupCommitPolicy
            {
                MaxUnflushedBytes = long.MaxValue,
                Interval = TimeSpan.FromMilliseconds(-1),
                MaxUnflushedCount = 5,   // 每 5 条提前提交
            };
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings, policy);

            LogicalAddress committed0 = log.CommittedOffset;
            for (int i = 0; i < 5; i++)
                await log.AppendAsync(new byte[100]);

            Assert.True(log.CommittedOffset > committed0,
                $"提前提交失败：CommittedOffset={log.CommittedOffset} 应 > 初始 {committed0}");
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ CommitAsync 手动提交
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CommitAsync_Manual_AdvancesCommittedOffset()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            LogicalAddress committed0 = log.CommittedOffset;
            log.Append(new byte[100]);
            await log.CommitAsync();

            Assert.True(log.CommittedOffset > committed0);
            Assert.Equal(log.TailAddress, log.CommittedOffset);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task WaitForCommitAsync_ReturnsAfterCommit()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            var policy = new GroupCommitPolicy
            {
                MaxUnflushedBytes = long.MaxValue,
                Interval = TimeSpan.FromMilliseconds(2),
                MaxUnflushedCount = int.MaxValue,
            };
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings, policy);

            LogicalAddress tail = log.Append(new byte[100]);
            // 单条不触发页满 → 后台循环只推到 FlushedTail（不调 FlushPage）→ 显式 CommitAsync 落盘
            await log.CommitAsync();
            await log.WaitForCommitAsync(tail);
            Assert.True(log.CommittedOffset >= tail);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ Truncate
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TruncateSuffix_RollsBackTail()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            log.Append(new byte[100]);
            LogicalAddress tail = log.TailAddress;
            log.Append(new byte[200]);
            Assert.True(log.TailAddress > tail);

            bool ok = log.TruncateSuffix(tail);
            Assert.True(ok);
            Assert.Equal(tail, log.TailAddress);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task TruncatePrefix_AdvancesBeginAddress()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            LogicalAddress a1 = log.Append(new byte[100]);
            LogicalAddress a2 = log.Append(new byte[100]);
            await log.FlushAsync();
            await log.CommitAsync();

            LogicalAddress beginBefore = log.BeginAddress;
            log.TruncatePrefix(a2);   // 删 [0, a2)
            Assert.True(log.BeginAddress >= a2);
            Assert.True(log.BeginAddress > beginBefore || beginBefore == LogicalAddress.Empty);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复——写数据 → Dispose → 新实例同卷 → 读回
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Recover_NewInstance_ReadsBackCommittedData()
    {
        var vol = new TestVolume();
        try
        {
            var settings = TestLogSettingsFactory.EntryOn(vol, "entry-recover", logPageSizeBits: 14, deleteOnClose: false);
            int entries = 20;
            var payloads = new byte[entries][];
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                for (int i = 0; i < entries; i++)
                {
                    payloads[i] = new byte[100];
                    Array.Fill(payloads[i], (byte)(i + 1));
                    log.Append(payloads[i]);
                }
                await log.FlushAsync();
                await log.CommitAsync();   // 末页提交落盘
            }

            // 新实例同卷重开（扫盘恢复），读回全部已提交 entry
            var settings2 = TestLogSettingsFactory.EntryOn(vol, "entry-recover", logPageSizeBits: 14, deleteOnClose: true);
            using var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings2);
            using var cursor = log2.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ DIO 模式（NoBuffering 请求）——覆盖 PageFrame 扇区对齐路径
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void DIO_Append_MultiPage_AllEntries_ReadBack()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntryDIO(logPageSizeBits: 10);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            int entries = 30;
            var payloads = new byte[entries][];
            for (int i = 0; i < entries; i++)
            {
                payloads[i] = new byte[100];
                Array.Fill(payloads[i], (byte)(i + 1));
                log.Append(payloads[i]);
            }
            log.Flush();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task DIO_Append_Recover_NewInstance_ReadsBack()
    {
        var vol = new TestVolume();
        try
        {
            var settings = TestLogSettingsFactory.EntryOn(vol, "entry-dio-recover", logPageSizeBits: 12,
                deleteOnClose: false, hints: FileOpenHints.NoBuffering);
            int entries = 40;
            var payloads = new byte[entries][];
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                for (int i = 0; i < entries; i++)
                {
                    payloads[i] = new byte[100];
                    Array.Fill(payloads[i], (byte)(i + 1));
                    log.Append(payloads[i]);
                }
                await log.FlushAsync();
                await log.CommitAsync();
            }

            var settings2 = TestLogSettingsFactory.EntryOn(vol, "entry-dio-recover", logPageSizeBits: 12,
                deleteOnClose: true, hints: FileOpenHints.NoBuffering);
            using var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings2);
            using var cursor = log2.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task ManagedMeta_Preallocate_Recover_ReadsBackAll()
    {
        // ★ 预分配 + Managed meta + 恢复：meta Load 是最高权威恢复源，不受预分配段文件大小影响
        //   （Managed 模式经本轮修复从 NotImplementedException 桩变为可用——本测试即其首个活体验证）。
        var vol = new TestVolume();
        try
        {
            var settings = TestLogSettingsFactory.EntryOn(vol, "entry-prealloc-meta", logPageSizeBits: 12,
                metaKind: MetaPolicyKind.Managed, deleteOnClose: false, preallocate: true, payloadCapacity: 64);
            int entries = 30;
            var payloads = new byte[entries][];
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                for (int i = 0; i < entries; i++)
                {
                    payloads[i] = new byte[100];
                    Array.Fill(payloads[i], (byte)(i + 1));
                    log.Append(payloads[i]);
                }
                await log.FlushAsync();
                await log.CommitAsync();   // commit 时 meta 持久化水位
            }

            var settings2 = TestLogSettingsFactory.EntryOn(vol, "entry-prealloc-meta", logPageSizeBits: 12,
                metaKind: MetaPolicyKind.Managed, deleteOnClose: true, preallocate: true, payloadCapacity: 64);
            using var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings2);
            using var cursor = log2.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ Replay（WAL 核心：只重放已 commit 的 entry）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Replay_AllCommitted_Entries_ReadBack()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            int entries = 30;
            var payloads = new byte[entries][];
            for (int i = 0; i < entries; i++)
            {
                payloads[i] = new byte[64];
                Array.Fill(payloads[i], (byte)(i + 1));
                log.Append(payloads[i]);
            }
            await log.CommitAsync();

            var got = new List<byte[]>();
            long replayed = log.Replay((ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress addr) =>
            {
                got.Add(payload.ToArray());
                Assert.False(isMeta);
            });

            Assert.Equal(entries, replayed);
            Assert.Equal(entries, got.Count);
            for (int i = 0; i < entries; i++)
                Assert.Equal(payloads[i], got[i]);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Replay_FromMiddleAddress_StartsAtThatEntry()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            var addrs = new LogicalAddress[10];
            for (int i = 0; i < 10; i++)
                addrs[i] = log.Append(new byte[] { (byte)i });
            await log.CommitAsync();

            LogicalAddress from = addrs[5];
            int count = 0;
            log.Replay(from, (ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress addr) =>
            {
                Assert.Equal((byte)(5 + count), payload[0]);
                count++;
            });
            Assert.Equal(5, count);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Replay_DoesNotReadUncommittedEntries()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings, new GroupCommitPolicy
            {
                Interval = TimeSpan.FromMilliseconds(-1),   // 禁用全部自动提交（手动场景）
                MaxUnflushedBytes = long.MaxValue,
                MaxUnflushedCount = int.MaxValue,
            });

            log.Append(new byte[] { 1 });
            log.Append(new byte[] { 2 });
            // 不 commit！末页未满 → CommittedOffset 仍 = 初始
            int count = 0;
            log.Replay((ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress addr) => count++);
            Assert.Equal(0, count);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task ReplayAsync_AllCommitted_ReadsBack()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            var payloads = new byte[20][];
            for (int i = 0; i < 20; i++)
            {
                payloads[i] = new byte[32];
                Array.Fill(payloads[i], (byte)(i + 1));
                log.Append(payloads[i]);
            }
            await log.CommitAsync();

            var got = new List<byte[]>();
            long replayed = await log.ReplayAsync((ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress addr, CancellationToken ct) =>
            {
                got.Add(payload.ToArray());
                return ValueTask.CompletedTask;
            });
            Assert.Equal(20, replayed);
            Assert.Equal(20, got.Count);
            for (int i = 0; i < 20; i++)
                Assert.Equal(payloads[i], got[i]);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task OpenReplayCursor_ReadsUpToCommittedOffset()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            for (int i = 0; i < 10; i++)
                log.Append(new byte[] { (byte)i });
            await log.CommitAsync();
            LogicalAddress committed = log.CommittedOffset;

            log.Append(new byte[] { 99 });    // commit 后追加（不 commit）——不应被重放
            log.Append(new byte[] { 100 });

            using var cursor = log.OpenCursor(LogicalAddress.Empty, committed);
            Assert.NotNull(cursor);
            int count = 0;
            while (cursor!.MoveNext())
            {
                Assert.True(cursor.CurrentAddress <= committed);
                count++;
            }
            Assert.Equal(10, count);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Replay_VerifyCrc_Default_ReadsBackNormally()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            for (int i = 0; i < 20; i++)
                log.Append(new byte[] { (byte)i });
            await log.CommitAsync();

            int count = 0;
            log.Replay((ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress addr) => count++);
            Assert.Equal(20, count);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Replay_VerifyCrc_True_ValidatesIntegrity()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            for (int i = 0; i < 20; i++)
                log.Append(new byte[] { (byte)i });
            await log.CommitAsync();

            int count = 0;
            log.Replay(LogicalAddress.Empty, (ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress addr) => count++, verifyCrc: true);
            Assert.Equal(20, count);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ opaque meta 公共入口契约（用户裁定：写侧拦截——禁用即报错，不静默吞）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SetOpaqueMeta_Disabled_Throws_Clearly()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14,
            metaKind: MetaPolicyKind.Disabled);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            var act = () => log.SetOpaqueMeta(new byte[] { 1, 2, 3 });
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*MetaPolicyKind=Disabled*opaque 登记被拒*",
                    "配置禁用时写侧必须明确报错——静默 no-op 会让调用方以为写成功");
            // 读侧契约：Disabled 恒 Empty（空=无数据，不抛）
            log.ReadOpaqueMeta().IsEmpty.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task SetOpaqueMeta_RidesWithWatermarkCommit_Transport()
    {
        // ★ 搭车语义（用户裁定）：SetOpaqueMeta 只登记（stage），随水位线提交原子落盘——
        //   登记后由【数据路径的内部提交链】触发落盘（无 opaque 通道），opaque 仍在块里。
        //   回归旧缺陷：内部提交曾把外部 opaque 静默冲掉。
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14,
            metaKind: MetaPolicyKind.Transport, payloadCapacity: 64);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            var opaque = new byte[] { 0xAB, 0xCD, 0xEF };
            log.SetOpaqueMeta(opaque);
            // 登记未提交：读侧是 read-last-committed（此刻 Empty）
            log.ReadOpaqueMeta().IsEmpty.Should().BeTrue("未落盘前读到的是上一已提交值");

            // 数据路径提交（AppendMeta 无 opaque 参数）——水位 + opaque 同块原子落盘
            log.Append(new byte[100]);
            await log.CommitAsync();
            log.ReadOpaqueMeta().ToArray().Should().Equal(opaque, "opaque 搭水位线车随内部提交链落盘");

            // 二次提交（无重新登记）——opaque 持续随行
            log.Append(new byte[50]);
            await log.CommitAsync();
            log.ReadOpaqueMeta().ToArray().Should().Equal(opaque, "opaque 持续随每次水位提交");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task SetOpaqueMeta_Only_NoData_CommitStillCompleteBlock()
    {
        // ★ 纯 opaque 提交（用户裁定语义）：零数据写入时显式 CommitAsync——数据为空，
        //   但 meta 块完整自洽（当前水位原样携带 + opaque 同块同 CRC）。
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14,
            metaKind: MetaPolicyKind.Transport, payloadCapacity: 64);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            var opaque = new byte[] { 0x11, 0x22, 0x33 };
            log.SetOpaqueMeta(opaque);
            LogicalAddress dataTail = log.TailAddress;   // 数据尾（零数据 = 初始值）
            await log.CommitAsync();   // 零数据——独立刷盘点（唯一合法形态）

            log.ReadOpaqueMeta().ToArray().Should().Equal(opaque, "纯 opaque 提交：块完整，opaque 可读");
            log.CommittedOffset.Should().Be(dataTail, "零数据提交：水位 = 数据尾原样携带（meta 完整）");
            // Transport 嵌入式：meta 块本身作为 entry 写进 log 流——Tail 含 meta entry，> 数据尾
            log.TailAddress.Should().BeGreaterThan(dataTail, "meta entry 写入推进 log 尾");
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 3a 外部隔离（注入 MetadataMetaTransport——meta 托管 VersionedMetadata 版本链）
    //   用户裁定：外部独有 meta 存储优先选 VersionedMetadata，避免独自写盘一致性 + 自搭 2PC。
    // ═══════════════════════════════════════════════════════════════════

    private static MetadataMetaTransport NewExternalMeta(TestVolume vol, string engineName, bool deleteOnClose)
        => new(vol.Fs, new VersionedMetadataSettings(
            new StorageEngineOptions(engineName, 1L << 20, enableSegmentation: false)
                .WithDeleteOnClose(deleteOnClose))
        {
            PayloadSize = 4096,   // ≥ 12 + LogMetaPayload + MetaOpaqueBytes + 4
        });

    [Fact]
    public async Task ExternalMeta_MetadataTransport_RecoverRoundtrip()
    {
        var vol = new TestVolume();
        try
        {
            // 实例 1：EntryLog + 外部隔离 meta（Transport 注入 MetadataMetaTransport）
            byte[][] payloads;
            using (var ext = NewExternalMeta(vol, "entry-ext-meta", deleteOnClose: false))
            {
                var settings = TestLogSettingsFactory.EntryOn(vol, "entry-ext",
                    logPageSizeBits: 14, metaKind: MetaPolicyKind.Transport, deleteOnClose: false, payloadCapacity: 64);
                using var log = new EntryLog(vol.Fs, settings, metaTransport: ext);
                log.Initialize();
                log.WaitForReady();
                payloads = new byte[10][];
                for (int i = 0; i < 10; i++)
                {
                    payloads[i] = new byte[64];
                    Array.Fill(payloads[i], (byte)(i + 1));
                    log.Append(payloads[i]);
                }
                await log.CommitAsync();   // 水位块经 ext 落到 VersionedMetadata
            }

            // 实例 2：同卷重开（新适配器读同引擎）——恢复走 meta（外部介质 O(1)）
            using (var ext2 = NewExternalMeta(vol, "entry-ext-meta", deleteOnClose: true))
            {
                var settings2 = TestLogSettingsFactory.EntryOn(vol, "entry-ext",
                    logPageSizeBits: 14, metaKind: MetaPolicyKind.Transport, payloadCapacity: 64);
                using var log3 = new EntryLog(vol.Fs, settings2, metaTransport: ext2);
                log3.Initialize();
                log3.WaitForReady();
                using var cursor = log3.OpenCursor(LogicalAddress.Empty);
                Assert.NotNull(cursor);
                int idx = 0;
                while (cursor!.MoveNext())
                {
                    Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                    idx++;
                }
                Assert.Equal(10, idx);
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task ExternalMeta_PureOpaque_ZeroMainstreamWatermarkImpact()
    {
        var vol = new TestVolume();
        try
        {
            using var ext = NewExternalMeta(vol, "entry-ext-meta2", deleteOnClose: true);
            var settings = TestLogSettingsFactory.EntryOn(vol, "entry-ext2",
                logPageSizeBits: 14, metaKind: MetaPolicyKind.Transport, payloadCapacity: 64);
            using var log = new EntryLog(vol.Fs, settings, metaTransport: ext);
            log.Initialize();
            log.WaitForReady();

            LogicalAddress tailBefore = log.TailAddress;
            LogicalAddress committedBefore = log.CommittedOffset;
            var opaque = new byte[] { 0x0A, 0x0B, 0x0C };
            log.SetOpaqueMeta(opaque);
            await log.CommitAsync();   // 纯 opaque 提交——外部隔离：主流零接触

            // ★ 3a 水位语义：主流水位字节级零变动（meta 全程在外部介质）
            log.TailAddress.Should().Be(tailBefore, "外部隔离：meta 不进主流——Tail 不动");
            log.CommittedOffset.Should().Be(committedBefore, "外部隔离：CommittedOffset 不动");
            log.ReadOpaqueMeta().ToArray().Should().Equal(opaque, "opaque 经 VersionedMetadata 外部介质往返");
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ Transport meta（宿主流嵌入）+ 提前提交——防递归栈溢出（回归测试）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Append_EmbeddedMeta_SingleForceCommit_DoesNotStackOverflow()
    {
        // Transport meta（宿主流嵌入）+ 单条强制提交：OnAppended 对 isMeta 跳过提前提交，递归终止
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14,
            metaKind: MetaPolicyKind.Transport, payloadCapacity: 64);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings, new GroupCommitPolicy
            {
                MaxUnflushedBytes = long.MaxValue,
                MaxUnflushedCount = 0,             // ★ 单条强制：每条 Append 立即提交
                Interval = TimeSpan.FromMilliseconds(-1),
            });

            for (int i = 0; i < 50; i++)
                log.Append(new byte[] { (byte)i });

            Assert.True(log.CommittedOffset > LogicalAddress.Empty,
                $"Transport meta 单条强制提交应推进 CommittedOffset，实际={log.CommittedOffset}");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task AppendAsync_EmbeddedMeta_DefaultGc_DoesNotStackOverflow()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 14,
            metaKind: MetaPolicyKind.Transport, payloadCapacity: 64);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);   // 默认 gc

            for (int i = 0; i < 100; i++)
                await log.AppendAsync(new byte[] { (byte)i });
            await log.CommitAsync();

            Assert.True(log.CommittedOffset > LogicalAddress.Empty);
        }
        finally { vol.Dispose(); }
    }
}
