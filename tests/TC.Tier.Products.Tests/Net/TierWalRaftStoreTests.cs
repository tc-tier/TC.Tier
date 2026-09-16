using System.Buffers;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Primitives;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// TierWalRaftStore 契约 smoke（IRaftStore × TierWAL 组合——产品接线面）：
/// 追加/持久化门/读面/冲突截断/重启恢复/快照水位与导入。
/// </summary>
public class TierWalRaftStoreTests
{
    private static List<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> Entries(params (long term, byte kind, int fill)[] specs)
    {
        var list = new List<(long, byte, ReadOnlyMemory<byte>)>(specs.Length);
        foreach (var (term, kind, fill) in specs)
        {
            var c = new byte[64];
            c.AsSpan().Fill((byte)fill);
            list.Add((term, kind, c));
        }
        return list;
    }

    private static NodeId Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new NodeId(bytes);
    }

    private static async Task<(TierWalRaftStore Store, TierWal Wal, IFileSystem Fs)> CreateAsync()
    {
        var fs = TierFs.New("memory:");
        var options = new TierWalOptions()
            .WithWalName("raft")
            .WithCommitInterval(TimeSpan.FromMilliseconds(-1))          // 显式提交形态（raft 同步点）
            .WithMaxUnflushedBytes(long.MaxValue)
            .WithMaxUnflushedCount(int.MaxValue);
        var wal = await new TierWalBuilder(fs, options).StartAsync();
        var store = new TierWalRaftStore(wal);
        await store.InitializeAsync();
        return (store, wal, fs);
    }

    [Fact]
    public async Task Append_PersistGate_Reads_RoundTrip()
    {
        var (store, wal, _) = await CreateAsync();
        (await store.AppendAsync(0, Entries((1, 1, 1), (1, 1, 2), (2, 2, 3), (2, 2, 4), (2, 2, 5)))).Should().Be(5);
        wal.PersistedIndex.Should().Be(0, "显式提交形态——未 Commit 前持久化水位不动");
        await store.WaitForPersistedAsync(5);
        wal.PersistedIndex.Should().Be(5);

        store.CountEntries(1, 100).Should().Be(5);
        store.CountEntries(1, 3).Should().Be(3);
        store.CountEntries(6, 100).Should().Be(0);
        store.LastLogIndex.Should().Be(5);
        store.LastLogTerm.Should().Be(2);

        store.TryGetEntry(3, out var term, out var kind, out var content).Should().BeTrue();
        (term, kind).Should().Be((2, 2));
        content.Span.ToArray().Should().OnlyContain(b => b == 3);
        store.TryGetEntry(6, out _, out _, out _).Should().BeFalse();

        (await store.ReadLogTermAsync(3)).Should().Be(2);
        (await store.PrevLogMatchesAsync(5, 2)).Should().BeTrue();
        (await store.PrevLogMatchesAsync(5, 1)).Should().BeFalse();
        (await store.PrevLogMatchesAsync(0, 0)).Should().BeTrue();

        var writer = new PooledBufferWriter();
        var terms = new long[5];
        (await store.WriteEntriesToAsync(1, 5, writer, terms)).Should().Be(5);
        terms.Should().Equal(1, 1, 2, 2, 2);

        // region 字节产物逐条回读（RaftEntriesRegion 读面）
        var region = writer.WrittenMemory;
        RaftEntriesRegion.TryReadCount(region, out var cnt, out var cursor).Should().BeTrue();
        cnt.Should().Be(5);
        var readTerms = new List<long>();
        for (var i = 0; i < cnt; i++)
        {
            RaftEntriesRegion.TryReadEntry(region, ref cursor, out var t, out var k, out var c).Should().BeTrue();
            readTerms.Add(t);
            c.ToArray().Should().OnlyContain(b => b == (byte)(i + 1));
            _ = k;
        }
        readTerms.Should().Equal(1, 1, 2, 2, 2);
        writer.Dispose();
    }

    [Fact]
    public async Task ConflictTruncate_PrevIndexLessThanTail_Rewrites()
    {
        var (store, wal, _) = await CreateAsync();
        await store.AppendAsync(0, Entries((1, 1, 1), (1, 1, 2), (1, 1, 3), (1, 1, 4)));
        await store.WaitForPersistedAsync(4);

        var tail = await store.AppendAsync(2, Entries((2, 2, 8), (2, 2, 9)));   // prev=2 < 尾 4——截 (2,4] 重写
        tail.Should().Be(4);
        await store.WaitForPersistedAsync(4);   // raft 纪律：读面=已持久化窗口（TierWal 读界=CommittedOffset）

        store.TryGetEntry(2, out var t2, out _, out var c2).Should().BeTrue();
        t2.Should().Be(1); c2.Span.ToArray().Should().OnlyContain(b => b == 2);      // prevIndex 处保留
        store.TryGetEntry(3, out var t3, out _, out var c3).Should().BeTrue();
        t3.Should().Be(2); c3.Span.ToArray().Should().OnlyContain(b => b == 8);      // 新分支自 prev+1 落位
        store.LastLogIndex.Should().Be(4);
        store.LastLogTerm.Should().Be(2);
    }

    [Fact]
    public async Task Recovery_Restarts_StateAndEntries()
    {
        var (store, wal, fs) = await CreateAsync();
        await store.WriteTermAndVoteAsync(7, Id(0xAB));
        await store.AppendAsync(0, Entries((7, 1, 1), (7, 1, 2), (7, 2, 3)));
        await store.UpdateAppliedIndexAsync(2);
        await store.WaitForPersistedAsync(3);
        await wal.DisposeAsync();

        var options = new TierWalOptions()
            .WithWalName("raft")
            .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
            .WithMaxUnflushedBytes(long.MaxValue)
            .WithMaxUnflushedCount(int.MaxValue);
        var wal2 = await new TierWalBuilder(fs, options).StartAsync();
        var store2 = new TierWalRaftStore(wal2);
        await store2.InitializeAsync();

        store2.Term.Should().Be(7);
        store2.VotedFor.Should().Be(Id(0xAB));
        store2.AppliedIndex.Should().Be(2);
        store2.LastLogIndex.Should().Be(3);
        store2.LastLogTerm.Should().Be(7);
        store2.TryGetEntry(2, out var t2, out _, out var c2).Should().BeTrue();
        t2.Should().Be(7); c2.Span.ToArray().Should().OnlyContain(b => b == 2);
        (await store2.ReadLogTermAsync(3)).Should().Be(7);
        await wal2.DisposeAsync();
    }

    [Fact]
    public async Task Snapshot_Watermark_ReadAndImport()
    {
        var (store, wal, _) = await CreateAsync();
        await store.AppendAsync(0, Entries((1, 1, 1), (1, 1, 2), (1, 1, 3), (1, 1, 4), (1, 1, 5)));
        await store.WaitForPersistedAsync(5);

        await store.TruncatePrefixToAsync(3);
        store.SnapshotIndex.Should().Be(3);
        store.CountEntries(1, 100).Should().Be(0, "快照区读面拒绝（≤ snapshotIndex）");
        store.CountEntries(4, 100).Should().Be(2);

        var snaps = new List<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)>();
        await foreach (var e in store.ReadSnapshotEntriesAsync(3)) snaps.Add(e);
        snaps.Select(s => s.Index).Should().Equal(1, 2, 3);
        snaps[0].Content.Span.ToArray().Should().OnlyContain(b => b == 1);

        // 导入（follower 侧）：清全部 → 重灌 [1..4] → snapshotIndex=4
        var imported = Entries((9, 1, 7), (9, 1, 8), (9, 2, 9), (9, 2, 10))
            .Select((e, i) => (Index: i + 1L, e.Term, e.Kind, e.Content)).ToList();
        await store.ImportSnapshotAsync(4, ToAsync(imported));
        store.SnapshotIndex.Should().Be(4);
        store.LastLogIndex.Should().Be(4);
        store.LastLogTerm.Should().Be(9);
        store.TryGetEntry(4, out var t4, out _, out var c4).Should().BeTrue();
        t4.Should().Be(9); c4.Span.ToArray().Should().OnlyContain(b => b == 10);
        await wal.DisposeAsync();
    }

    private static async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ToAsync(
        IReadOnlyList<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> source)
    {
        await Task.Yield();   // async 迭代器需含 await 点（CS1998）
        foreach (var e in source) yield return e;
    }
}
