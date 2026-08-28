namespace TC.Tier.Runtime.Structures.SortedIndex;

public abstract partial class SortedIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>同步额外清理钩子（LifecycleBase 模板）——比较族无引擎外原生资源（引擎/arena 归 Resources 统一释放），空实现。</summary>
    /// <param name="disposing">true = 用户调 Dispose（可触托管资源）。</param>
    protected override void DisposeOverride(bool disposing)
    {
    }

    /// <summary>异步额外清理钩子（LifecycleBase 模板）——对等 <see cref="DisposeOverride(bool)"/> 的异步轨，空实现。</summary>
    /// <param name="disposing">true = 用户调 DisposeAsync（可触托管资源）。</param>
    /// <returns>释放完成的 ValueTask。</returns>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await ValueTask.CompletedTask;
    }
}
