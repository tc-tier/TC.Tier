using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace.Leases;

public abstract partial class LeaseBase
{
    /// <summary>
    /// chunk 按下标访问（含物理门）——for 循环用：
    /// <c>for (var i = 0; i &lt; lease.ChunkCount; i++) { var chunk = lease[i]; IO(chunk); chunk.Commit(); }</c>
    /// <para>★ 物理门与流水线第一拍（<see cref="ChunkEnumerator.MoveNext"/>）同一道——按下标访问同样过门。</para>
    /// </summary>
    /// <param name="index">chunk 下标（[0, <see cref="ChunkCount"/>)）。</param>
    /// <exception cref="ArgumentOutOfRangeException">index 越界（lease 已 ReleaseExtents 后 ChunkCount=0）。</exception>
    public ChunkScope this[int index]
    {
        get
        {
            if ((uint)index >= (uint)ChunkCountInternal)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"chunk 下标越界：{index}，ChunkCount={ChunkCountInternal}");
            var ext = ExtentsInternal[index];
            EnterChunkPhysicalGate(ext);
            return new ChunkScope(this, ext, index);
        }
    }

    /// <summary>
    /// lease 区间块的 struct 迭代器——零分配，保留延迟模型。
    /// <para>★ 持 <see cref="LeaseBase"/> 引用，MoveNext 过类型化物理门后由 <see cref="Current"/> 产出
    ///   <see cref="ChunkScope"/>（几何 + 分段 Commit/Rollback）——foreach 完整迭代器模式。</para>
    /// </summary>
    public struct ChunkEnumerator
    {
        private readonly LeaseBase _lease;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ChunkEnumerator(LeaseBase lease)
        {
            _lease = lease;
            _index = -1;
        }

        /// <summary>当前 chunk（几何 + 分段 Commit/Rollback 的 <see cref="ChunkScope"/>）。</summary>
        public readonly ChunkScope Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var ext = _lease.ExtentsInternal[_index];
                return new ChunkScope(_lease, ext, _index);
            }
        }

        /// <summary>前进到下一个 chunk（过类型化物理门后可用 <see cref="Current"/> 产出）。</summary>
        /// <returns>true = 已推进到下一个 chunk；false = 遍历结束。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var next = _index + 1;
            if (next >= _lease.ChunkCountInternal) return false;
            _lease.EnterChunkPhysicalGate(
                _lease.ExtentsInternal[next]); // ★ 类型化物理门（Append/Write 等 Empty→Ready；Reclaim 系无门）
            _index = next;
            return true;
        }

        /// <summary>提交当前 chunk（doneMask 仲裁——与其他路径 exactly-once）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CommitCurrent() => _lease.OnChunkCommit(_index);

        /// <summary>回滚当前 chunk（doneMask 仲裁——与其他路径 exactly-once）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RollbackCurrent() => _lease.OnChunkRollback(_index);
    }
}
