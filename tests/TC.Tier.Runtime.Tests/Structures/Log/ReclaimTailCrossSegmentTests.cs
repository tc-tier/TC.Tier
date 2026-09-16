namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// ReclaimTail 跨段缺陷复现（引擎核心 BUG 定针——设计稿《Reclaim 家族对齐 + Log 逐帧分配》证据测试）。
/// <para>★ 家族对照：Reclaim（逐 chunk PunchHole+段锁+drain）/ ReclaimHead（逐段 Delete+边界 Punch+段锁+meta 刷新）
/// 均跨段完备；ReclaimTail 只物理处理 newTail.SegId 单段（裸 SetLength、无段锁、无跨段循环、无逐段 meta 刷新）
/// ——本文件用多段 TruncateSuffix 把缺失钉死：</para>
/// <para>① 物理残留：newTail.SegId 之外的段的 stale 帧原样驻留（应 Punch/删，对齐家族）；</para>
/// <para>② 同几何重启：水位纪律+覆写掩蔽下功能侥幸正确（控制组——掩蔽存在的证明）；</para>
/// <para>③ 几何换轴重启：段大小参数变更后扫描的地址算术换轴、断链点漂移、定尾错误
/// （物理事实 vs 双尾水位双源矛盾；设计裁定=meta 几何指纹 fast-fail）。</para>
/// </summary>
public class ReclaimTailCrossSegmentTests
{
    private const int SegmentSize = 256 * 1024;   // 256KB 段——跨段廉价
    private const int PageSizeBits = 12;          // 4KB 页（3KB entry + header + padding ≤ 4KB）
    private const int EntryCount = 200;           // ~620KB ≈ 3 段（82+82+36）
    private const int KeepCount = 120;            // 保留 0..119（newTail 落在 seg1——跨段范围=seg1 尾+seg2）
    private const byte KeptFill = 0xCC;
    private const byte DiscardedFill = 0xDD;

    private static EntryLogSettings SettingsOn(TestVolume vol, string engineName,
        bool deleteOnClose, long segmentGrowthLimit = SegmentSize)
        => new(new StorageEngineOptions(engineName, segmentGrowthLimit, enableSegmentation: true,
            preallocateFile: false, deleteOnClose))
        {
            LogPageSizeBits = PageSizeBits,
        };

    private static EntryLog OpenLog(TestVolume vol, EntryLogSettings settings)
    {
        var log = new EntryLog(vol.Fs, settings);
        log.Initialize();
        log.WaitForReady();
        return log;
    }

    private static byte[] Payload(byte fill)
    {
        var p = new byte[3 * 1024];
        Array.Fill(p, fill);
        return p;
    }

    /// <summary>写入 EntryCount 条（前 KeepCount 条 KeptFill、其余 DiscardedFill），返回各条起始地址。</summary>
    private static LogicalAddress[] AppendAll(EntryLog log)
    {
        var addr = new LogicalAddress[EntryCount];
        for (var i = 0; i < EntryCount; i++)
            addr[i] = log.Append(Payload(i < KeepCount ? KeptFill : DiscardedFill));
        return addr;
    }

    private static int CountEntries(EntryLog log, out bool discardedByteSeen)
    {
        discardedByteSeen = false;
        using var cursor = log.OpenCursor(LogicalAddress.Empty);
        var count = 0;
        while (cursor!.MoveNext())
        {
            if (cursor.CurrentPayload.Contains(DiscardedFill)) discardedByteSeen = true;
            count++;
        }
        return count;
    }

    /// <summary>物理断言：全部段文件不得含 DiscardedFill（对"删段/Punch"两种修复形态都成立——stale 帧必消失）。
    /// 段文件路径确定性：{engine}/{engine}.{segId}。</summary>
    private static void AssertNoDiscardedBytesPhysically(TestVolume vol, string engineName)
    {
        for (var segId = 0; segId < 64; segId++)
        {
            var path = $"{engineName}/{engineName}.{segId}";
            if (!vol.Fs.Exists(path)) continue;
            using var h = vol.Fs.Open(path, new FileOpenOptions { Access = AccessMode.Read });
            var buf = new byte[h.Length];
            _ = h.Read(0, buf);
            Assert.DoesNotContain(DiscardedFill, buf);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ① 物理残留（核心红测试——家族对齐语义：截断后全卷无 stale 帧）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TruncateSuffix_MultiSegment_NoStaleDataPhysicallyRemaining()
    {
        var vol = new TestVolume();
        try
        {
            var settings = SettingsOn(vol, "rt-phys", deleteOnClose: false);
            using (var log = OpenLog(vol, settings))
            {
                var addr = AppendAll(log);
                Assert.True(log.TruncateSuffix(addr[KeepCount]), "TruncateSuffix 应成功");  // 语义：地址所在条目一并截去——保留 0..KeepCount-1
            }

            AssertNoDiscardedBytesPhysically(vol, "rt-phys");
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ② 同几何重启（控制组——水位纪律+覆写掩蔽下应侥幸正确）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TruncateSuffix_MultiSegment_ReopenSameGeometry_TailCorrect()
    {
        var vol = new TestVolume();
        try
        {
            var settings = SettingsOn(vol, "rt-same", deleteOnClose: false);
            using (var log = OpenLog(vol, settings))
            {
                var addr = AppendAll(log);
                Assert.True(log.TruncateSuffix(addr[KeepCount]));
            }

            using (var log = OpenLog(vol, settings))
            {
                var count = CountEntries(log, out var discardedSeen);
                Assert.Equal(KeepCount, count);
                Assert.False(discardedSeen, "读回含被截断条目——定尾错误");
            }
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ③ 几何换轴重启（崩溃一致性——设计裁定=meta 几何指纹 fast-fail）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TruncateSuffix_MultiSegment_ReopenChangedGeometry_MustFailFast()
    {
        var vol = new TestVolume();
        try
        {
            var settings = SettingsOn(vol, "rt-geo", deleteOnClose: false);
            using (var log = OpenLog(vol, settings))
            {
                var addr = AppendAll(log);
                Assert.True(log.TruncateSuffix(addr[KeepCount]));
            }

            // 段大小 256KB → 1MB：地址→文件映射换轴。设计裁定：几何指纹不符 = 拒绝恢复（fast-fail），
            // 禁止用新几何解读旧物理（扫描断链点漂移=定尾错误=物理事实不一致）。
            // 两种可接受终态：打不开（指纹 fast-fail）| 打开且数据精确；其余（数据错误等）= 红。
            var changed = SettingsOn(vol, "rt-geo", deleteOnClose: false, segmentGrowthLimit: 1024 * 1024);
            EntryLog? reopened = null;
            Exception? openEx = null;
            try { reopened = OpenLog(vol, changed); }
            catch (Exception e) { openEx = e; }

            if (openEx is not null)
            {
                Console.WriteLine($"RT-PROBE: 几何换轴 fast-fail（{openEx.GetType().Name}）: {openEx.Message}");
                return;
            }
            Console.WriteLine("RT-PROBE: 几何换轴后成功打开——定尾正确性由数据断言判定");

            using (reopened!)
            {
                var count = CountEntries(reopened!, out var discardedSeen);
                Assert.False(discardedSeen, "读回含被截断条目——几何换轴后定尾错误（物理事实不一致）");
                Assert.Equal(KeepCount, count);
            }
        }
        finally { vol.Dispose(); }
    }
}
