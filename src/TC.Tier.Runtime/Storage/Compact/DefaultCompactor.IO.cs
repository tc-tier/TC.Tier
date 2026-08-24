using TC.Tier.Core.Primitives;
namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor
{
    // ═══════════════════════════════════════════════════════════════
    //  IO handle 工厂（fs.Open 短命句柄——现造现弃，不入池）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 打开源段只读 IFileHandle（现造现弃——fs.Open 短命句柄，不入池）。
    /// <para>★ 恒缓冲读（搬迁 chunk 边界非扇区对齐，DIO 校验必失败——与旧临时段同纪律）。</para>
    /// </summary>
    private IFileHandle OpenSourceHandle(int segId)
        => _fileSystem.Open(GetSegmentPath(segId), new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
        });

    /// <summary>
    /// 创建临时段 IFileHandle（可写+预分配——open 即幂等预分配）。
    /// <para>★ 恒缓冲（Compact 搬迁的 chunk 边界是 lease 区间，非扇区对齐，DIO 校验必失败）；
    ///   恒无 WriteThrough（短命文件）。</para>
    /// </summary>
    private IFileHandle CreateTempHandle(int segId, long preallocateSize)
    {
        var handle = _fileSystem.Open(GetTempPath(segId), new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
            PreallocateSize = preallocateSize > 0 ? preallocateSize : 0,
        });
        lock (_tempHandles) _tempHandles[segId] = handle;
        return handle;
    }

    /// <summary>RangeCompact 使用 buffered 源句柄，支持任意非对齐 from/to。</summary>
    private IFileHandle OpenRangeSourceHandle(int segId)
        => _fileSystem.Open(GetSegmentPath(segId), new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
        });
}
