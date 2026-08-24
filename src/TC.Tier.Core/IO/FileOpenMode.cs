namespace TC.Tier.Core.IO;

/// <summary>
/// 打开模式轴——文件存在性处置（BCL <see cref="FileMode"/> 对应）。
/// <para>★ <see cref="Append"/> 仅控制句柄游标初始位置（EOF），<b>不</b>施加强制追加——
///   与 BCL FileMode.Append / POSIX O_APPEND 均不同（后者已裁决不在本层）；多写者顺序追加用句柄 Append()。</para>
/// </summary>
public enum FileOpenMode
{
    /// <summary>已存在则打开，不存在抛 <see cref="IOError.NotFound"/>。</summary>
    OpenExisting,

    /// <summary>已存在则打开，不存在则创建。</summary>
    OpenOrCreate,

    /// <summary>仅新建——已存在抛 <see cref="IOError.AlreadyExists"/>。</summary>
    CreateNew,

    /// <summary>打开并截断到 0（已存在要求写权限；不存在的行为依介质=OpenOrCreate 截断语义）。</summary>
    Truncate,

    /// <summary>打开（不存在则创建）且游标初始化于 EOF。</summary>
    Append,
}