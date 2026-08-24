using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Collections;

/// <summary>
/// 分片锁集合，Value存WeakReference&lt;TValue&gt;
/// TKey 支持 int / long / uint / ulong / Guid / string 等；
/// </summary>
/// <typeparam name="TKey">字典键，必须实现IEquatable&lt;TKey&gt;</typeparam>
/// <typeparam name="TValue">目标类型，必须是引用类型(class)</typeparam>
public sealed class ShardLockWeakReference<TKey, TValue>
    where TKey : IEquatable<TKey>
    where TValue : class
{
    private readonly int _shardCount;
    private readonly int _shardMask;
    private readonly (Dictionary<TKey, WeakReference<TValue>> Dict, object Sync)[] _shards;
    private readonly IEqualityComparer<TKey> _comparer;

    /// <summary>
    /// 默认分片=16；使用类型默认 EqualityComparer&lt;TKey&gt;.Default
    /// </summary>
    public ShardLockWeakReference()
        : this(shardCount: 16, EqualityComparer<TKey>.Default)
    {
    }

    /// <summary>
    /// 自定义分片数(必须2^N) + 自定义比较器
    /// </summary>
    /// <param name="shardCount">分片数，必须是2的幂：8/16/32</param>
    /// <param name="comparer">key相等比较器，传null用Default</param>
    public ShardLockWeakReference(int shardCount, IEqualityComparer<TKey>? comparer = null)
    {
        if (shardCount < 2 || (shardCount & (shardCount - 1)) != 0)
            throw new ArgumentException("shardCount must be power of two(2,4,8,16,32...)", nameof(shardCount));

        _shardCount = shardCount;
        _shardMask = shardCount - 1;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;

        _shards = new (Dictionary<TKey, WeakReference<TValue>>, object)[_shardCount];
        for (var i = 0; i < _shardCount; i++)
        {
            //传入comparer，Dictionary使用IEquatable<T>，值类型无装箱
            _shards[i] = (new Dictionary<TKey, WeakReference<TValue>>(64, _comparer), new object());
        }
    }

    /// <summary>
    /// 计算分片索引；利用 EqualityComparer 获取hashcode；
    /// 注意：不要自己BitConverter打Guid，直接调用GetHashCode，交给comparer处理，通用兼容全部TKey
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetShardIndex(in TKey key)
    {
        var hash = _comparer.GetHashCode(key!);
        //处理负数hash，转为非负
        var uHash = (uint)hash;
        return (int)(uHash & _shardMask);
    }

    /// <summary>
    /// 添加或更新键值对，value存为WeakReference&lt;TValue&gt;
    /// </summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    public void AddOrUpdate(TKey key, TValue value)
    {
        var idx = GetShardIndex(key);
        ref var shard = ref _shards[idx];
        lock (shard.Sync)
        {
            shard.Dict[key] = new WeakReference<TValue>(value);
        }
    }

    /// <summary>
    /// 尝试获取键对应的值，如果值已被GC回收，则返回false
    /// </summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <returns>如果值存在且未被GC回收，返回true；否则返回false</returns>
    public bool TryGet(TKey key, out TValue? value)
    {
        value = null;
        var idx = GetShardIndex(key);
        ref var shard = ref _shards[idx];
        lock (shard.Sync)
        {
            if (!shard.Dict.TryGetValue(key, out var wr))
                return false;
            wr.TryGetTarget(out value);
            return value != null;
        }
    }

    /// <summary>
    /// 移除指定键的条目，如果存在则返回true，否则返回false
    /// </summary>
    /// <param name="key">键</param>
    /// <returns>如果键存在并被移除，返回true；否则返回false</returns>
    public bool Remove(TKey key)
    {
        var idx = GetShardIndex(key);
        ref var shard = ref _shards[idx];
        lock (shard.Sync)
        {
            return shard.Dict.Remove(key);
        }
    }

    /// <summary>
    /// 清理所有已被GC回收的弱引用条目，并返回移除的总数
    /// </summary>
    /// <returns>移除的弱引用条目总数</returns>
    public int CleanupDeadReferences()
    {
        var totalRemoved = 0;
        List<TKey>? toRemove = null;

        for (var i = 0; i < _shardCount; i++)
        {
            ref var shard = ref _shards[i];
            lock (shard.Sync)
            {
                toRemove?.Clear();
                foreach (var kv in shard.Dict.Where(kv => !kv.Value.TryGetTarget(out _)))
                {
                    toRemove ??= new List<TKey>(16);
                    toRemove.Add(kv.Key);
                }

                if (toRemove is not { Count: > 0 }) continue;
                foreach (var k in toRemove)
                {
                    shard.Dict.Remove(k);
                }

                totalRemoved += toRemove.Count;
            }
        }

        return totalRemoved;
    }

    /// <summary>
    /// 清空所有分片的字典，移除所有条目
    /// </summary>
    public void Clear()
    {
        for (var i = 0; i < _shardCount; i++)
        {
            ref var shard = ref _shards[i];
            lock (shard.Sync)
            {
                shard.Dict.Clear();
            }
        }
    }

    /// <summary>
    /// 获取所有分片中条目的总数（包括已被GC回收的弱引用条目）
    /// </summary>
    /// <returns>所有分片中条目的总数</returns>
    public int GetTotalEntryCount()
    {
        var cnt = 0;
        for (var i = 0; i < _shardCount; i++)
        {
            ref var shard = ref _shards[i];
            lock (shard.Sync)
            {
                cnt += shard.Dict.Count;
            }
        }

        return cnt;
    }

    /// <summary>
    /// 获取所有分片中仍然存活的值（未被GC回收的对象）
    /// </summary>
    public IEnumerable<TValue> AllValues
    {
        get
        {
            for (var i = 0; i < _shardCount; i++)
            {
                // ★ C# 12 兼容：迭代器内不允许 ref 局部变量（C# 13 preview 特性）——值元组拷贝两份引用，语义等价
                var shard = _shards[i];
                lock (shard.Sync)
                {
                    foreach (var kv in shard.Dict)
                    {
                        if (kv.Value.TryGetTarget(out var value))
                        {
                            yield return value;
                        }
                    }
                }
            }
        }
    }
}