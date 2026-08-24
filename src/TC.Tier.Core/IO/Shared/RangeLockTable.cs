namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 进程内字节范围锁表（mem/remote 介质共用——多句柄协调真实生效）。
/// <para>★ G8 翻案（medium-protocol §5.9）：remote 的进程内 advisory 区间表与 mem 同构——
///   仅约束同进程同 fs 实例的句柄（与 FileSharing 同一诚实等级，差异声明管辖）。</para>
/// <para>★ ★ CORE-29 契约声明：本类<b>零内部同步</b>——所有方法必须由调用方在外部锁内调用
///   （mem = fs._lock；remote = _rangeLockGate）；ChangedGate 例外（PulseAll 由调用方持表锁时
///   触发——等待方只持 ChangedGate 不持表锁）。换调用方 = 先读此声明。</para>
/// </summary>
internal sealed class RangeLockTable
{
    private readonly List<Entry> _entries = [];

    /// <summary>变更信号（★ CORE-20：阻塞等待者的条件变量——释放时 PulseAll，免 15ms 轮询 + 全局锁抢占；
    /// 丢失唤醒由等待方 50ms 有界分片兜底）。</summary>
    internal readonly object ChangedGate = new();

    private readonly struct Entry(long start, long length, FileLockMode mode, object owner)
    {
        public readonly long Start = start, Length = length;
        public readonly FileLockMode Mode = mode;
        public readonly object Owner = owner;

        public bool Overlaps(long start, long length) => Start < start + length && start < Start + Length;
    }

    /// <summary>尝试获取——同 owner 重叠=允许（POSIX OFD 转换语义）；他 owner 重叠且任一排他=冲突。</summary>
    public bool TryAcquire(long offset, long length, FileLockMode mode, object owner)
    {
        foreach (var e in _entries)
        {
            if (!e.Overlaps(offset, length)) continue;
            if (!ReferenceEquals(e.Owner, owner) && (e.Mode == FileLockMode.Exclusive || mode == FileLockMode.Exclusive))
                return false;
        }
        _entries.Add(new Entry(offset, length, mode, owner));
        return true;
    }

    /// <summary>★ CORE-29：按 (offset, length, owner) 精确释放**一个**同型条目（原删除全部同型——
    /// 同 owner 同区间双持（不同 mode 合法——TryAcquire 同 owner 重叠放行）时一次释放两条 = 锁泄漏）。</summary>
    public void Release(long offset, long length, object owner)
    {
        var released = false;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (ReferenceEquals(e.Owner, owner) && e.Start == offset && e.Length == length)
            {
                _entries.RemoveAt(i);
                released = true;
                break;   // ★ CORE-29：一次一条（对称于 TryAcquire 的逐条添加）
            }
        }
        // ★ CORE-20：唤醒阻塞等待者（PulseAll 须持 ChangedGate——调用方持表锁 → 锁序 表锁 → ChangedGate；
        //   等待者只持 ChangedGate（不持表锁）——无环）
        if (released) lock (ChangedGate) Monitor.PulseAll(ChangedGate);
    }

    public void ReleaseAll(object owner)
    {
        var released = _entries.RemoveAll(e => ReferenceEquals(e.Owner, owner)) > 0;
        if (released) lock (ChangedGate) Monitor.PulseAll(ChangedGate);   // ★ CORE-20
    }

    /// <summary>空表判据（持有方清理用）。</summary>
    public bool IsEmpty => _entries.Count == 0;
}
