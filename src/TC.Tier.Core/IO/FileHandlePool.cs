using System.Collections.Concurrent;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO;

/// <summary>
/// 句柄池——<see cref="IFileHandle"/> 的键控共享缓存（公开原语；三步获取/败者自毁语义搬迁自
/// StorageEngineBase.FileHandle，第九轮评审重立为 Acquire/Release 协议）。
/// <para>★ 单获取接口 <see cref="Acquire"/>：读写分离由 options 参数分桶（key 含 Access）；
///   预分配是创建期意图不进 key——同文件不同预分配大小的两次 Acquire 命中同一实例。</para>
/// <para>★ 归还协议（挂载式，零分配）：池内句柄 Dispose = 归还使用权（默认<b>不</b>关闭底层）；
///   底层关闭只有三条池内出口——<see cref="Release(IFileHandle, bool)"/>(close:true)（定向）、
///   <see cref="RemoveAll"/>(谓词批量)、<see cref="Dispose"/>（全量）。外部任何 Dispose 都不可能关闭池内资源。</para>
/// <para>★ 安全 LRU：maxCapacity 超容只淘汰 idle（usage==0）句柄；in-use 跳过并告警
///   （"淘汰正在使用的句柄是调用方错误"从文档警告升级为机制保证）。LRU 淘汰不回收派生
///   <see cref="IMappedSection"/>（映射生命周期独立）。</para>
/// <para>★ 关闭顺序契约：pool.Dispose 先于 fs.Dispose；Dispose 时 usage&gt;0 告警（忘还观测点）。</para>
/// <para>★ io.md 陷阱：无 maxCapacity 时禁无界增长——长期运行消费者必须周期 RemoveAll 回收。</para>
/// </summary>
public sealed class FileHandlePool : IDisposable
{
    /// <summary>
    /// 池专用 key——值相等、零分配。★ 显式 HashCode.Combine（㉜′）；
    /// ★ 不含 PreallocateSize（含它则同文件不同预分配 → 两实例，命中率崩塌）。
    /// </summary>
    internal readonly record struct HandleCacheKey(string Path, AccessMode Access, FileOpenMode Mode,
        FileSharing Sharing, FileOpenHints Hints)
    {
        public int KeyHashCode => HashCode.Combine(Path, (int)Access, (int)Mode, (int)Sharing, (int)Hints);
    }

    private sealed class Entry
    {
        public required IFileHandle Handle;
        public required HandlePoolAttachment Attachment;
        public long LastUsed;
    }

    private readonly IFileSystem _fs;
    private readonly int? _maxCapacity;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<HandleCacheKey, Entry> _cache = new();
    private readonly ConcurrentDictionary<IFileHandle, HandleCacheKey> _reverse = new();
    private long _clock;
    private int _disposed;

    /// <summary>构造——默认无界（纯缓存 + 手动回收模型）；可选容量上限（超容 LRU 只逐 idle）。</summary>
    public FileHandlePool(IFileSystem fileSystem, int? maxCapacity = null, ILogger? logger = null)
    {
        _fs = fileSystem;
        _maxCapacity = maxCapacity;
        _logger = logger;
    }

    private static HandleCacheKey KeyOf(string path, FileOpenOptions options)
        => new(path, options.Access, options.Mode, options.Sharing, options.Hints);

    /// <summary>
    /// 获取（唯一获取接口）——相同 (path, 打开语义) 命中同一实例并 +1 使用权；未命中按完整意图
    /// （含 PreallocateSize——open 即幂等执行）创建、挂载、入池。三步入字典 + 败者自毁
    /// （竞争输家真关闭——从未被服务，不走归还簿记）。
    /// </summary>
    public IFileHandle Acquire(string path, FileOpenOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var key = KeyOf(path, options);
        if (_cache.TryGetValue(key, out var hit))
        {
            Interlocked.Increment(ref hit.Attachment.Usage);
            hit.Attachment.Trace("acquire-hit");
            Volatile.Write(ref hit.LastUsed, Interlocked.Increment(ref _clock));
            return hit.Handle;
        }

        var created = _fs.Open(path, options);
        var attachment = ((IPoolAttachable)created).AttachPool(this);
        Interlocked.Increment(ref attachment.Usage);
        attachment.Trace("acquire-create");
        var entry = new Entry { Handle = created, Attachment = attachment, LastUsed = Interlocked.Increment(ref _clock) };
        var winner = _cache.GetOrAdd(key, entry);
        if (!ReferenceEquals(winner.Handle, created))
        {
            // 并发竞争输掉 → 输家句柄真关闭（从未被服务，直接关闭不经归还）；reverse 不登记。
            // ★ 输家调用方拿到的是 winner 句柄——必须对 winner 的使用权 +1（借还配对铁律），
            //   否则调用方 Dispose 归还 -1 而其 Acquire 从未 +1 → 计数下溢（引擎并发建段实测触发）。
            Interlocked.Increment(ref winner.Attachment.Usage);
            winner.Attachment.Trace("acquire-race-winner");
            Volatile.Write(ref winner.LastUsed, Interlocked.Increment(ref _clock));
            ((IPoolAttachable)created).CloseUnderlying();
        }
        else
        {
            _reverse[created] = key;
            EvictOverCapacity();
        }
        return winner.Handle;
    }

    /// <summary>命中获取（未命中返回 false，不创建、无副作用）。</summary>
    public bool TryAcquire(string path, FileOpenOptions options, out IFileHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_cache.TryGetValue(KeyOf(path, options), out var hit))
        {
            Interlocked.Increment(ref hit.Attachment.Usage);
            hit.Attachment.Trace("try-acquire-hit");
            Volatile.Write(ref hit.LastUsed, Interlocked.Increment(ref _clock));
            handle = hit.Handle;
            return true;
        }
        handle = null!;
        return false;
    }

    /// <summary>
    /// 归还使用权——★ 默认（close:false）只注销本次借用，底层照常留池服务其他共享者（句柄 Dispose 等价）；
    /// close:true = 定向关闭：底层 Dispose + 出缓存（关闭必与出缓存同发——无僵尸窗口）。
    /// </summary>
    public void Release(IFileHandle handle, bool close = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (handle is not IPoolAttachable attachable || attachable.PoolAttachment?.Pool != this)
            throw new ArgumentException("句柄不属于本池（须经本池 Acquire 获取）。", nameof(handle));
        if (close)
        {
            ForceClose(handle, attachable.PoolAttachment, source: nameof(Release));
            return;
        }
        OnUsageReleased(handle, attachable.PoolAttachment);
    }

    /// <summary>归还核心（句柄 Dispose 路由到此处）——计数递减 + 下溢绊线。</summary>
    internal void OnUsageReleased(IFileHandle handle, HandlePoolAttachment attachment)
    {
        attachment.Trace("release");
        var usage = Interlocked.Decrement(ref attachment.Usage);
#if DEBUG
        if (usage < 0)
            throw new InvalidOperationException(
                $"[FileHandlePool] 使用权计数下溢（多还）——path={handle.Path}，usage={usage}。" +
                "每次 Acquire/借用须恰好配对一次归还（Dispose/Release）。" +
                $"\n── 借还历史（示波器）──{attachment.Dump()}");
#else
        _ = usage;
#endif
    }

    /// <summary>按谓词批量关闭（业务事件——删文件；引擎 lease 保证静默）。在用计数&gt;0 时强制关闭并告警。</summary>
    public int RemoveAll(Predicate<string> pathMatch)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var removed = 0;
        foreach (var kv in _cache)
        {
            if (!pathMatch(kv.Key.Path)) continue;
            if (!_cache.TryRemove(kv.Key, out var entry)) continue;
            ForceCloseEntry(entry, source: nameof(RemoveAll));
            removed++;
        }
        return removed;
    }

    /// <summary>当前缓存实例数。</summary>
    public int Count => _cache.Count;

    /// <inheritdoc/>
    /// <remarks>全量关闭（统一释放入口——须先于 fs.Dispose）。usage&gt;0 的句柄强制关闭并告警（忘还观测点）。幂等。</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var kv in _cache)
        {
            if (_cache.TryRemove(kv.Key, out var entry))
                ForceCloseEntry(entry, source: nameof(Dispose));
        }
        _reverse.Clear();
    }

    private void ForceClose(IFileHandle handle, HandlePoolAttachment attachment, string source)
    {
        if (_reverse.TryRemove(handle, out var key))
            _cache.TryRemove(key, out _);
        ForceCloseCore(handle, attachment, source);
    }

    private void ForceCloseEntry(Entry entry, string source)
    {
        _reverse.TryRemove(entry.Handle, out _);
        ForceCloseCore(entry.Handle, entry.Attachment, source);
    }

    private void ForceCloseCore(IFileHandle handle, HandlePoolAttachment attachment, string source)
    {
        var usage = Volatile.Read(ref attachment.Usage);
        if (usage > 0)
            _logger?.LogWarning(
                "[FileHandlePool] {Source} 强制关闭在用句柄 path={Path}（usage={Usage}>0——借用未归还或业务事件与借用并发）",
                source, handle.Path, usage);
        ((IPoolAttachable)handle).CloseUnderlying();
    }

    /// <summary>容量超限 LRU 淘汰——★ 只逐 idle（usage==0）；in-use 跳过并告警（机制保证不误伤在用）。</summary>
    private void EvictOverCapacity()
    {
        if (_maxCapacity is not { } cap) return;
        var attempts = 0;
        while (_cache.Count > cap && attempts++ < cap * 2)
        {
            HandleCacheKey oldest = default;
            var oldestStamp = long.MaxValue;
            var found = false;
            foreach (var kv in _cache)
            {
                var stamp = Volatile.Read(ref kv.Value.LastUsed);
                if (stamp >= oldestStamp) continue;
                oldestStamp = stamp;
                oldest = kv.Key;
                found = true;
            }
            if (!found) return;
            if (!_cache.TryGetValue(oldest, out var entry) || !_cache.TryRemove(oldest, out var removed)) continue;

            // ★ 安全淘汰：只逐 idle——最久未用但在用的句柄放回（告警），标记已尝试后扫次旧
            if (Volatile.Read(ref removed.Attachment.Usage) == 0)
            {
                _reverse.TryRemove(removed.Handle, out _);
                ((IPoolAttachable)removed.Handle).CloseUnderlying();
                _logger?.LogWarning("[FileHandlePool] LRU 淘汰 idle 句柄 path={Path}（容量 {Cap}）；派生映射不受影响",
                    oldest.Path, cap);
            }
            else
            {
                _logger?.LogWarning("[FileHandlePool] LRU 跳过在用句柄 path={Path}（usage>0——安全淘汰不误伤）", oldest.Path);
                Volatile.Write(ref removed.LastUsed, Interlocked.Increment(ref _clock));   // 标记已尝试——下轮扫次旧
                _cache.TryAdd(oldest, removed);
            }
        }
    }
}
