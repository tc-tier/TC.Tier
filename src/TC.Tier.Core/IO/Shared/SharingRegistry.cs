namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 进程内共享冲突登记表——<see cref="FileSharing"/> 的 advisory 语义在 Unix 上的兑现
/// （POSIX open(2) 无 FileShare 原生对应；BCL Unix 的进程内检查只拦 <c>Share=None</c>，
/// Read/Write/ReadWrite 声明不检查——本表补全双向兼容检查）。
/// <para>★ 检查规则（Windows CreateFile 语义对齐，双向）：新句柄与每个已注册句柄满足
///   <c>existing.Access ⊆ new.Sharing &amp;&amp; new.Access ⊆ existing.Sharing</c> 才放行；
///   任一方向越界抛 <see cref="IOError.SharingViolation"/>。</para>
/// <para>★ 保护边界（与 io.md §7.2 一致）：仅约束同进程内经同一 fs 实例打开的句柄；
///   Windows 上 OS 原生 FileShare 是超集保护（双保险，两者断言方向一致）。</para>
/// </summary>
internal sealed class SharingRegistry
{
    /// <summary>单个已注册句柄的登记项（引用身份——同参数双句柄各自独立注销）。</summary>
    internal sealed record Entry(AccessMode Access, FileSharing Sharing);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _open = new();

    /// <summary>access → 所需的 share 位（ReadWrite 需要两者）。</summary>
    private static FileSharing RequiredShare(AccessMode access) => access switch
    {
        AccessMode.Read => FileSharing.Read,
        AccessMode.Write => FileSharing.Write,
        _ => FileSharing.ReadWrite,
    };

    /// <summary>
    /// 检查并注册（原子——check-then-register 同步块内，无竞态窗口）。
    /// 与任何已注册句柄双向不兼容时抛 <see cref="FileIOException"/>（SharingViolation）。
    /// </summary>
    public Entry Register(string path, AccessMode access, FileSharing sharing)
    {
        var entry = new Entry(access, sharing);
        lock (_gate)
        {
            if (_open.TryGetValue(path, out var existing))
            {
                var need = RequiredShare(access);
                foreach (var e in existing)
                {
                    // 双向：已开句柄的访问权 ⊄ 新句柄允许的共享 → 冲突；
                    //       新句柄的访问权 ⊄ 已开句柄允许的共享 → 冲突。
                    if ((RequiredShare(e.Access) & ~sharing) != 0 ||
                        (need & ~e.Sharing) != 0)
                    {
                        throw new FileIOException(IOError.SharingViolation,
                            $"Open rejected by sharing policy: existing (access={e.Access}, sharing={e.Sharing}) " +
                            $"vs new (access={access}, sharing={sharing}) on '{path}'.",
                            path, "Open");
                    }
                }
                existing.Add(entry);
            }
            else
            {
                _open[path] = [entry];
            }
        }
        return entry;
    }

    /// <summary>注销句柄（Dispose 路径——按登记项引用精确移除；幂等）。</summary>
    public void Unregister(string path, Entry entry)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(path, out var existing)) return;
            existing.Remove(entry);
            if (existing.Count == 0)
                _open.Remove(path);
        }
    }

    /// <summary>该路径是否有打开句柄在档（Raw 介质 Delete/Move-overwrite 的前置检查——
    /// 条目摘除即时回收物理块，打开句柄在档 = 锁外快照读者可能访问已回收块，拒绝是唯一安全语义）。</summary>
    public bool HasOpenHandles(string path)
    {
        lock (_gate)
            return _open.ContainsKey(path);
    }
}
