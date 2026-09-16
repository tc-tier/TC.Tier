using System.Buffers;
using System.Threading;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// Witness 高水位存储（二期-F2——DDR-F2）：witness 节点的 <see cref="IRaftStore"/> 形态。
/// <para>★ 只持久化「日志存在性证明」——高水位对 (LastIndex, LastTerm) + (Term, VotedFor)
/// 选举契约；日志体/状态机/快照全部缺席（witness 永不成为 leader，故永不提供日志体，
/// 永不导出/安装快照——无体日志不会被读取）。</para>
/// <para>★ 本实现为内存版（测试/嵌入式/内存形态）；生产持久化（高水位断电丢失 = 退回
/// 旧高水位，投票保守方向安全但可能阻塞当选——建议装配方 fsync 持久化）归装配方按
/// 本接口封装。</para>
/// </summary>
public sealed class WitnessHighWaterStore : IRaftStore
{
    private readonly object _lock = new();
    private long _term;
    private NodeId _votedFor;
    private long _highIndex;    // 高水位 index（单调不降）
    private long _highTerm;     // 高水位记录任期（推进时的 leader term——投票比较口径）

    /// <inheritdoc/>
    public long Term { get { lock (_lock) return _term; } }

    /// <inheritdoc/>
    public NodeId VotedFor { get { lock (_lock) return _votedFor; } }

    /// <summary>恒 0——witness 无状态机。</summary>
    public long AppliedIndex => 0;

    /// <summary>高水位 index。</summary>
    public long LastLogIndex { get { lock (_lock) return _highIndex; } }

    /// <summary>高水位 term。</summary>
    public long LastLogTerm { get { lock (_lock) return _highTerm; } }

    /// <summary>= 高水位。</summary>
    public long AllocatedIndex { get { lock (_lock) return _highIndex; } }

    /// <summary>= 高水位（断言推进即视为持久化完成——内存版无 fsync 窗口）。</summary>
    public long PersistedIndex { get { lock (_lock) return _highIndex; } }

    /// <summary>恒 0——无快照。</summary>
    public long SnapshotIndex => 0;

    /// <summary>无恢复面（内存版——持久化版本在此恢复高水位）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>初始化完成即结束（内存版同步完成）。</returns>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>选举契约（原子抬任期/投票——应答前落盘；内存版锁内直改）。</summary>
    /// <param name="term">任期（单调不降——低于当前任期抛）。</param>
    /// <param name="votedFor">本任期投票对象（Empty = 清除）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入生效即完成（内存版无 fsync 窗口；生产持久化版本应答前落盘）。</returns>
    /// <exception cref="InvalidOperationException">term 低于当前任期。</exception>
    public ValueTask WriteTermAndVoteAsync(long term, NodeId votedFor, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (term < _term)
                throw new InvalidOperationException($"任期回退：{term} < {_term}。");
            _term = term;
            _votedFor = votedFor;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>无状态机——no-op。</summary>
    /// <param name="index">已应用 index（witness 无状态机——忽略）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>恒同步完成（no-op）。</returns>
    public ValueTask UpdateAppliedIndexAsync(long index, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>
    /// witness 断言推进（二期-F2 核心）：内容丢弃、只推高水位（index 严格更高且 term ≥
    /// 记录任期才推进——单调不回退）。
    /// </summary>
    /// <param name="index">Leader 日志前沿 index。</param>
    /// <param name="term">Leader 任期（高水位记录的任期口径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 高水位已推进；false = 未推进（落后/同位/任期旧——单调不回退）。</returns>
    public ValueTask<bool> AssertHighWatermarkAsync(long index, long term, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (index <= _highIndex || term < _highTerm)
                return new ValueTask<bool>(false);
            _highIndex = index;
            _highTerm = term;
        }
        return new ValueTask<bool>(true);
    }

    /// <summary>无日志体——诚实失败优于静默（断言流走 <see cref="AssertHighWatermarkAsync"/>，
    /// 不经本方法）。</summary>
    /// <param name="prevLogIndex">前置 index（无日志体——不适用）。</param>
    /// <param name="prevLogTerm">前置 term（无日志体——不适用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无返回——恒抛（PrevLog 校验在引擎 witness 分支被跳过，不经本方法）。</returns>
    /// <exception cref="NotSupportedException">恒抛——witness 无日志体。</exception>
    public ValueTask<bool> PrevLogMatchesAsync(long prevLogIndex, long prevLogTerm, CancellationToken ct = default)
        => throw new NotSupportedException("witness 无日志体（PrevLog 校验在引擎 witness 分支被跳过——DDR-F2）。");

    /// <summary>index ≤ 高水位的记录任期 = 高水位 term；越界抛。</summary>
    /// <param name="index">条目 index（0 ≤ index ≤ 高水位——越界抛）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>该 index 的记录任期（index == 高水位 = 高水位 term；index &lt; 高水位 = 0——只存高水位对，无逐条 term）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">index 越界（&lt; 0 或 &gt; 高水位）。</exception>
    public ValueTask<long> ReadLogTermAsync(long index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (index < 0 || index > _highIndex)
                throw new ArgumentOutOfRangeException(nameof(index), $"index {index} 超出高水位 {_highIndex}。");
            return new ValueTask<long>(index == _highIndex ? _highTerm : 0);
        }
    }

    /// <summary>no-op（高水位单调——无截尾语义）。</summary>
    /// <param name="indexInclusive">起始删除 index（忽略——高水位不回退）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>恒同步完成（no-op）。</returns>
    public ValueTask TruncateSuffixFromAsync(long indexInclusive, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>no-op（无日志体可截）。</summary>
    /// <param name="indexInclusive">删除至该 index（含）——忽略（无日志体可截）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>恒同步完成（no-op）。</returns>
    public ValueTask TruncatePrefixToAsync(long indexInclusive, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>无日志体——witness 的条目到达走引擎 witness 分支（内容不落盘）。</summary>
    /// <param name="prevIndex">前置 index（断言匹配点——无日志体，不适用）。</param>
    /// <param name="entries">条目（(Term, Kind, Content) 三元组——内容不落盘）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无返回——恒抛（条目到达走 <see cref="AssertHighWatermarkAsync"/> 断言流）。</returns>
    /// <exception cref="NotSupportedException">恒抛——witness 无日志体。</exception>
    public ValueTask<long> AppendAsync(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
        => throw new NotSupportedException("witness 无日志体（Append 走 AssertHighWatermark 断言流——DDR-F2）。");

    /// <summary>无日志体。</summary>
    /// <param name="index">条目 index（无日志体——不适用）。</param>
    /// <param name="term">条目 term（不写出）。</param>
    /// <param name="kind">条目种类（不写出）。</param>
    /// <param name="content">条目内容（不写出）。</param>
    /// <returns>无返回——恒抛。</returns>
    /// <exception cref="NotSupportedException">恒抛——witness 无日志体。</exception>
    public bool TryGetEntry(long index, out long term, out byte kind, out ReadOnlyMemory<byte> content)
        => throw new NotSupportedException("witness 无日志体。");

    /// <summary>无日志体。</summary>
    public IEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> Scan(long startIndex, int maxCount)
        => throw new NotSupportedException("witness 无日志体。");

    /// <summary>无日志体——witness 永非 leader（不读日志补复制）。</summary>
    public IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadEntriesAsync(long startIndex, CancellationToken ct = default)
        => throw new NotSupportedException("witness 无日志体。");

    /// <summary>witness 永非 leader。</summary>
    /// <param name="startIndex">起始 index（不适用——witness 永非 leader）。</param>
    /// <param name="maxCount">至多计数条数（不适用）。</param>
    /// <returns>无返回——恒抛（无日志体）。</returns>
    /// <exception cref="NotSupportedException">恒抛——witness 无日志体。</exception>
    public int CountEntries(long startIndex, int maxCount)
        => throw new NotSupportedException("witness 无日志体。");

    /// <summary>witness 永非 leader。</summary>
    /// <param name="startIndex">起始 index（不适用——witness 永非 leader）。</param>
    /// <param name="count">至多写入条数（不适用）。</param>
    /// <param name="writer">帧缓冲视图（不适用）。</param>
    /// <param name="terms">term 回填数组（不适用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无返回——恒抛（无日志体）。</returns>
    /// <exception cref="NotSupportedException">恒抛——witness 无日志体。</exception>
    public ValueTask<int> WriteEntriesToAsync(long startIndex, int count, IBufferWriter<byte> writer, long[] terms, CancellationToken ct = default)
        => throw new NotSupportedException("witness 无日志体。");

    /// <summary>即时完成（内存版无持久化窗口）。</summary>
    /// <param name="index">目标 index（断言推进即视为持久化完成——同步通过）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>即时完成（内存版无 fsync 窗口）。</returns>
    public ValueTask WaitForPersistedAsync(long index, CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>无日志体——witness 不导出快照。</summary>
    public IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadSnapshotEntriesAsync(long snapshotIndex, CancellationToken ct = default)
        => throw new NotSupportedException("witness 不导出快照（无日志体/无状态机——DDR-F2）。");

    /// <summary>无状态机——witness 拒绝快照安装。</summary>
    /// <param name="snapshotIndex">快照覆盖点（不适用——witness 无状态机）。</param>
    /// <param name="entries">快照条目流（不消费）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>无返回——恒抛。</returns>
    /// <exception cref="NotSupportedException">恒抛——witness 无状态机，不安装快照。</exception>
    public ValueTask ImportSnapshotAsync(long snapshotIndex, IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
        => throw new NotSupportedException("witness 不安装快照（无状态机——DDR-F2）。");

    /// <summary>无日志体——no-op。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>恒同步完成（no-op）。</returns>
    public ValueTask RefreshTailAfterImportAsync(CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
