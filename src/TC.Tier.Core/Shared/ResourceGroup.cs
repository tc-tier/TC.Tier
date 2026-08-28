using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Shared;

/// <summary>
/// 资源组——引擎/设备资源的生命周期管理器（组合持有，非继承）。
/// <list type="bullet">
/// <item><term>按名注册/查询</term><description>去掉旧 <c>this[int]</c> 下标索引（隐式契约脆弱）；用 <see cref="Add{TResource}(TResource,string?,ResourceOwnership)"/> / <see cref="Get{T}(string)"/> 显式按名。</description></item>
/// <item><term>后期添加</term><description>支持构造期 <c>params</c> + 运行期 <see cref="Add"/>（解决旧版一次性传死、meta engine 等后期资源只能靠各基类自建 <c>_disposables</c> 列表的分裂）。</description></item>
/// <item><term>所有权二态</term><description><see cref="ResourceOwnership"/> 区分 Owned（我释放）/ Referenced（外部管，只跟踪）。注入引擎等用 Referenced——泄漏可见但不被 Dispose。</description></item>
/// <item><term>object 单存储</term><description>资源可能是 <see cref="IDisposable"/>、<see cref="IAsyncDisposable"/> 或两者都实现（如 <c>IStorageEngine</c>）。
///   ★ <see cref="IAsyncDisposable"/> 不继承 <see cref="IDisposable"/>，只异步的资源（如 <c>IReadSession</c>）也收；不实现两者其一的资源在 Add 时直接抛。</description></item>
/// <item><term>聚合异常</term><description>Dispose 逐个释放、收集异常，全部尝试完后聚合抛 <see cref="AggregateException"/>（不因单个失败跳过其余，也不静默吞）。</description></item>
/// </list>
/// </summary>
public sealed class ResourceGroup : IDisposable, IAsyncDisposable
{
    /// <summary>资源条目——整合目标 + 所有权 + 添加时间戳 + Debug 注册栈（避免三个并行 list 脱节）。</summary>
    private sealed record ResourceEntry(object Target, ResourceOwnership Ownership, long AddedMs, string? DebugStack = null);

    // === 跟踪（对齐 lease 范式：实例级跟踪 + 诊断 + 泄漏可见）===
    /// <summary>组唯一标识（InstanceTracker 注册时生成，诊断用）。</summary>
    public Guid Id { get; }

    // === 存储（全部在 _lock 下访问）===
    // _byName：按名查（诊断/按名取）；_ordered：保插入序，Dispose 逆序释放。同一对象两处引用，不重复 Dispose。
    private readonly Dictionary<string, ResourceEntry> _byName = new(StringComparer.Ordinal);
    private readonly List<ResourceEntry> _ordered = [];
    private readonly object _lock = new();
    private int _disposed;  // 0=未释放, 1=已释放（CAS 防双释放）

    /// <summary>构造资源组（可构造期一次性传入若干资源，向旧 ResourceGroupOwner 用法兼容）。
    /// 构造期对象尚未暴露，无需加锁。
    /// <para>⚠️ <b>子类构造器警告</b>：构造期未加锁依赖"对象尚未暴露"——若子类在构造器里启动线程/回调
    ///   并暴露 <c>this.Resources</c>，可能并发访问未完成构造的实例。子类构造器严禁在 base 构造完成前
    ///   把 this 传给并发组件（对齐 .NET 构造器安全准则）。</para></summary>
    /// <param name="initial">构造期持有的资源——每个须实现 <see cref="IDisposable"/> 或 <see cref="IAsyncDisposable"/>（至少其一；按 Type.Name 自动命名，重名追加序号）。默认 <see cref="ResourceOwnership.Owned"/>。
    ///   不实现两接口的资源在此抛 <see cref="ArgumentException"/>。</param>
    public ResourceGroup(params object[] initial)
    {
        Id = InstanceTracker.Register(this, nameof(ResourceGroup));
        ArgumentNullException.ThrowIfNull(initial);
        foreach (var r in initial)
        {
            if (r is not (IDisposable or IAsyncDisposable))
                throw new ArgumentException(
                    $"资源必须实现 {nameof(IDisposable)} 或 {nameof(IAsyncDisposable)}（至少其一）", nameof(initial));
            AddCoreLocked(r, null, ResourceOwnership.Owned);
        }
    }

    // === 按名注册 ===

    /// <summary>添加资源（重名抛 <see cref="ArgumentException"/>）。
    /// <para>★ 泛型入口：<typeparamref name="TResource"/> 约束为 class（编译期类型安全）。
    ///   运行期校验至少实现 <see cref="IDisposable"/> 或 <see cref="IAsyncDisposable"/>（泛型约束无法表达"或"关系）。</para>
    /// <para>★ <see cref="IAsyncDisposable"/> 不继承 <see cref="IDisposable"/>：只异步的资源（如 <c>IReadSession</c>）也收。</para>
    /// <para>★ <paramref name="ownership"/>：默认 <see cref="ResourceOwnership.Owned"/>（Dispose 释放）；注入资源用 <see cref="ResourceOwnership.Referenced"/>（只跟踪诊断，Dispose 跳过）。</para>
    /// <para>★ 线程安全：用 <c>lock</c> 保护，可被后台恢复 task 与主线程并发调用。</para>
    /// </summary>
    /// <typeparam name="TResource">资源类型——须实现 IDisposable 或 IAsyncDisposable。</typeparam>
    /// <param name="resource">要添加的资源。</param>
    /// <param name="name">注册名；缺省用 <c>resource.GetType().Name</c>。</param>
    /// <param name="ownership">所有权模式（Owned=我释放 / Referenced=外部管只跟踪）。</param>
    public void Add<TResource>(TResource resource, string? name = null, ResourceOwnership ownership = ResourceOwnership.Owned)
        where TResource : class
    {
        // ★ 校验在锁外做（不持锁抛异常）+ ThrowIfDisposed 在锁内做（防 Add 与 Dispose 竞态）
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is not (IDisposable or IAsyncDisposable))
            throw new ArgumentException(
                $"资源 {typeof(TResource).Name} 必须实现 {nameof(IDisposable)} 或 {nameof(IAsyncDisposable)}（至少其一）",
                nameof(resource));
        lock (_lock)
        {
            ThrowIfDisposed();
            AddCoreLocked(resource, name, ownership);
        }
    }

    /// <summary>尝试添加（重名、已释放、或不实现两接口返回 false，不抛）。线程安全。</summary>
    public bool TryAdd<TResource>(TResource resource, string? name = null, ResourceOwnership ownership = ResourceOwnership.Owned)
        where TResource : class
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is not (IDisposable or IAsyncDisposable)) return false;
        lock (_lock)
        {
            if (IsDisposed) return false;
            var actualName = ResolveNameLocked(resource, name);
            var entry = new ResourceEntry(resource, ownership, Environment.TickCount64, CaptureRegisterStack());
            if (!_byName.TryAdd(actualName, entry)) return false;
            _ordered.Add(entry);
            return true;
        }
    }

    /// <summary>核心添加（已持锁；重名抛）。构造期 + Add 共用。</summary>
    private void AddCoreLocked(object resource, string? name, ResourceOwnership ownership)
    {
        var actualName = ResolveNameLocked(resource, name);
        var entry = new ResourceEntry(resource, ownership, Environment.TickCount64, CaptureRegisterStack());
        if (!_byName.TryAdd(actualName, entry))
            throw new ArgumentException($"资源名 '{actualName}' 已存在", nameof(name));
        _ordered.Add(entry);
    }

    /// <summary>Debug 下捕获注册调用栈（定位泄漏源）；Release 返回 null 零开销。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? CaptureRegisterStack()
    {
#if DEBUG
        return new StackTrace(2, false).ToString();  // 跳过本方法 + Add 调用帧
#else
        return null;
#endif
    }

    /// <summary>解析注册名（已持锁）：显式优先，缺省用 Type.Name，仍重名则追加序号。</summary>
    /// <param name="resource">资源对象。</param>
    /// <param name="name">显式注册名（可空）。</param>
    /// <returns>最终注册名（不为空，唯一）。</returns>
    private string ResolveNameLocked(object resource, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        var baseName = resource.GetType().Name;
        // 同 Type 多实例：追加 -2, -3...（首个不加序号）
        if (!_byName.ContainsKey(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!_byName.ContainsKey(candidate)) return candidate;
        }
    }

    // === 按名查询（线程安全）===

    /// <summary>按名取资源并转型；不存在或类型不匹配返回 null。线程安全。</summary>
    /// <typeparam name="T">资源类型（class 约束，编译期类型安全）。</typeparam>
    /// <param name="name">注册名。</param>
    /// <returns>资源实例或 null。</returns>
    public T? Get<T>(string name) where T : class
    {
        lock (_lock)
        {
            return _byName.TryGetValue(name, out var e) && e.Target is T typed ? typed : null;
        }
    }

    /// <summary>是否包含指定名。线程安全。</summary>
    /// <param name="name">注册名。</param>
    /// <returns>是否包含。</returns>
    public bool Contains(string name)
    {
        lock (_lock) { return _byName.ContainsKey(name); }
    }

    /// <summary>已注册的所有资源名快照（诊断/枚举用）。线程安全——返回快照副本，避免外部枚举期间被修改。</summary>
    public IReadOnlyList<string> Names
    {
        get { lock (_lock) { return [.. _byName.Keys]; } }
    }

    /// <summary>诊断：当前组内所有资源快照（对齐 lease GetActiveLeases——泄漏时定位"谁加了什么资源"）。
    /// 线程安全——锁内快照副本。含所有权模式（Owned/Referenced）。</summary>
    /// <returns>资源快照列表（按添加顺序）。</returns>
    public IReadOnlyList<ResourceInfo> GetResources()
    {
        lock (_lock)
        {
            var result = new ResourceInfo[_ordered.Count];
            for (var i = 0; i < _ordered.Count; i++)
            {
                var e = _ordered[i];
                result[i] = new ResourceInfo
                {
                    Name = FindNameLocked(e),
                    TypeName = e.Target.GetType().Name,
                    AddedTimestampMs = e.AddedMs,
                    Ownership = e.Ownership,
                    DebugStack = e.DebugStack,
                };
            }
            return result;
        }
    }

    /// <summary>反查资源名（已持锁）——诊断 GetResources 用。</summary>
    /// <param name="entry">资源条目。</param>
    /// <returns><![CDATA[资源名或 "<unknown>"（理论上不可能）。]]></returns>
    private string FindNameLocked(ResourceEntry entry)
    {
        foreach (var (name, e) in _byName)
            if (ReferenceEquals(e, entry)) return name;
        return "<unknown>";
    }

    // === Dispose 状态 ===

    /// <summary>
    /// 是否已释放（CAS 防双释放）。线程安全——Volatile.Read。
    /// </summary>
    private bool IsDisposed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _disposed) == 1;
    }

    /// <summary>已释放则抛 <see cref="ObjectDisposedException"/>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    // === Dispose（快照后释放 + 聚合异常 + 防双释放）===
    // ★ 在锁内取快照、锁外执行释放——避免 Dispose 回调（可能反调本组）死锁，且不让释放 IO 阻塞其他 Add。
    // ★ 只释放 Owned 资源——Referenced 资源（外部注入）跳过（调用方自管，避免双释放）。

    /// <inheritdoc/>
    /// <remarks>逆序释放（后加的先释放）；仅 <see cref="ResourceOwnership.Owned"/> 资源释放，<see cref="ResourceOwnership.Referenced"/> 跳过。
    /// 同步路径优先 <see cref="IDisposable"/>（防同步上下文死锁）。聚合异常展平。
    /// <para>★ 只实现 <see cref="IAsyncDisposable"/> 的资源在同步 Dispose 里阻塞等异步完成（语义与异步入口一致）。</para></remarks>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        var snapshot = SnapshotAndClear();
        var exs = DisposeCoreSync(snapshot);
        InstanceTracker.Unregister(this);  // ★ 正常释放——从跟踪表移除（泄漏检测：未移除=未 Dispose）
        if (exs is not null) throw new AggregateException("ResourceGroup Dispose 过程中发生一个或多个异常", exs);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        var snapshot = SnapshotAndClear();
        var exs = await DisposeCoreAsync(snapshot).ConfigureAwait(false);
        InstanceTracker.Unregister(this);
        if (exs is not null) throw new AggregateException("ResourceGroup DisposeAsync 过程中发生一个或多个异常", exs);
    }

    /// <summary>在锁内快照 Owned 资源列表（逆序，后加的在前）并清空存储。CAS 已置位，后续 Add 会因 IsDisposed 拒绝。
    /// ★ Referenced 资源不进快照（不释放），但仍从存储清空（组本身已 Dispose）。</summary>
    private List<object> SnapshotAndClear()
    {
        lock (_lock)
        {
            var snapshot = new List<object>(_ordered.Count);
            // 逆序快照（后加的在前——先释放）；仅 Owned 进快照
            for (var i = _ordered.Count - 1; i >= 0; i--)
            {
                var e = _ordered[i];
                if (e.Ownership == ResourceOwnership.Owned) snapshot.Add(e.Target);
            }
            _ordered.Clear();
            _byName.Clear();
            return snapshot;
        }
    }

    /// <summary>同步释放核心（已快照）：IDisposable 优先（防同步上下文死锁）、收集异常。锁外执行。
    /// <para>★ 同步路径优先 <see cref="IDisposable"/>——仅对只实现 <see cref="IAsyncDisposable"/> 的资源才阻塞等异步
    ///   （避免两接口都实现的资源在有 SynchronizationContext 的线程上 <c>GetAwaiter().GetResult()</c> 死锁）。</para>
    /// <para>★ 展平 <see cref="AggregateException"/>——避免 GetResult 抛的 AggregateException 与外层聚合嵌套。</para></summary>
    /// <param name="snapshot">快照列表（逆序，后加的在前）。</param>
    /// <returns>异常列表（展平 AggregateException），无异常返回 null。</returns
    private static List<Exception>? DisposeCoreSync(List<object> snapshot)
    {
        List<Exception>? exs = null;
        foreach (var r in snapshot)
        {
            try
            {
                // ★ IDisposable 优先；仅 IDisposable 不存在时才走 IAsyncDisposable（阻塞等）
                if (r is IDisposable d) d.Dispose();
                else if (r is IAsyncDisposable ad)
                {
#pragma warning disable TCSG031 // 设计必需：Dispose 必须同步完成（IDisposable 契约无 async 形态）
                    ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore TCSG031
                }
            }
            catch (Exception ex) { AddFlattened(exs ??= new(), ex); }
        }
        return exs;
    }

    /// <summary>异步释放核心（已快照）：IAsyncDisposable 优先（await）、收集异常。锁外执行。</summary>
    /// <param name="snapshot">快照列表（逆序，后加的在前）。</param>
    /// <returns>异常列表（展平 AggregateException），无异常返回 null。</returns>
    private static async Task<List<Exception>?> DisposeCoreAsync(List<object> snapshot)
    {
        List<Exception>? exs = null;
        foreach (var r in snapshot)
        {
            try
            {
                switch (r)
                {
                    case IAsyncDisposable ad:
                        await ad.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable d:
                        d.Dispose();
                        break;
                }
            }
            catch (Exception ex) { AddFlattened(exs ??= new(), ex); }
        }
        return exs;
    }

    /// <summary>展平异常收集——AggregateException 拆出 InnerExceptions，避免聚合嵌套（同步 Dispose 的 GetResult 会包一层）。</summary>
    /// <param name="exs">异常列表（可能 null，首次异常时创建）。</param>
    /// <param name="ex">新捕获的异常。</param>
    private static void AddFlattened(List<Exception> exs, Exception ex)
    {
        if (ex is AggregateException ae && ae.InnerExceptions.Count > 0)
            exs.AddRange(ae.InnerExceptions);
        else
            exs.Add(ex);
    }
}
