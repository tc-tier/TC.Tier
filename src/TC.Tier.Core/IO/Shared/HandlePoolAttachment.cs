using System.Runtime.CompilerServices;

namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 句柄池附件——使用权计数与池归属（挂在句柄上的内建协议，零包装器/零热路径分配）。
/// <para>★ Dispose 语义按挂载分叉：池内句柄 Dispose = <b>归还使用权</b>（usage--，资源不动——
///   using 成为安全惯用法）；池外句柄（fs.Open 直开）Dispose = 关闭资源。</para>
/// <para>★ Debug 绊线（Core 哲学：协议违反立即爆）：计数下溢（多还）抛异常并携带借还历史环形记录。</para>
/// </summary>
internal sealed class HandlePoolAttachment
{
    public FileHandlePool? Pool;
    public int Usage;

#if DEBUG
    private readonly (string Op, int Tid, int Usage)[] _ops = new (string, int, int)[16];
    private int _opsIdx;

    /// <summary>记录一次借还操作（Debug 环形示波器——下溢诊断现场；Release 版 no-op）。</summary>
    /// <param name="op">操作名（如 acquire-hit/release）。</param>
    public void Trace(string op)
    {
        var i = Interlocked.Increment(ref _opsIdx) - 1;
        _ops[i % _ops.Length] = (op, Environment.CurrentManagedThreadId, Volatile.Read(ref Usage));
    }

    /// <summary>导出借还历史（新→旧，环形缓冲上限内）——计数下溢异常的现场附件。</summary>
    /// <returns>多行诊断字符串（每行 = 一次操作 + 线程 id + 当时计数）。</returns>
    public string Dump()
    {
        var sb = new System.Text.StringBuilder();
        var end = Volatile.Read(ref _opsIdx);
        var start = Math.Max(0, end - _ops.Length);
        for (var i = start; i < end; i++)
        {
            var e = _ops[i % _ops.Length];
            sb.Append($"\n  [{i}] {e.Op} T{e.Tid} usage={e.Usage}");
        }
        return sb.ToString();
    }
#else
    /// <summary>记录一次借还操作（Release 版 no-op）。</summary>
    /// <param name="op">操作名（被忽略）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string op)
    {
    }

    /// <summary>导出借还历史（Release 版恒空——无示波器）。</summary>
    /// <returns>空字符串。</returns>
    public string Dump() => string.Empty;
#endif
}