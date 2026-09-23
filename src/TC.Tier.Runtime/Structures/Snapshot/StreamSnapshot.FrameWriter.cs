
namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>
/// StreamSnapshot 帧写入 partial——嵌套 StreamFrameWriter（[Header 14B][Data][Footer 28B] 帧流式写 + CRC64 增量累积）。
/// </summary>
public sealed partial class StreamSnapshot
{
    /// <summary>
    /// 流式帧写入器。[Header 14B][Data][Footer 28B（Magic+TotalLength+EntryCount+UnifiedCrc64）]。
    /// CRC64 incremental 覆盖 Header + Data + Footer 前 20B（流式边写边累积，Complete 收官）。
    /// 多帧：Complete 后 ResetFrame，可再开新帧。
    /// </summary>
    public sealed class StreamFrameWriter : IAsyncDisposable
    {
        private readonly ISnapshotWriteSession _session;
        private readonly UnifiedCrc64 _hash = new();
        private long _totalDataLength;
        private long _entryCount;
        private bool _headerWritten;
        private bool _disposed;

        internal StreamFrameWriter(ISnapshotWriteSession session)
        {
            _session = session;
        }

        /// <summary>当前帧已写 data 总字节数（Complete/ResetFrame 后清零）。</summary>
        public long TotalDataLength => _totalDataLength;
        /// <summary>当前帧已写 entry 条数（Complete/ResetFrame 后清零）。</summary>
        public long EntryCount => _entryCount;

        /// <summary>异步写一个 entry（自动 EntryCount+1；CRC 边写边累积）。</summary>
        /// <param name="data">entry 数据字节（首次写自动加帧头）。</param>
        /// <param name="ct">取消令牌。默认 default。</param>
        /// <returns>表示写入完成的任务（写入会话快速路径同步完成）。</returns>
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            EnsureHeader();
            _hash.Append(data.Span);
            _totalDataLength += data.Length;
            _entryCount++;
            var writeTask = _session.WriteAsync(data, ct);
            if (writeTask.IsCompletedSuccessfully) return ValueTask.CompletedTask;
            return AwaitWriteSlow(writeTask);
        }

        private static async ValueTask AwaitWriteSlow(ValueTask writeTask)
            => await writeTask.ConfigureAwait(false);

        /// <summary>同步写一个 entry。</summary>
        /// <param name="data">entry 数据字节（首次写自动加帧头）。</param>
        public void Write(ReadOnlySpan<byte> data)
        {
            EnsureHeader();
            _hash.Append(data);
            _totalDataLength += data.Length;
            _entryCount++;
            _session.Write(data);
        }

        /// <summary>完成当前帧：写 Footer（CRC64 收官）+ flush + 重置（可开新帧）。</summary>
        /// <param name="ct">取消令牌。默认 default。</param>
        /// <returns>表示帧完成（Footer 已写 + 数据已 flush）的任务；帧计数器已重置。</returns>
        public async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            EnsureHeader();
            await WriteFooterAsync(ct).ConfigureAwait(false);
            await _session.FlushAsync(ct).ConfigureAwait(false);
            ResetFrame();
        }

        /// <summary>完成当前帧（同步轨）。</summary>
        public void Complete()
        {
            EnsureHeader();
            WriteFooterSync();
            _session.Flush();
            ResetFrame();
        }

        /// <summary>Dispose 自动闭环未完成的帧（对齐旧 StreamFrameWriter 语义——只 using 不 Complete 也安全）。</summary>
        /// <returns>表示释放完成的任务；未完成帧已 Complete 闭环，底层写会话已释放。</returns>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_headerWritten)
                await CompleteAsync().ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        // —— 内部 ——

        private void EnsureHeader()
        {
            if (_headerWritten) return;
            Span<byte> hdr = stackalloc byte[StreamFrameHeaderCodec.StructSize];
            // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量
            var header = StreamFrameHeaderCodec.Create();
            StreamFrameHeaderCodec.Write(hdr, in header);
            _hash.Append(hdr); // ★ CRC 累积 Header
            _session.WriteSmall(hdr);
            _headerWritten = true;
        }

        private void ResetFrame()
        {
            _headerWritten = false;
            _totalDataLength = 0;
            _entryCount = 0;
            _hash.Reset();
        }

        private async ValueTask WriteFooterAsync(CancellationToken ct)
        {
            await _session.FlushIfFullAsync(StreamFrameFooterCodec.StructSize, ct).ConfigureAwait(false);
            WriteFooterToSession();
        }

        private void WriteFooterSync()
        {
            _session.FlushIfFull(StreamFrameFooterCodec.StructSize);
            WriteFooterToSession();
        }

        /// <summary>写 Footer(28B)：前 20B（Magic+TotalLength+EntryCount）累积 CRC，末 8B 是结果。</summary>
        private void WriteFooterToSession()
        {
            Span<byte> footer = stackalloc byte[StreamFrameFooterCodec.StructSize];

            // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
            var frameFooter = StreamFrameFooterCodec.Create();
            frameFooter.TotalLength = (ulong)_totalDataLength;
            frameFooter.EntryCount = (ulong)_entryCount;
            frameFooter.Crc = 0; // 占位，回填
            StreamFrameFooterCodec.Write(footer, in frameFooter);
            _hash.Append(footer.Slice(0, StreamFrameFooterCodec.Offset_Crc)); // ★ CRC 累积 Footer 前 20B

            ulong crc = UnifiedCrc.FinalizeCrc64(_hash);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                footer.Slice(StreamFrameFooterCodec.Offset_Crc, StreamFrameFooterCodec.StructSize - StreamFrameFooterCodec.Offset_Crc), crc);

            _session.WriteSmall(footer);
        }
    }
}
