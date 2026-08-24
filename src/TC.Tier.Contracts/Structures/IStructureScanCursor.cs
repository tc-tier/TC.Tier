namespace TC.Tier.Contracts.Structures;

/// <summary>
/// 表示一个结构扫描游标，用于在数据结构中进行顺序扫描。
/// </summary>
public interface IStructureScanCursor : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 游标的扫描方向。
    /// </summary>
    ReadDirection Direction { get; }

    /// <summary>
    /// 推进到下一块，成功返回 true，失败/到末尾返回 false。
    /// </summary>
    /// <returns>如果成功推进到下一块，返回 true；如果失败或到达末尾，返回 false。</returns>
    bool MoveNext();

    /// <summary>
    /// 推进到下一块，成功返回 true，失败/到末尾返回 false。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，可用于取消异步操作。</param>
    /// <returns>如果成功推进到下一块，返回 true；如果失败或到达末尾，返回 false。</returns>
    ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default);
}