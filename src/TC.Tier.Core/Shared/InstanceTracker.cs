using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Shared;

/// <summary>
/// 实例跟踪器——WeakReference 跟踪表 + GetAlive 诊断 + 泄漏告警。
/// <para>★ 对齐 <c>LogicalAddressRegistry.LeaseRef</c>：不阻止 GC（弱引用），但能枚举发现泄漏。
///   核心哲学：底层绝不假设调用方遵守规则——忘 Dispose 的实例能被发现、能诊断。</para>
/// <para>★ 用 <see cref="ConditionalWeakTable{TKey,TValue}"/>：key 弱引用，实例被 GC 时条目自动移除
///   （无需手动清理，跟踪器自身不造成泄漏）。</para>
/// </summary>
internal static class InstanceTracker
{
    /// <summary>跟踪条目 sidecar（对齐 lease 的 LeaseDiagnostics 思路——避免污染实例自身字段）。
    /// ★ 不存任何捕获实例的委托/引用——避免强引用破坏 ConditionalWeakTable 弱引用语义。
    ///   实时状态查询走实例自身的 RecoveryState 属性，不在跟踪器里（跟踪器只做"谁还活着"诊断）。</summary>
    private sealed class TrackedInfo
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly long CreatedTimestampMs = Environment.TickCount64;
        internal string TypeName = "";
    }

    // ★ ConditionalWeakTable<object, TrackedInfo>：key 弱引用 + 自动清理。
    //   实例被 GC → 条目被移除（无 finalize 回调，但条目本身不阻止 GC，跟踪器零泄漏）。
    private static readonly ConditionalWeakTable<object, TrackedInfo> Alive = new();
    private static readonly object Lock = new();

    /// <summary>注册实例（构造器调）。返回跟踪 Id。</summary>
    /// <param name="instance">要跟踪的实例。</param>
    /// <param name="typeName">类型名（诊断用）。</param>
    /// <returns>跟踪 Id（可用于诊断日志/告警）。</returns>
    internal static Guid Register(object instance, string typeName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var info = new TrackedInfo { TypeName = typeName };
        lock (Lock) { Alive.GetOrCreateValue(instance); Alive.AddOrUpdate(instance, info); }
        return info.Id;
    }

    /// <summary>注销实例（Dispose 调）——标记正常释放。返回是否注销成功（未注册返回 false）。</summary>
    /// <param name="instance">要注销的实例。</param>
    /// <returns>是否注销成功（未注册返回 false）。</returns>
    internal static bool Unregister(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (Lock)
        {
            return Alive.Remove(instance);
        }
    }

    /// <summary>获取当前所有存活（未 GC）的跟踪实例快照（对齐 GetActiveLeases）。</summary>
    /// <param name="typeFilter">可选类型名过滤（子串匹配，null = 全部）。</param>
    /// <returns>当前所有存活的跟踪实例快照。</returns>
    /// <remarks>★ ConditionalWeakTable 不支持直接枚举（无原子快照），用 ForEach 兜底——
    ///   枚举期间实例可能被 GC（条目消失），返回的是尽力而为快照（诊断场景可接受）。
    ///   State 字段不填（实时状态走实例自身属性，跟踪器不持有捕获实例的委托——防强引用泄漏）。</remarks>
    internal static IReadOnlyList<TrackedInstanceInfo> GetAlive(string? typeFilter = null)
    {
        var result = new List<TrackedInstanceInfo>();
        // ConditionalWeakTable.GetEnumerator 必须在锁内或防重入（文档要求调用方同步）
        lock (Lock)
        {
            foreach (var (key, info) in Alive)
            {
                if (typeFilter is not null && !info.TypeName.Contains(typeFilter, StringComparison.Ordinal))
                    continue;
                result.Add(new TrackedInstanceInfo
                {
                    Id = info.Id,
                    CreatedTimestampMs = info.CreatedTimestampMs,
                    TypeName = info.TypeName,
                });
            }
        }
        return result;
    }
}