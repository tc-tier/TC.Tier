using TC.Tier.Core.Net.Ports;

namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// 内存身份夹具（spec-12 §8.3——IIdentitySource 测试实现：首次生成（NewRandom）并保存、
/// 此后加载同一身份——NodeId 跨"重启"稳定；生成与保存一体（锁内），半写不可见）。
/// </summary>
public sealed class InMemoryIdentitySource : IIdentitySource
{
    private readonly object _lock = new();
    private NodeIdentity? _identity;

    public ValueTask<NodeIdentity> LoadOrCreateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _identity ??= new NodeIdentity { Id = NodeId.NewRandom() };
            return ValueTask.FromResult(_identity);
        }
    }
}
