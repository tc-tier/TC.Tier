using System.Diagnostics;
using System.Text;
using FASTER.core;

if (args.Length < 1)
{
    Console.WriteLine("Usage: FasterFsyncBench <test-dir>");
    return;
}

var testDir = args[0];

Console.WriteLine("=======================================================");
Console.WriteLine("  FASTER Single vs Dual Partition fsync Benchmark");
Console.WriteLine("=======================================================");
Console.WriteLine();

// 真正的 fsync 测试：WriteThrough 模式下，每次 Upsert 是否触发 fsync
// FASTER 的 Upsert 默认是纯内存操作，不触发 fsync
// 要让每次写入都 fsync，需要用 WriteThrough 或者频繁 ShiftReadOnlyAddress

// 测试场景：模拟 Raft 的写入模式
// 每次 Commit 后必须确保数据落盘（fsync）
// 对比：单分区（业务KV + Raft entries 共享 log） vs 双分区（各自独立 log）

const int EntriesPerBatch = 100;
const int NumBatches = 100;
const int TotalEntries = EntriesPerBatch * NumBatches;

// === Single Partition ===
{
    if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
    Directory.CreateDirectory(testDir);
    
    var logPath = Path.Combine(testDir, "single");
    Directory.CreateDirectory(logPath);
    
    var device = Devices.CreateLogDevice(Path.Combine(logPath, "hlog.log"));
    var kv = new FasterKV<SpanByte, SpanByte>(1 << 20, new LogSettings
    {
        LogDevice = device,
        MemorySizeBits = 24, // 16MB in-memory log — 足够容纳测试数据
        PageSizeBits = 12,
        MutableFraction = 0.9,
    }, new CheckpointSettings { CheckpointDir = Path.Combine(logPath, "ckp") });
    
    var session = kv.NewSession(new SpanByteFunctions_ByteArrayOutput<Empty>());
    
    var totalSw = Stopwatch.StartNew();
    var flushSw = new Stopwatch();
    
    for (var batch = 0; batch < NumBatches; batch++)
    {
        for (var i = 0; i < EntriesPerBatch; i++)
        {
            var idx = batch * EntriesPerBatch + i;
            
            // 业务 KV
            SpanByte bizKey = SpanByte.FromFixedSpan(Encoding.UTF8.GetBytes($"biz:{idx}"));
            var bizVal = new byte[256];
            Random.Shared.NextBytes(bizVal);
            SpanByte bizValSpan = SpanByte.FromFixedSpan(bizVal.AsSpan());
            session.Upsert(ref bizKey, ref bizValSpan);
            
            // Raft entry
            SpanByte raftKey = SpanByte.FromFixedSpan(Encoding.UTF8.GetBytes($"raft:{idx}"));
            var raftVal = new byte[128];
            Random.Shared.NextBytes(raftVal);
            SpanByte raftValSpan = SpanByte.FromFixedSpan(raftVal.AsSpan());
            session.Upsert(ref raftKey, ref raftValSpan);
        }
        
        // 模拟 Commit 后 fsync — 强制 flush 当前所有内存页到磁盘
        session.CompletePending(true);
        flushSw.Start();
        kv.Log.ShiftReadOnlyAddress(kv.Log.TailAddress, wait: true);
        flushSw.Stop();
    }
    
    totalSw.Stop();
    
    Console.WriteLine($"[Single-Partition] {NumBatches} batches × {EntriesPerBatch} entries:");
    Console.WriteLine($"  Total time:       {totalSw.ElapsedMilliseconds,6} ms");
    Console.WriteLine($"  Flush time:       {flushSw.ElapsedMilliseconds,6} ms");
    Console.WriteLine($"  Throughput:       {TotalEntries * 2.0 / totalSw.Elapsed.TotalSeconds,10:N0} ops/sec");
    Console.WriteLine($"  Avg batch latency:{totalSw.Elapsed.TotalMilliseconds / NumBatches,8:F1} ms");
    Console.WriteLine($"  Flush per batch:  {flushSw.Elapsed.TotalMilliseconds / NumBatches,8:F2} ms");
    Console.WriteLine();
    
    session.Dispose();
    kv.Dispose();
    device.Dispose();
}

// === Dual Partition ===
{
    if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
    Directory.CreateDirectory(testDir);
    
    var bizPath = Path.Combine(testDir, "biz");
    var raftPath = Path.Combine(testDir, "raft");
    Directory.CreateDirectory(bizPath);
    Directory.CreateDirectory(raftPath);
    
    var device1 = Devices.CreateLogDevice(Path.Combine(bizPath, "hlog.log"));
    var device2 = Devices.CreateLogDevice(Path.Combine(raftPath, "hlog.log"));
    
    var kv1 = new FasterKV<SpanByte, SpanByte>(1 << 19, new LogSettings
    {
        LogDevice = device1, MemorySizeBits = 23, PageSizeBits = 12, MutableFraction = 0.9,
    }, new CheckpointSettings { CheckpointDir = Path.Combine(bizPath, "ckp") });
    
    var kv2 = new FasterKV<SpanByte, SpanByte>(1 << 19, new LogSettings
    {
        LogDevice = device2, MemorySizeBits = 23, PageSizeBits = 12, MutableFraction = 0.9,
    }, new CheckpointSettings { CheckpointDir = Path.Combine(raftPath, "ckp") });
    
    var s1 = kv1.NewSession(new SpanByteFunctions_ByteArrayOutput<Empty>());
    var s2 = kv2.NewSession(new SpanByteFunctions_ByteArrayOutput<Empty>());
    
    var totalSw = Stopwatch.StartNew();
    var flushSw = new Stopwatch();
    
    for (var batch = 0; batch < NumBatches; batch++)
    {
        for (var i = 0; i < EntriesPerBatch; i++)
        {
            var idx = batch * EntriesPerBatch + i;
            
            // 业务 KV → 分区 1
            SpanByte bizKey = SpanByte.FromFixedSpan(Encoding.UTF8.GetBytes($"biz:{idx}"));
            var bizVal = new byte[256];
            Random.Shared.NextBytes(bizVal);
            SpanByte bizValSpan = SpanByte.FromFixedSpan(bizVal.AsSpan());
            s1.Upsert(ref bizKey, ref bizValSpan);
            
            // Raft entry → 分区 2
            SpanByte raftKey = SpanByte.FromFixedSpan(Encoding.UTF8.GetBytes($"raft:{idx}"));
            var raftVal = new byte[128];
            Random.Shared.NextBytes(raftVal);
            SpanByte raftValSpan = SpanByte.FromFixedSpan(raftVal.AsSpan());
            s2.Upsert(ref raftKey, ref raftValSpan);
        }
        
        // 两个分区都要 fsync
        s1.CompletePending(true);
        s2.CompletePending(true);
        flushSw.Start();
        kv1.Log.ShiftReadOnlyAddress(kv1.Log.TailAddress, wait: true);
        kv2.Log.ShiftReadOnlyAddress(kv2.Log.TailAddress, wait: true);
        flushSw.Stop();
    }
    
    totalSw.Stop();
    
    Console.WriteLine($"[Dual-Partition]   {NumBatches} batches × {EntriesPerBatch} entries:");
    Console.WriteLine($"  Total time:       {totalSw.ElapsedMilliseconds,6} ms");
    Console.WriteLine($"  Flush time:       {flushSw.ElapsedMilliseconds,6} ms");
    Console.WriteLine($"  Throughput:       {TotalEntries * 2.0 / totalSw.Elapsed.TotalSeconds,10:N0} ops/sec");
    Console.WriteLine($"  Avg batch latency:{totalSw.Elapsed.TotalMilliseconds / NumBatches,8:F1} ms");
    Console.WriteLine($"  Flush per batch:  {flushSw.Elapsed.TotalMilliseconds / NumBatches,8:F2} ms");
    Console.WriteLine();
    
    s1.Dispose();
    s2.Dispose();
    kv1.Dispose();
    kv2.Dispose();
    device1.Dispose();
    device2.Dispose();
}

Console.WriteLine("Done.");
