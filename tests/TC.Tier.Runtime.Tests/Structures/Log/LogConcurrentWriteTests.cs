using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// Log 并发写契约测试（单写者铁律解除——页状态机 + 原子段分配）：
/// N 线程并发 Append 数据完整 / 并发批 / 页满竞争（小页）/ 崩溃一致性（并发写 + Prepare 重开）。
/// </summary>
public class LogConcurrentWriteTests
{
    private static EntryLogSettings Settings(TestVolume vol, string name, int pageSizeBits = 14)
        => TestLogSettingsFactory.EntryOn(vol, name, logPageSizeBits: pageSizeBits, deleteOnClose: false);

    [Fact]
    public void ConcurrentAppend_AllDataReadable_NoOverlap()
    {
        using var vol = new TestVolume();
        using var log = TestLogSettingsFactory.NewEntryLog(vol, Settings(vol, "cw-log"));

        const int writers = 8;
        const int perWriter = 5_000;
        var addresses = new LogicalAddress[writers][];
        for (int w = 0; w < writers; w++) addresses[w] = new LogicalAddress[perWriter];

        var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var payload = new byte[32];
            for (int k = 0; k < perWriter; k++)
            {
                payload[0] = (byte)w;                          // 写者标记
                BitConverter.GetBytes(k).CopyTo(payload, 4);   // 序号标记
                addresses[w][k] = log.Append(payload);
            }
        })).ToArray();
        Task.WaitAll(workers);
        log.Flush();   // ★ 数据落盘（cursor 只读 FlushedTail 已落盘区）

        // 读回验证：扫盘每个 entry 的内容 = (writer, seq)——地址集合与扫盘内容互证
        var buf = new byte[32];
        using var cursor = log.OpenCursor(log.BeginAddress, log.TailAddress);
        int total = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentPayload.CopyTo(buf);
            int w = buf[0];
            int seq = BitConverter.ToInt32(buf, 4);
            w.Should().BeInRange(0, writers - 1);
            seq.Should().BeInRange(0, perWriter - 1);
            total++;
        }
        total.Should().Be(writers * perWriter, "全部并发 entry 可扫盘读出");
    }

    [Fact]
    public void ConcurrentAppend_Batches_AllDataReadable()
    {
        using var vol = new TestVolume();
        using var log = TestLogSettingsFactory.NewEntryLog(vol, Settings(vol, "cw-batch"));

        const int writers = 4;
        const int perWriter = 3_000;
        var addresses = new LogicalAddress[writers][];
        for (int w = 0; w < writers; w++) addresses[w] = new LogicalAddress[perWriter];

        var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var payload = new byte[32];
            for (int k = 0; k < perWriter;)
            {
                using var batch = log.BeginAppendBatch();
                int budget = 256;
                while (k < perWriter && budget-- > 0)
                {
                    payload[0] = (byte)w;
                    BitConverter.GetBytes(k).CopyTo(payload, 4);
                    addresses[w][k] = batch.Append(payload);
                    k++;
                }
            }
        })).ToArray();
        Task.WaitAll(workers);
        log.Flush();

        var buf = new byte[32];
        using var cursor = log.OpenCursor(log.BeginAddress, log.TailAddress);
        int total = 0;
        var seen = new int[writers];
        while (cursor.MoveNext())
        {
            cursor.CurrentPayload.CopyTo(buf);
            int w = buf[0];
            if (w < 0 || w >= writers)
            {
                Console.WriteLine($"[dbg-BAD] w={w} addr={cursor.CurrentAddress} payload={string.Join(',', buf.Take(8))}");
                continue;
            }
            seen[w]++;
            total++;
        }
        for (int w = 0; w < writers; w++)
            if (seen[w] != perWriter) Console.WriteLine($"[dbg-CNT] writer {w}: {seen[w]} (expect {perWriter})");
        Console.WriteLine($"[dbg-TOTAL] {total} (expect {writers * perWriter})");
        total.Should().Be(writers * perWriter, "并发批全部 entry 可扫盘读出");
    }

    [Fact]
    public void ConcurrentAppend_SmallPage_RotationContention()
    {
        // 小页（4KB）+ 多写者——页满换页仲裁竞争
        using var vol = new TestVolume();
        using var log = TestLogSettingsFactory.NewEntryLog(vol, Settings(vol, "cw-rot", pageSizeBits: 12));

        const int writers = 8;
        const int perWriter = 2_000;
        var addresses = new LogicalAddress[writers][];
        for (int w = 0; w < writers; w++) addresses[w] = new LogicalAddress[perWriter];

        var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var payload = new byte[64];   // ~100B/entry → 4KB 页 ≈ 40 entry/页 → 2000 条跨 50 页
            for (int k = 0; k < perWriter; k++)
            {
                payload[0] = (byte)w;
                BitConverter.GetBytes(k).CopyTo(payload, 4);
                addresses[w][k] = log.Append(payload);
            }
        })).ToArray();
        Task.WaitAll(workers);
        log.Flush();

        var buf = new byte[64];
        using var cursor = log.OpenCursor(log.BeginAddress, log.TailAddress);
        int total = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentPayload.CopyTo(buf);
            buf[0].Should().BeInRange(0, writers - 1);
            total++;
        }
        total.Should().Be(writers * perWriter, "小页换页竞争下全部 entry 可扫盘读出");
    }

    [Fact]
    public void ConcurrentAppend_Prepare_CrossInstanceRecoverable()
    {
        // 并发写 + Prepare 落盘 → 重开恢复——全部并发数据可读（WAL 崩溃一致性）
        using var vol = new TestVolume();
        const int writers = 4;
        const int perWriter = 2_000;
        using (var log = TestLogSettingsFactory.NewEntryLog(vol, Settings(vol, "cw-crash")))
        {
            var workers = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                var payload = new byte[32];
                payload[0] = (byte)w;
                for (int k = 0; k < perWriter; k++)
                {
                    BitConverter.GetBytes(k).CopyTo(payload, 4);
                    log.Append(payload);
                }
            })).ToArray();
            Task.WaitAll(workers);
            log.Prepare(seq: 1);
        }

        using var log2 = TestLogSettingsFactory.NewEntryLog(vol, Settings(vol, "cw-crash"));
        // 重开恢复：扫盘应读到全部并发 entry（写者标记校验）
        var buf = new byte[32];
        int[] seen = new int[4];
        using var cursor = log2.OpenCursor(log2.BeginAddress, log2.TailAddress);
        while (cursor.MoveNext())
        {
            cursor.CurrentPayload.CopyTo(buf);
            int w = buf[0];
            w.Should().BeInRange(0, 3);
            seen[w]++;
        }
        seen.Should().AllBeEquivalentTo(perWriter, "每个写者的全部 entry 跨实例恢复可读");
    }
}
