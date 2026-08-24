using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// FILE_STANDARD_INFO——GetFileInformationByHandleEx(FileStandardInfo) 的输出结构体。
/// <para>布局对齐 Windows SDK winnt.h FILE_STANDARD_INFO（AllocatedSize + EndOfFile + NumberOfLinks + DeletePending + Directory）。</para>
/// <para>★ <see cref="AllocatedSize"/>：文件在磁盘上**真实分配**的字节数（区分稀疏/预分配空洞，
///   空洞区域不计数）；<see cref="EndOfFile"/>：逻辑大小（等同 FileInfo.Length）。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileStandardInfo
{
    /// <summary>文件在磁盘上真实分配的字节数（区分预分配/稀疏空洞）。</summary>
    public long AllocatedSize;
    /// <summary>逻辑大小（等同 FileInfo.Length，含预分配空洞）。</summary>
    public long EndOfFile;
    /// <summary>硬链接数。</summary>
    public uint NumberOfLinks;
    /// <summary>是否标记删除（待关闭句柄后删）。</summary>
    public int DeletePending;
    /// <summary>是否目录。</summary>
    public int Directory;
}
