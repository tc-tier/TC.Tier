namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段内区间记录。
/// <para>★ State 是 byte（ExtentStateCode 编码：高4bit=Src + 低4bit=Phase），不是 enum。</para>
/// <para>★ LeaseRef 已移除——诊断跟踪移到段表诊断表（见 src/TC.Tier.Runtime/docs/segment-table.md 诊断接口）。</para>
/// </summary>
public struct ExtentRecord
{
    /// <summary>
    /// 区间起始位置（包含）。
    /// </summary>
    internal readonly long Start;
    /// <summary>
    /// 区间结束位置（不包含）。
    /// </summary>
    internal readonly long End;
    /// <summary>
    /// 区间状态（ExtentStateCode 编码 byte）。
    /// </summary>
    internal byte State;
    /// <summary>
    /// 区间版本。
    /// </summary>
    internal readonly int Version;
    /// <summary>
    /// 是否为稀疏区间。
    /// </summary>
    internal bool Sparse;

    /// <summary>
    /// 初始化一个新的 <see cref="ExtentRecord"/> 实例。
    /// </summary>
    /// <param name="start">区间起始位置（包含）。</param>
    /// <param name="end">区间结束位置（不包含）。</param>
    /// <param name="state">区间状态（ExtentStateCode 编码 byte）。</param>
    /// <param name="sparse">是否为稀疏区间。</param>
    /// <param name="version">区间版本。</param>
    internal ExtentRecord(long start, long end, byte state, bool sparse = false, int version = 0)
    {
        Start = start;
        End = end;
        State = state;
        Version = version;
        Sparse = sparse;
    }
}
