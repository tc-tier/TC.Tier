namespace TC.Tier.Runtime.Structures;

/// <summary>
/// 游标骨架基类——提供 Direction 存储 + MoveNextAsync 的同步委托默认实现。
/// <para>各结构游标类继承本类，只 override MoveNext + 扩展 Current*/结构专属成员。
/// 有真异步 I/O 的结构 override MoveNextAsync；否则用默认同步委托。</para>
/// </summary>
public abstract class StructureScanCursorBase : IStructureScanCursor
{
    /// <summary>创建游标骨架。</summary>
    protected StructureScanCursorBase(ReadDirection direction) => Direction = direction;

    /// <inheritdoc/>
    public ReadDirection Direction { get; }

    /// <inheritdoc/>
    public abstract bool MoveNext();

    /// <inheritdoc/>
    /// <remarks>默认实现：同步委托 MoveNext。有真异步 I/O 的子类 override。</remarks>
    public virtual ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
        => new(MoveNext());

    /// <inheritdoc/>
    public abstract void Dispose();

    /// <inheritdoc/>
    public abstract ValueTask DisposeAsync();
}