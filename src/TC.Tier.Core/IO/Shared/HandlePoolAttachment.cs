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

    public void Trace(string op)
    {
        var i = Interlocked.Increment(ref _opsIdx) - 1;
        _ops[i % _ops.Length] = (op, Environment.CurrentManagedThreadId, Volatile.Read(ref Usage));
    }

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Trace(string op)
    {
    }

    public string Dump() => string.Empty;
#endif
}