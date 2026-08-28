using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>底层 EntryLog 直连诊断（TierWAL 失败溯源——不改底层，只验证行为）。</summary>
public class DiagLogBaseTests
{
    private static EntryLogSettings Settings(string name)
        => new(new StorageEngineOptions(name, 8L << 20, enableSegmentation: true, preallocateFile: false, deleteOnClose: true))
        {
            MetaPolicyKind = MetaPolicyKind.Managed,
            MetaOpaqueBytes = 16 * 1024,
            CommitInterval = TimeSpan.FromMilliseconds(-1),   // 禁后台时间循环
            MaxUnflushedBytes = long.MaxValue,
            MaxUnflushedCount = int.MaxValue,
        };

    [Fact]
    public async Task Diag_Append10_Commit_OpenCursorFromFirst()
    {
        using var vol = new TestVolume();
        var log = new EntryLog(vol.Fs, Settings("diag1"));
        log.Initialize();
        log.WaitForReady();

        var first = LogicalAddress.Empty;
        for (int i = 0; i < 10; i++)
        {
            var a = log.Append(System.Text.Encoding.UTF8.GetBytes($"entry-{i}"));
            if (i == 0) first = a;
        }
        await log.CommitAsync();
        var committed = log.CommittedOffset;
        var tail = log.TailAddress;

        // 1) 从头扫（OpenCursor(Empty, committed)）
        long c1 = 0;
        using (var cur = log.OpenCursor(LogicalAddress.Empty, committed))
            while (cur.MoveNext()) c1++;
        // 2) 从首条地址扫（断点续传）
        long c2 = 0;
        using (var cur = log.OpenCursor(first, committed))
            while (cur.MoveNext()) c2++;
        // 3) 从首条地址扫到 tail（物理尾）
        long c3 = 0;
        using (var cur = log.OpenCursor(first, tail))
            while (cur.MoveNext()) c3++;

        Assert.True(c1 == 10 && c2 == 10 && c3 == 10, $"c1={c1} c2={c2} c3={c3}");

        log.Dispose();
    }

    [Fact]
    public async Task Diag_Append3000_AsyncCursor_FromMiddle()
    {
        using var vol = new TestVolume();
        var log = new EntryLog(vol.Fs, Settings("diag2"));
        log.Initialize();
        log.WaitForReady();

        var (anchor1024, committed) = Append3000Sync(log);

        // 同步轨
        long c1 = 0;
        using (var cur = log.OpenCursor(anchor1024, committed))
            while (cur.MoveNext()) c1++;
        // 异步轨（锚点）
        await using (var cur = log.OpenCursor(anchor1024, committed))
        {
            bool r1 = await cur.MoveNextAsync();
            long c2 = r1 ? 1 : 0;
            while (await cur.MoveNextAsync()) c2++;
        }
        // 异步轨（段首——无 skip）
        long c3 = 0;
        await using (var cur = log.OpenCursor(LogicalAddress.Empty, committed))
            while (await cur.MoveNextAsync()) c3++;

        Assert.True(c1 == 3000 - 1023 && c3 == 3000, $"sync={c1} asyncHead={c3}");
        log.Dispose();
    }

    /// <summary>同步 append（ref struct 批不能在 async 方法——C#12）。</summary>
    private static (LogicalAddress Anchor, LogicalAddress Committed) Append3000Sync(EntryLog log)
    {
        LogicalAddress anchor = LogicalAddress.Empty;
        using (var batch = log.BeginAppendBatch())
        {
            for (int i = 0; i < 3000; i++)
            {
                var a = batch.Append(System.Text.Encoding.UTF8.GetBytes($"entry-{i}"));
                if (i == 1023) anchor = a;
            }
        }
        log.CommitAsync().AsTask().GetAwaiter().GetResult();   // 测试诊断——同步等待提交（Task 面）
        return (anchor, log.CommittedOffset);
    }
}
