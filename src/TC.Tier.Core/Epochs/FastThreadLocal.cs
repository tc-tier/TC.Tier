using System.Diagnostics.CodeAnalysis;

namespace TC.Tier.Core.Epochs;

/// <summary>
/// 实例级线程局部变量的快速实现（用槽位数组替代 ThreadStatic 字段，规避每个实例一个 ThreadStatic 的开销）。
/// </summary>
/// <typeparam name="T">线程局部值类型。</typeparam>
[SuppressMessage("ReSharper", "StaticMemberInGenericType")]
internal sealed class FastThreadLocal<T>
{
    // 支持的最大实例数
    private const int kMaxInstances = 128;

    [ThreadStatic]
    private static T[]? _tlValues;
    [ThreadStatic]
    private static int[]? _tlIid;

    private readonly int _offset;
    private readonly int _iid;

    private static readonly int[] Instances = new int[kMaxInstances];
    private static int _instanceId;

    public FastThreadLocal()
    {
        _iid = Interlocked.Increment(ref _instanceId);

        for (int i = 0; i < kMaxInstances; i++)
        {
            if (0 == Interlocked.CompareExchange(ref Instances[i], _iid, 0))
            {
                _offset = i;
                return;
            }
        }
        throw new InvalidOperationException("Unsupported number of simultaneous instances");
    }

    public void InitializeThread()
    {
        if (_tlValues == null)
        {
            _tlValues = new T[kMaxInstances];
            _tlIid = new int[kMaxInstances];
        }

        if (_tlIid![_offset] == _iid) return;
        _tlIid[_offset] = _iid;
        _tlValues![_offset] = default!;
    }

    public void DisposeThread()
    {
        _tlValues![_offset] = default!;
        _tlIid![_offset] = 0;
    }

    /// <summary>
    /// 在所有线程上释放本实例（归还槽位）。
    /// </summary>
    public void Dispose()
    {
        Instances[_offset] = 0;
    }

    public T Value
    {
        get => _tlValues![_offset];
        set => _tlValues![_offset] = value;
    }

    public bool IsInitializedForThread => (_tlValues != null) && (_iid == _tlIid![_offset]);
}