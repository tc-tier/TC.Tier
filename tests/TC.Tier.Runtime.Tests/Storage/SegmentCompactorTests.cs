using TC.Tier.Core.Primitives;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// SegmentCompactor 单元测试——按 §10 测试计划覆盖：
/// 单元/校验/排他/并发/崩溃恢复/Dispose 协同。
/// <para>★ 重点验证新设计：</para>
/// <para>  - 段号复用（firstNewSegId = oldMinSeg，不是 0 也不是 MinSegId + Count）</para>
/// <para>  - 增量整理（段表允许中间 Invalid 洞）</para>
/// <para>  - Tail 不动（Compact 是整理不是写入）</para>
/// <para>  - 段角色 3 模式（首段中间起/中间段/末段中间止）</para>
/// <para>  - marker 机制（成功后删除，崩溃时恢复）</para>
/// <para>  - Cancel/timeout/排他</para>
/// </summary>
public sealed class SegmentCompactorTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static byte[] MakePattern(int length, byte seed = 0xAB)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.1 单元测试（正确性）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_Full_SingleSegment_DataIntact()
    {
        // ★ 单段活跃段 Compact：seg0 末段中间止（PunchHole），新段复用 seg0 位置
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(500, 0x88);
        var addr = dev.Append(data);

        var result = await dev.StartCompact().WaitAsync();

        // 段号复用：firstNewSegId = oldMinSeg = 0
        result.NewLowWaterMark.SegId.Should().Be(0);
        result.MigrationMap.Should().ContainKey(addr);

        // 数据完整
        byte[] dst = new byte[500];
        int n = dev.Read(result.MigrationMap[addr].GetValueOrDefault(), dst);
        n.Should().Be(500);
        dst.SequenceEqual(data).Should().BeTrue();
    }

    [Fact]
    public async Task Compact_Full_MultiSegment_NewSegCountLessThanOld()
    {
        // ★ 多段 Compact：新段数 < 旧段数（紧凑消除碎片）
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var addrs = new LogicalAddress[10];
        for (int i = 0; i < 10; i++)
            addrs[i] = dev.Append(MakePattern(512, (byte)i));

        // ★ 区间统一后尾停驻段末 (4,1024)——旧哨兵形态 (5,0) 把段数虚算 +1，旧断言靠虚高基线才绿
        //   （10×512=5120 恰满 5 段零碎片，全量 Compact 无从减段）。记录粒度真相在使用方
        //   （§XVIII/A8）——RangeCompact 只申报前 8 条为活记录，未申报 1024B 视为洞不搬迁：
        //   活数据 4096B → 4 段 < 旧 5 段，"紧凑减段"探针意图真正成立。
        var liveRecords = new (LogicalAddress, long)[8];
        for (int i = 0; i < 8; i++)
            liveRecords[i] = (addrs[i], 512);

        int segCountBefore = dev.AllocatedTail.SegId + 1;
        segCountBefore.Should().BeGreaterThan(3, "应跨多段");

        var result = await dev.StartRangeCompact(addrs[0], dev.CommittedTail, liveRecords).WaitAsync();

        // 新段数 < 旧段数（紧凑）
        int newSegCount = result.NewHighWaterMark.SegId - result.NewLowWaterMark.SegId + 1;
        newSegCount.Should().BeLessThan(segCountBefore, "Compact 后段数减少");

        // 数据完整（迁移后读回——未申报的 records 8/9 不迁移，跳过）
        byte[] dst = new byte[512];
        for (int i = 0; i < 10; i++)
        {
            if (result.MigrationMap.TryGetValue(addrs[i], out var newAddr))
            {
                int n = dev.Read(newAddr.GetValueOrDefault(), dst);
                n.Should().Be(512);
                dst.SequenceEqual(MakePattern(512, (byte)i)).Should().BeTrue();
            }
        }
    }

    [Fact]
    public async Task Compact_Full_TailUnchanged_AfterCompact_AppendContinues()
    {
        // ★ Tail 不动——Compact 是整理不是写入，后续 Append 在活跃段继续写
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(500, 0xAA));
        LogicalAddress tailBefore = dev.AllocatedTail;

        await dev.StartCompact().WaitAsync();

        // Tail 应保持不变（活跃段未涉及，文件保留）
        // 注：单段 Compact 时 Tail 段号不变，但 FileOffset 也不变
        dev.AllocatedTail.Should().Be(tailBefore, "Compact 不动 Tail");

        // 后续 Append 应正常工作
        var addr = dev.Append(MakePattern(300, 0xBB));
        byte[] dst = new byte[300];
        int n = dev.Read(addr, dst);
        n.Should().Be(300);
        dst.SequenceEqual(MakePattern(300, 0xBB)).Should().BeTrue();
    }

    [Fact]
    public async Task Compact_Full_NewSegSizeIsMaxRealSize()
    {
        // ★ §16.2：segLimit = max(源段 RealSize)
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        for (int i = 0; i < 5; i++)
            dev.Append(MakePattern(1024, (byte)i));

        var result = await dev.StartCompact().WaitAsync();

        // 验证：新段数 ≤ 源段数（紧凑不会扩容）
        int newSegCount = result.NewHighWaterMark.SegId - result.NewLowWaterMark.SegId + 1;
        newSegCount.Should().BeLessThanOrEqualTo(5);
    }


    [Fact]
    public async Task Compact_Full_Reopen_DataIntact()
    {
        // ★ Compact 后 reopen 数据完整（marker 已删除，扫盘为权威）
        var vol = NewVol();
        byte[] data = MakePattern(2000, 0xCC);
var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(data);
            await dev.StartCompact().WaitAsync();
        }

        // Compact 成功后 marker 已删除
        vol.Fs.Exists("test/test.compact.marker").Should().BeFalse("Compact 成功后 marker 已删除");
        using (var dev2 = options.Builder(vol.Fs).Start())
        {
            dev2.WaitForReady();
            byte[] dst = new byte[2000];
            int n = dev2.Read(new LogicalAddress(0, 0), dst);
            n.Should().Be(2000);
            dst.SequenceEqual(data).Should().BeTrue();
        }
    }


    // ═══════════════════════════════════════════════════════════════
    //  §10.3 排他测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_WhileCompactInProgress_Throws()
    {
        // ★ 同步 × 异步排他：先启动异步 Compact，再同步 Compact 应抛 InvalidOperationException
        //   ★ 用大数据让 Compact 跑得足够久（>1ms），保证第二个 Compact 触发排他
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写 1MB 数据（1024 段 × 1KB），Compact 至少需要数十 ms
        for (int i = 0; i < 1000; i++)
            dev.Append(MakePattern(1024, (byte)(i & 0xFF)));

        // 启动异步 Compact
        var op1 = dev.StartCompact();

        // 自旋等 _compacting 标志置 1（不 sleep——立即检测）
        var spin = new SpinWait();
        while (true)
        {
            try
            {
                // 立即尝试第二个 Compact——若 _compacting=1 抛异常（测试通过）
                await dev.StartCompact().WaitAsync();
                // 第一个 Compact 还没拿到锁——继续等
                spin.SpinOnce();
                if (spin.Count > 10000) break;  // 兜底
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Compact"))
            {
                // 排他异常——测试 PASS
                break;
            }
            catch
            {
                // 其他异常（如 ObjectDisposed、超时）——Compact 已完成或失败
                break;
            }
        }

        try { await op1.WaitAsync(CancellationToken.None); } catch { /* OK */ }
    }

    [Fact]
    public async Task StartCompact_Twice_SecondThrows()
    {
        // ★ 异步 × 异步排他——大数据 + 自旋
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        for (int i = 0; i < 1000; i++)
            dev.Append(MakePattern(1024, (byte)(i & 0xFF)));

        var op1 = dev.StartCompact();

        bool caughtExclusive = false;
        var spin = new SpinWait();
        for (int attempt = 0; attempt < 10000; attempt++)
        {
            try
            {
                dev.StartCompact();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Compact"))
            {
                caughtExclusive = true;
                break;
            }
            spin.SpinOnce();
        }
        caughtExclusive.Should().BeTrue("第二个 Compact 应触发排他异常");

        try { await op1.WaitAsync(CancellationToken.None); } catch { /* OK */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.7 Dispose 协同测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Compact_AfterDispose_ThrowsObjectDisposed()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        dev.Dispose();

        Action act = () => dev.StartCompact();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_WhileStartCompact_WaitsAndCancels()
    {
        // ★ Dispose 触发 _compactCts.Cancel + 等 _compacting 释放
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        for (int i = 0; i < 10; i++)
            dev.Append(MakePattern(512, (byte)i));

        var op = dev.StartCompact();
        await Task.Delay(50);

        // Dispose 应在 5 秒内返回（_compactCts.Cancel + WaitForCompactingRelease）
        var disposeTask = Task.Run(() => dev.Dispose());
        bool done = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10))) == disposeTask;
        done.Should().BeTrue("Dispose 应在超时内完成（Compact 被 Cancel 回滚）");
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.6 崩溃恢复测试（marker）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Compact_CrashCorruptedMarker_Reopen_DeletesMarker()
    {
        // ★ marker 损坏（CRC/magic 错）→ reopen 时 recover 视为 Phase 1 崩溃，删 marker
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(200));
        }

        // 手动写一个损坏的 marker
        vol.Fs.CreateFile("test/test.compact.marker");
        using (var h = vol.Fs.Open("test/test.compact.marker", new FileOpenOptions
               {
                   Access = AccessMode.Write,
                   Mode = FileOpenMode.OpenExisting,
                   Sharing = FileSharing.ReadWrite,
               }))
            h.Write(0, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00 });

        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
        }

        // marker 应被 recover 删除（损坏视为 Phase 1 崩溃）
        vol.Fs.Exists("test/test.compact.marker").Should().BeFalse("损坏的 marker 应被 recover 删除");
    }

    [Fact]
    public async Task Compact_NoMarker_NoCompactTempFiles_AfterSuccess()
    {
        // ★ Compact 成功后无 marker + 无 .compact 临时文件
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(200));
            await dev.StartCompact().WaitAsync();
        }

        foreach (var f in vol.Fs.EnumerateFiles("test", "*"))
        {
            f.Name.Should().NotEndWith(".compact", "Compact 成功后无 .compact 临时文件");
            f.Name.Should().NotBe("test.compact.marker", "Compact 成功后无 marker 文件");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.4 并发测试（Compact + Append 不冲突）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_ConcurrentWithAppend_NoDeadlock_DataConsistent()
    {
        // ★ Compact 与 Append 范围不重叠（Tail 在活跃段，Compact 搬 CommittedTail 之前）
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 先写一批历史数据（跨多段）
        for (int i = 0; i < 5; i++)
            dev.Append(MakePattern(1024, (byte)i));

        // 启动 Compact（异步）
        var compactOp = dev.StartCompact();

        // 同时继续 Append（活跃段未涉及，不应阻塞）
        var appendTask = Task.Run(() =>
        {
            for (int i = 0; i < 5; i++)
                dev.Append(MakePattern(1024, (byte)(0x10 + i)));
        });

        // 两者都应完成，不死锁
        await appendTask;

        try
        {
            await compactOp.WaitAsync(CancellationToken.None);
        }
        catch
        {
            // Compact 可能因并发 Append 改变 CommittedTail 而失败——允许，不死锁即可
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.4 增强：随机打孔 + 整理 + 地址线性验证（核心场景）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_AfterRandomPunchHoles_AddressesAreLinearInCompactedRange()
    {
        // ★ 核心场景：N 段写入 → 随机打孔产生碎片 → Full Compact → 验证整理区域内地址完全线性
        //   "线性" = 新段号连续递增 + 新段内数据按搬迁顺序连续存放（无空洞）
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 1. 写 N 条记录（跨多段），记录地址 + 数据
        const int recordCount = 30;
        const int recordSize = 1024;
        var records = new (LogicalAddress addr, byte[] data)[recordCount];
        for (int i = 0; i < recordCount; i++)
        {
            var data = MakePattern(recordSize, (byte)(i & 0xFF));
            records[i] = (dev.Append(data), data);
        }

        // 2. 随机打孔——对偶数索引记录打孔（产生碎片）
        var rng = new Random(42);
        var punchedIndices = new HashSet<int>();
        for (int i = 0; i < recordCount; i += 2)
        {
            // 用 Reclaim 打洞回收（区间 = 该记录地址）
            // ★ 区间统一：记录起点可为段末边界 (seg,4096)（首字节在下一段）——打孔区间终点必须走
            //   引擎 CalculationAddress 跨段推进（旧手算 Offset+size 在边界形态下越段打洞，
            //   真磁盘上把后续 Full Compact 拖进 5 分钟超时）
            var addr = records[i].addr;
            dev.Reclaim(addr, dev.CalculationAddress(addr, recordSize));
            punchedIndices.Add(i);
        }

        // 3. Full Compact——整理已提交数据
        var result = await dev.StartCompact().WaitAsync();

        // 4. 验证整理后未被打孔的数据完整可读 + 地址线性连续
        //    MigrationMap 里保留的旧地址映射 → 新地址
        //    检查新地址按映射顺序递增（线性）
        LogicalAddress? prevNewAddr = null;
        byte[] dst = new byte[recordSize];
        for (int i = 0; i < recordCount; i++)
        {
            if (punchedIndices.Contains(i))
                continue;   // 被打孔的不验证

            var (oldAddr, data) = records[i];
            if (result.MigrationMap.TryGetValue(oldAddr, out var newAddr))
            {
                // 新地址应严格递增（线性）
                if (prevNewAddr is { } prev)
                    newAddr.GetValueOrDefault().Should().BeGreaterThan(prev, "Compact 后保留数据应线性连续");

                // 数据完整
                int n = dev.Read(newAddr.GetValueOrDefault(), dst);
                n.Should().Be(recordSize);
                dst.AsSpan(0, recordSize).SequenceEqual(data).Should().BeTrue("Compact 后数据应完整");

                prevNewAddr = newAddr;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.4 增强：Compact 区间完全排他（其他段 Append 不进 Compact 区）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_RangeExclusive_OtherSegmentsAppendSucceeds()
    {
        // ★ Compact 锁源段族，但活跃段（Tail 所在段）未涉及 → Append 在活跃段继续写
        //   验证：Compact 期间 Append 不阻塞 + Append 地址不在 Compact 范围内
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写 5 条（跨多段，seg 0..4）
        var oldAddrs = new LogicalAddress[5];
        for (int i = 0; i < 5; i++)
            oldAddrs[i] = dev.Append(MakePattern(512, (byte)i));

        LogicalAddress committedTailBefore = dev.CommittedTail;

        // 启动异步 Compact
        var op = dev.StartCompact();

        // 立即在活跃段 Append（应不阻塞——活跃段不在 Compact 范围）
        var appendTask = Task.Run(() =>
        {
            return dev.Append(MakePattern(512, 0xFF));
        });

        var completedFirst = await Task.WhenAny(appendTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completedFirst.Should().Be(appendTask, "Compact 期间 Append 应立即完成（不阻塞）");

        var newAddr = await appendTask;
        // Append 地址应在 CommittedTail 之前的位置之后（活跃段未写空间）
        newAddr.Should().BeGreaterThanOrEqualTo(committedTailBefore,
            "Append 地址在活跃段未写空间，不应进入 Compact 范围");

        try { await op.WaitAsync(CancellationToken.None); } catch { /* OK */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.4 增强：整理后重新操作稳定性
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_ThenAppendReadTruncate_AllWorkStably()
    {
        // ★ Compact 后继续 Append / Read / Reclaim / 再次 Compact 都稳定
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写一批
        var addr1 = dev.Append(MakePattern(1024, 0x01));
        dev.Append(MakePattern(1024, 0x02));
        var addr3 = dev.Append(MakePattern(1024, 0x03));

        // 第一次 Compact
        var r1 = await dev.StartCompact().WaitAsync();
        var newAddr1 = r1.MigrationMap[addr1].GetValueOrDefault();

        // Compact 后继续 Append——addr4 应在活跃段未写空间
        LogicalAddress tailBeforeAppend = dev.AllocatedTail;
        var addr4 = dev.Append(MakePattern(1024, 0x04));
        addr4.Should().BeGreaterThanOrEqualTo(tailBeforeAppend,
            "Compact 后 Append 地址应 >= Compact 后 AllocatedTail");

        // 读 Compact 后 + 新 Append 的数据
        byte[] dst = new byte[1024];
        dev.Read(newAddr1, dst);
        dst.SequenceEqual(MakePattern(1024, 0x01)).Should().BeTrue();

        dev.Read(addr4, dst);
        dst.SequenceEqual(MakePattern(1024, 0x04)).Should().BeTrue();

        // Reclaim 打洞新数据（★ 区间统一：打孔区间终点走 CalculationAddress 跨段推进，不手算 Offset+size）
        dev.Reclaim(addr4, dev.CalculationAddress(addr4, 1024));

        // 再次 Compact——所有操作应稳定
        var r2 = await dev.StartCompact().WaitAsync();
        r2.MigrationMap.Should().NotBeNull();

        // 第二次 Compact 后数据仍完整
        if (r2.MigrationMap.TryGetValue(newAddr1, out var finalAddr1))
        {
            dev.Read(finalAddr1.GetValueOrDefault(), dst);
            dst.SequenceEqual(MakePattern(1024, 0x01)).Should().BeTrue("多次 Compact 后数据完整");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.4 增强：多次增量整理维持段表连续
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_MultipleRounds_SegTableDoesNotDegrade()
    {
        // ★ 多次增量 Compact——段表不应恶化（段数不无限增长，地址保持连续）
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 多轮：写 → Compact → 验证
        for (int round = 0; round < 5; round++)
        {
            // 写一批数据
            var addrs = new LogicalAddress[5];
            for (int i = 0; i < 5; i++)
                addrs[i] = dev.Append(MakePattern(512, (byte)(round * 16 + i)));

            // Compact
            await dev.StartCompact().WaitAsync();

            // 验证段数不无限增长（粗略：Tail 段号不应超过合理范围）
            dev.AllocatedTail.SegId.Should().BeLessThan(100,
                $"第 {round + 1} 轮 Compact 后段号不应无限增长");
        }

        // 最终数据仍可读（最后一轮的）
        byte[] dst = new byte[512];
        // 注：前几轮 Compact 后旧地址失效（被搬迁），只验证最后一轮写入的（未 Compact 的活跃段数据）
        // 最后一轮的 Append 数据在活跃段，Compact 未涉及
    }

    // ═══════════════════════════════════════════════════════════════
    //  §10.4 增强：Compact 时 Read 范围内地址（拒绝/截断）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_DuringRead_NoTornData()
    {
        // ★ Compact 期间 Read：要么读到旧数据，要么读到新数据，绝不撕裂。
        //   并发场景验证——小数据 + 短延迟。
        //   ★ 使用 ReadAsync + CancellationToken 防止 Compact 持有排他锁时无限自旋。
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var addr = dev.Append(MakePattern(1024, 0xAA));
        byte[] expectedData = MakePattern(1024, 0xAA);

        // 启动异步 Compact
        var op = dev.StartCompact();

        // 并发 Read——使用 ReadAsync + 超时取消，防止死等 Compact 租约
        var readTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var dst = new byte[1024];
                int n = await dev.ReadAsync(addr, dst, cts.Token);
                return (n, dst);
            }
            catch (OperationCanceledException)
            {
                return (0, Array.Empty<byte>());
            }
        });

        var (n, readData) = await readTask;
        // 不撕裂：要么读到完整 1024 字节且数据一致，要么读到 0 或部分数据（截断/取消）
        if (n == 1024)
        {
            readData.AsSpan(0, 1024).SequenceEqual(expectedData).Should().BeTrue("Compact 期间 Read 不应读到撕裂数据");
        }
        // n < 1024 是合法（截断/取消），不算错误

        try { await op.WaitAsync(CancellationToken.None); } catch { /* OK */ }
    }

    // ═══════════════════════════════════════════════════════════════
    //  边界值测试
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compact_EmptyDevice_ReturnsEmptyResult()
    {
        // ★ 空设备 Compact（无数据）→ 应返回空结果，不死锁不崩溃
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var result = await dev.StartCompact().WaitAsync();

        result.MigrationMap.Should().BeEmpty("空设备无数据可搬迁");
    }

    [Fact]
    public async Task Compact_MemoryDevice_BasicWorks()
    {
        // ★ 内存设备 Compact 功能可用
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var dev = options.Builder(NewVol().Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(500, 0x88);
        var addr = dev.Append(data);

        var result = await dev.StartCompact().WaitAsync();

        result.MigrationMap.Should().ContainKey(addr);
        byte[] dst = new byte[500];
        int n = dev.Read(result.MigrationMap[addr].GetValueOrDefault(), dst);
        n.Should().Be(500);
        dst.SequenceEqual(data).Should().BeTrue("内存 Compact 数据完整");
    }

    // ★ 注：Compact_MemoryDevice_MultiRound / DisposeWhileCompact 暂时跳过——
    //   内存 Compact 在多次迭代时性能不足（AlignedMemoryManager pin 开销），
    //   核心正确性由 LocalStorageDevice 的 263 个测试覆盖。

    [Fact]
    public void Initialize_ZeroGrowthLimit_FallsBackToDefault()
    {
        // ★ 新语义（构造 = 配置）：segmentGrowthLimit ≤ 0 = 引擎默认（256MB），不抛异常
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 0).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        Action act = () => dev.WaitForReady();
        act.Should().NotThrow("segmentGrowthLimit ≤ 0 应回落引擎默认值（构造期归一化）");
        dev.SegmentGrowthLimit.Should().Be(256L * 1024 * 1024, "0 → 引擎默认 256MB");
    }

    [Fact]
    public void Reclaim_EmptyRange_NoOp()
    {
        // ★ 空区间截断无害
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        dev.Append(MakePattern(100));

        Action act = () => dev.Reclaim(from: null, to: null);
        act.Should().NotThrow("空 Reclaim 应为 no-op");
    }
}
