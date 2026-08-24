namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 文件系统对无缓冲（unbuffered / direct）I/O 的实际支持程度——平台中性的底层探测结果。
/// <para>★ 归属 NativeInterop 层（internal）：描述底层文件系统物理能力，由
///   <see cref="FileNative"/> 探测得出。**不公开**——上层 Device 层消费时通过映射转成公开的
///   <c>DirectIoMode</c>，避免下层（NativeInterop）反向依赖上层（Device）的层泄漏。</para>
/// <para>★ 三态语义：</para>
/// <list type="bullet">
/// <item><see cref="Supported"/>：文件系统真正支持 unbuffered I/O（Windows NTFS/ReFS、Linux ext4/xfs/btrfs），
///   open 成功且 flag 实际生效。</item>
/// <item><see cref="BestEffort"/>：文件系统接受 unbuffered hint 但不保证（macOS F_NOCACHE、未知 FS），
///   对齐非强制。</item>
/// <item><see cref="Ignored"/>：文件系统忽略 unbuffered flag（Linux overlayfs/tmpfs/ramfs 等容器常见 FS、
///   Windows 网络重定向器/SMB），open 成功但 flag 被静默吞掉——走 page cache。</item>
/// <item><see cref="NotRequested"/>：上层未请求禁用缓冲（disableBuffering=false），无需探测。</item>
/// </list>
/// </summary>
public enum UnbufferedIoSupport
{
    /// <summary>上层未请求禁用缓冲——无需探测，调用方应直接走 buffered 路径。</summary>
    NotRequested,

    /// <summary>文件系统真正支持 unbuffered I/O，flag 实际生效（强制对齐）。</summary>
    Supported,

    /// <summary>文件系统接受 hint 但不保证（对齐非强制，尽力而为）。</summary>
    BestEffort,

    /// <summary>文件系统忽略 flag（open 成功但走 page cache）——容器常见场景。</summary>
    Ignored,
}
