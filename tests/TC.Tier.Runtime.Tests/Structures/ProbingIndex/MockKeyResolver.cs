using System.Runtime.CompilerServices;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// 测试用 mock <see cref="IKeyResolver{TKey}"/>（继任旧 MockRecordStore——IRecordStore 已随 IKeyResolver 三方法定稿退役）。
/// <para>★ 用 <see cref="Dictionary{TKey,TValue}"/> 模拟"地址→key"映射，替代真实 Ring："任何实现者皆可喂索引"。</para>
/// <para>★ 用法：Insert 时 <c>resolver.Put(addr, key)</c> 注册；HashIndex Find/Insert/Delete 判等闭环
///   回调 TryGetKey 时读回 key；ScanAsync 按注册顺序吐 [begin,end) 对（恢复自建数据面）。</para>
/// </summary>
internal sealed class MockKeyResolver<TKey> : IKeyResolver<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private readonly Dictionary<LogicalAddress, TKey> _map = new();
    private readonly List<(LogicalAddress Addr, TKey Key)> _ordered = new();

    /// <summary>测试注册：记录某地址对应的 key（模拟 Ring 写了一条 record）；同地址重复 Put 覆盖原值。</summary>
    public void Put(LogicalAddress addr, TKey key)
    {
        if (_map.ContainsKey(addr))
        {
            _map[addr] = key;
            for (int i = 0; i < _ordered.Count; i++)
                if (_ordered[i].Addr == addr) { _ordered[i] = (addr, key); break; }
            return;
        }
        _map[addr] = key;
        _ordered.Add((addr, key));
    }

    /// <summary>已落盘水位（测试可设——模拟 Ring 的 FlushedUntilAddress 推进）。</summary>
    public LogicalAddress FlushedWatermark { get; set; } = LogicalAddress.Empty;

    public bool TryGetKey(LogicalAddress addr, out TKey key) => _map.TryGetValue(addr, out key);

    public LogicalAddress GetFlushedWatermark() => FlushedWatermark;

    public async IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // 纯内存源无真异步；await 保迭代器形态
        foreach (var (addr, key) in _ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (addr >= begin && addr < end)
                yield return (key, addr);
        }
    }

    public IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAsync(CancellationToken ct = default)
        => ScanAll(ct);

    private async IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAll(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var (addr, key) in _ordered)
        {
            ct.ThrowIfCancellationRequested();
            yield return (key, addr);
        }
    }
}
