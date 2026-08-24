namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 分配器稠密度回归（VII-8 销案线）——多写者并发 Append 后段表 SegId 序列必须稠密（0..tail 连续注册）。
/// <para>★ VII-8 根因（2026-08-16 插桩实锤）：<c>AppendSegmentRawUnsafe</c> 的 _segIndex 扩容用
///   <c>Array.Resize</c>——零初始化新数组经普通写先可见、-1 Fill 后可见——无锁读者（CAS 门/Ensure
///   步进）窗口内读到 _segIndex[x]=0 →「段存在（slot 0）」→ 跳过注册，尾水位穿过未注册段后
///   Ensure 只看 tail 当前段、永不回填 = 永久空洞（重开截断、数据不可达）。修复 = build-then-publish
///   （先填 -1 再 Volatile.Write 单点发布）。本探针单写者/多写者双硬断言。</para>
/// </summary>
public sealed class AllocatorDensityBaselineProbe : IDisposable
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

    private List<int> RunAndGetIds(int threads, int perThread)
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions("test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        var payload = new byte[512];
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++) dev.Append(payload);
        })).ToArray();
        Task.WaitAll(tasks);
        Thread.Sleep(500);   // worker 静默

        // ★ 走公共面（MinSegId..MaxSegId + TryGetSegment）——反射摸私有字段（_segments）对内部
        //   重构脆（v2 段表形态变化后 NRE）；密度语义等价：存在段即计入。
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var table = (SegmentTable)typeof(StorageEngine)
            .GetField("_segmentTable", flags)!.GetValue(dev)!;
        var ids = new List<int>();
        for (var segId = table.MinSegId; segId <= table.MaxSegId; segId++)
            if (table.TryGetSegment(segId, out _))
                ids.Add(segId);
        return ids;
    }

    private static List<int> FindGaps(List<int> ids)
    {
        var gaps = new List<int>();
        for (var i = 1; i < ids.Count; i++)
            if (ids[i] != ids[i - 1] + 1)
                gaps.Add(ids[i - 1]);   // 跳号起点
        return gaps;
    }

    /// <summary>单写者必须稠密——顺序路径回归绊线。</summary>
    [Fact]
    public void SingleWriter_SegmentIdSequence_Dense()
    {
        var ids = RunAndGetIds(threads: 1, perThread: 2400);
        var gaps = FindGaps(ids);
        gaps.Should().BeEmpty($"单写者顺序 Append 段表必须稠密（0..{ids.Count - 1} 连续）；跳号起点: {string.Join(",", gaps)}");
    }

    /// <summary>多写者必须稠密——VII-8（索引扩容发布顺序竞态）的销案硬断言。</summary>
    [Fact]
    public void MultiWriter_SegmentIdSequence_Dense()
    {
        var ids = RunAndGetIds(threads: 6, perThread: 400);
        var gaps = FindGaps(ids);
        gaps.Should().BeEmpty(
            $"多写者段表必须稠密（VII-8 索引扩容发布顺序竞态回归）；跳号起点: {string.Join(",", gaps)}；前 40: {string.Join(",", ids.Take(40))}");
    }
}
