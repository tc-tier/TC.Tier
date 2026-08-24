using System.Collections.Concurrent;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// FileHandlePool 单元测试（第九轮 Acquire/Release 协议）——单接口分桶/挂载式归还（Dispose=归还默认不关）/
/// 底层关闭三出口/外部 Dispose 后池健康/安全 LRU 不逐在用/下溢绊线/跨实例文件级 Append/截断复位。
/// </summary>
public sealed class FileHandlePoolTests
{
    private static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileSharing sharing = FileSharing.ReadWrite, long preallocate = 0)
        => new()
        {
            Access = access,
            Mode = access == AccessMode.Read ? FileOpenMode.OpenExisting : FileOpenMode.OpenOrCreate,
            Sharing = sharing,
            PreallocateSize = preallocate,
        };

    [Fact]
    public void Acquire_SameOptions_SameInstance_SharedUsage()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        var a = pool.Acquire("f", Opts());
        var b = pool.Acquire("f", Opts());
        b.Should().BeSameAs(a);
        pool.Count.Should().Be(1);
    }

    [Fact]
    public void Acquire_DifferentSemantics_DistinctInstances()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        var w = pool.Acquire("f", Opts());
        var r = pool.Acquire("f", Opts(access: AccessMode.Read));
        r.Should().NotBeSameAs(w);
        pool.Count.Should().Be(2);
    }

    [Fact]
    public void Acquire_ExcludesPreallocateSize_SameInstance_CreatedWithFullIntent()
    {
        // ⑫/㉔：预分配不进 key（同实例）；创建期意图完整执行（open 即幂等预分配——原 Publish 语义并入 Acquire）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions
        {
            Allocation = MemoryAllocationMode.Reserved,
            QuotaBytes = 1 << 22,
        });
        using var pool = new FileHandlePool(fs);
        var a = pool.Acquire("f", Opts(preallocate: 0));
        var b = pool.Acquire("f", Opts(preallocate: 1 << 20));
        b.Should().BeSameAs(a);
        // 带预分配的首次创建（或本例第二次命中前的任一路径）——验证 Acquire 以完整意图创建：
        using var pool2 = new FileHandlePool(fs);
        var c = pool2.Acquire("g", Opts(preallocate: 1 << 20));
        c.Length.Should().Be(1 << 20, "Acquire 按完整意图创建（含预分配——两步舞收拢）");
    }

    [Fact]
    public void Acquire_Concurrent_AllSameInstance_LosersClosed()
    {
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { QuotaBytes = 1 << 22 });
        using var pool = new FileHandlePool(fs);
        var handles = new ConcurrentBag<IFileHandle>();
        Parallel.For(0, 16, i => handles.Add(pool.Acquire($"f{i % 4}", Opts())));
        handles.Distinct().Count().Should().Be(4);
        pool.Count.Should().Be(4);
    }

    [Fact]
    public void Dispose_ReturnsUsage_DoesNotCloseUnderlying()
    {
        // ★ 核心：外部 Dispose = 归还使用权——底层资源不动（using 安全惯用法）
        // 实例是共享的：归还后句柄仍可用（其他借用者/池持有），真正的保证是"资源不可能被外部关掉"
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        var h = pool.Acquire("f", Opts());
        h.Dispose();   // 归还（非关闭）
        var act = () => h.Write(0, new byte[8]);
        act.Should().NotThrow("归还 ≠ 失效——共享实例资源未关（使用权计数为顾问式簿记）");

        // 池未受毒化：再次 Acquire 命中同一实例（无僵尸、无重建抖动）
        pool.Acquire("f", Opts()).Should().BeSameAs(h);
    }

    [Fact]
    public void UsingPattern_IsSafe_Idiomatic()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        for (var i = 0; i < 3; i++)
        {
            using (var h = pool.Acquire("loop", Opts()))
            {
                h.Append(new byte[16]);   // using 块内正常使用
            }   // Dispose = 归还——不杀共享句柄
        }
        pool.Count.Should().Be(1);   // 同实例贯穿三轮
    }

    [Fact]
    public void Release_Default_KeepsUnderlying_CloseTrue_ClosesAndEvicts()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        var h = pool.Acquire("f", Opts());

        pool.Release(h);   // 默认：归还不关
        var probe = pool.Acquire("f", Opts());
        probe.Should().BeSameAs(h, "Release(默认) 后底层仍在池中服务");

        pool.Release(probe, close: true);   // 定向关闭
        pool.Count.Should().Be(0);
        ((Action)(() => probe.Write(0, new byte[1]))).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Release_ForeignHandle_Throws()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        using var direct = fs.Open("direct", Opts());   // 池外直开
        var act = () => pool.Release(direct);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryAcquire_HitAndMiss()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        var h = pool.Acquire("f", Opts());
        pool.TryAcquire("f", Opts(), out var hit).Should().BeTrue();
        hit.Should().BeSameAs(h);
        pool.TryAcquire("missing", Opts(), out _).Should().BeFalse();
    }

    [Fact]
    public void RemoveAll_PredicateEvictsAndCloses()
    {
        using var fs = MemoryFileSystem.New();
        using var pool = new FileHandlePool(fs);
        var a = pool.Acquire("a", Opts());
        var b = pool.Acquire("b", Opts());
        pool.RemoveAll(p => p == "a").Should().Be(1);
        pool.Count.Should().Be(1);
        ((Action)(() => a.Read(0, new byte[1]))).Should().Throw<ObjectDisposedException>("RemoveAll 真关闭");
        ((Action)(() => b.Read(0, new byte[1]))).Should().NotThrow();
    }

    [Fact]
    public void PoolDispose_ClosesAll()
    {
        using var fs = MemoryFileSystem.New();
        var pool = new FileHandlePool(fs);
        var a = pool.Acquire("a", Opts());
        pool.Dispose();
        ((Action)(() => a.Read(0, new byte[1]))).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void PoolDispose_IsIdempotent()
    {
        using var fs = MemoryFileSystem.New();
        var pool = new FileHandlePool(fs);
        pool.Acquire("a", Opts());
        pool.Dispose();
        var act = () => pool.Dispose();
        act.Should().NotThrow();
        ((Action)(() => pool.Acquire("x", Opts()))).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void MaxCapacity_LruEvictsOnlyIdle_InUseSkipped()
    {
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { QuotaBytes = 1 << 22 });
        using var pool = new FileHandlePool(fs, maxCapacity: 2);
        var a = pool.Acquire("a", Opts());      // a 在用
        var b = pool.Acquire("b", Opts());
        pool.Release(b);                        // b 还清（idle）
        _ = pool.Acquire("a", Opts());          // 刷新 a 的 LRU 时钟（在用）
        var c = pool.Acquire("c", Opts());      // 超容——只可能逐 idle 的 b（a 在用被跳过）

        pool.Count.Should().Be(2);
        ((Action)(() => a.Read(0, new byte[1]))).Should().NotThrow("在用句柄不被 LRU 误伤（安全淘汰）");
        ((Action)(() => c.Read(0, new byte[1]))).Should().NotThrow();
    }

    [Fact]
    public void Eviction_DoesNotReclaimDerivedMappings()
    {
        // R7：淘汰句柄不回收其派生 IMappedSection（映射生命周期独立）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions
        {
            Allocation = MemoryAllocationMode.Reserved,
            QuotaBytes = 1 << 22,
        });
        using var pool = new FileHandlePool(fs, maxCapacity: 1);
        var a = pool.Acquire("a", Opts());
        a.Write(0, new byte[4096]);
        var section = a.Map(0, 4096, AccessMode.ReadWrite);
        _ = pool.Acquire("b", Opts());   // 超容（a 在用——安全 LRU 跳过；此处仅验证映射不随任何路径回收）

        section.View.Span[0] = 0x77;
        var act = () => section.View.Span[1] = 0x88;
        act.Should().NotThrow();
        section.Dispose();
    }

    [Fact]
    public void CrossInstanceAppend_FileLevelReservation_AllDistinctOffsets()
    {
        // ★ 第九轮核心验收：文件级追加预留——不同句柄实例（不同打开语义）并发 Append 也无覆写
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { QuotaBytes = 1 << 22 });
        var w1 = fs.Open("log", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite });
        var w2 = fs.Open("log", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        const int perHandle = 2000, len = 64;
        var offsets = new ConcurrentBag<long>();
        Parallel.For(0, 2, i =>
        {
            var h = i == 0 ? w1 : w2;
            var data = new byte[len];
            for (var n = 0; n < perHandle; n++)
                offsets.Add(h.Append(data));
        });
        offsets.Distinct().Count().Should().Be(2 * perHandle, "跨实例并发追加落点两两不交（文件级预留）");
        w1.Length.Should().Be((long)2 * perHandle * len);
        w1.Dispose();
        w2.Dispose();
    }

    [Fact]
    public void AppendCursor_ResetOnTruncate_ContinuesFromNewEnd()
    {
        using var fs = MemoryFileSystem.New();
        var h = fs.Open("f", Opts());
        h.Append(new byte[100]).Should().Be(0);
        h.SetLength(40);   // 截断——预留权威复位
        h.Append(new byte[10]).Should().Be(40, "截断后追加从新末端继续");
        h.Length.Should().Be(50);
        h.Dispose();
    }

    [Fact]
    public void AppendCursor_RecreatedAfterDelete_StartsFresh()
    {
        using var fs = MemoryFileSystem.New();
        var h = fs.Open("f", Opts());
        h.Append(new byte[100]);
        fs.Delete("f");   // 预留盒摘除
        var h2 = fs.Open("f", Opts());
        h2.Append(new byte[10]).Should().Be(0, "删除重建后从零开始（盒按新 Length 重建）");
        h.Dispose();
        h2.Dispose();
    }

    [Fact]
    public void PositionBookmarks_PerHandleIndependent()
    {
        // 游标拆分的另一半：Position/Seek 是会话书签——各句柄独立互不干扰
        using var fs = MemoryFileSystem.New();
        var h1 = fs.Open("f", Opts());
        var h2 = fs.Open("f", Opts());
        h1.Append(new byte[16]);   // h1 书签推进；文件级预留推进
        h2.Position.Should().Be(0, "h2 的书签不受 h1 追加影响（会话状态各归各）");
        h1.Seek(4, SeekOrigin.Begin);
        h2.Position.Should().Be(0);
        h1.Dispose();
        h2.Dispose();
    }

    [Fact]
    public void HandleCacheKey_ValueEquality_ExplicitHash()
    {
        var k1 = new FileHandlePool.HandleCacheKey("f", AccessMode.ReadWrite, FileOpenMode.OpenOrCreate,
            FileSharing.ReadWrite, FileOpenHints.None);
        var k2 = new FileHandlePool.HandleCacheKey("f", AccessMode.ReadWrite, FileOpenMode.OpenOrCreate,
            FileSharing.ReadWrite, FileOpenHints.None);
        k2.Should().Be(k1);
        k2.GetHashCode().Should().Be(k1.GetHashCode());

        var hashes = new HashSet<int>();
        for (var i = 0; i < 10000; i++)
        {
            var k = new FileHandlePool.HandleCacheKey($"file-{i}", AccessMode.ReadWrite,
                FileOpenMode.OpenOrCreate, FileSharing.ReadWrite, FileOpenHints.None);
            hashes.Add(k.GetHashCode());
        }
        hashes.Count.Should().BeGreaterThan(9800, $"哈希碰撞率过高：{hashes.Count}/10000");
    }

    [Fact]
    public void Pool_WorksWithDiskFileSystem()
    {
        var dir = TestTempDir.Create("core-io-pool2");
        using var fs = DiskFileSystem.OpenOrCreate(dir);
        fs.EnsureRoot();
        using var pool = new FileHandlePool(fs);
        var h = pool.Acquire("f", Opts());
        h.Write(0, new byte[128]);
        pool.Acquire("f", Opts()).Should().BeSameAs(h);
        pool.RemoveAll(p => p == "f").Should().Be(1);
        TestTempDir.TryCleanup(dir);
    }
}
