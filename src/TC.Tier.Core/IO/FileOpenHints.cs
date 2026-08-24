namespace TC.Tier.Core.IO;

/// <summary>
/// 打开提示轴——缓存策略与访问模式提示（正交可叠加）。
/// <para>★ <see cref="NoBuffering"/> 与 <see cref="WriteThrough"/> 可叠加（DIO + 写透）。</para>
/// </summary>
[Flags]
public enum FileOpenHints
{
    /// <summary>无提示（默认缓冲 IO）。</summary>
    None = 0,

    /// <summary>绕过 OS 页缓存（Win FILE_FLAG_NO_BUFFERING / Linux O_DIRECT）——三重对齐强制。</summary>
    NoBuffering = 1,

    /// <summary>写透（FILE_FLAG_WRITE_THROUGH / O_SYNC）。</summary>
    WriteThrough = 2,

    /// <summary>顺序扫描提示（顺序预取优化）。</summary>
    SequentialScan = 4,

    /// <summary>随机访问提示（禁用预读）。</summary>
    RandomAccess = 8,
}