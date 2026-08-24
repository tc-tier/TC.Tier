namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 快照读取器——上层 pull 数据从 Ring 导出（快照导出用）。
/// <para>★ 页级流式：caller 每次 Read 只取自己 buffer 大小的数据，处理完再 Read 下一块，支持 GB/TB。</para>
/// <para>★ 冷热透明：内部按 FlushedUntilAddress 分流热区（内存页池取）/冷区（设备读），对外统一连续字节流。</para>
/// <para>★ 压缩/保存是上层的事：caller 在自己的 Read 循环里套压缩 Stream / 文件 / 网络，Ring 输出原始字节。</para>
/// <para>★ 对齐 WAL 的 SnapshotReader 范式（pull 模式，IAsyncDisposable）。</para>
/// <para>参见 base.md §2.10。</para>
/// </summary>
public interface IRingSnapshotReader : IDisposable, IAsyncDisposable
{
    /// <summary>快照数据总字节长度（= end - begin）。</summary>
    long Length { get; }

    /// <summary>
    /// 同步：填充 caller buffer，返回读取字节数，0 = EOF。
    /// <para>caller 可用任意大小 buffer（页级或更大），内部按需从热区内存/冷区设备拷贝。</para>
    /// </summary>
    /// <param name="buffer">caller 提供的目标缓冲区。</param>
    /// <returns>实际读取字节数（&lt; buffer.Length 表示末尾不足一块或 EOF）；0 = 已到末尾。</returns>
    int Read(Span<byte> buffer);

    /// <summary>
    /// 异步：填充 caller buffer。
    /// <para>冷区走 ReadDevicePageAsync（IOCP），热区同步拷贝（纯内存）。</para>
    /// </summary>
    /// <param name="buffer">caller 提供的目标缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际读取字节数；0 = EOF。</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}
