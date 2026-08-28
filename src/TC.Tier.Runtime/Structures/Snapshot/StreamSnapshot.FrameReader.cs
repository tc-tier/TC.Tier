using System.Buffers;
using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Snapshot;

public sealed partial class StreamSnapshot
{
    /// <summary>
    /// 流式帧读取器。ReadDataAsync 读 data 至 EOF；footer CRC64 增量校验（读多少算多少，不驻内存整块）。
    /// </summary>
    public sealed class StreamFrameReader : IAsyncDisposable
    {
        private const int StreamChunkSize = 64 * 1024;

        private readonly ISnapshotReadSession _session;
        private readonly Crc64 _hash = new();
        private readonly byte[] _headerBuffer = new byte[StreamFrameHeaderCodec.StructSize];
        private readonly long _dataAvailable;
        private long _dataRead;
        private bool _headerParsed;
        private bool _footerParsed;
        private bool _footerValid;
        private long _entryCount;
        private long _totalLength;
        private ulong _storedChecksum;
        private bool _disposed;

        internal StreamFrameReader(ISnapshotReadSession session, long logicalLength)
        {
            _session = session;
            _dataAvailable = logicalLength - StreamFrameHeaderCodec.StructSize - StreamFrameFooterCodec.StructSize;
        }

        /// <summary>footer 是否已解析且 CRC64 校验通过（读至 data 末尾自动解析，解析前恒 false）。</summary>
        public bool IsFooterValid => _footerValid;
        /// <summary>帧内 entry 条数（footer 解析前为 0）。</summary>
        public long EntryCount => _entryCount;
        /// <summary>帧内 data 总长度（footer 解析前为 0）。</summary>
        public long TotalLength => _totalLength;
        /// <summary>footer 存储的 CRC64 校验和（footer 解析前为 0）。</summary>
        public ulong StoredChecksum => _storedChecksum;

        /// <summary>读 data（EOF 后返回 0；自动解析并校验 footer CRC64）。</summary>
        public async ValueTask<int> ReadDataAsync(Memory<byte> dest, CancellationToken ct = default)
        {
            if (!_headerParsed)
                await ParseHeaderAsync(ct).ConfigureAwait(false);

            long dataRemaining = _dataAvailable - _dataRead;
            if (dataRemaining <= 0)
            {
                if (!_footerParsed)
                    await ParseFooterAsync(ct).ConfigureAwait(false);
                return 0;
            }

            int toRead = (int)Math.Min(dest.Length, dataRemaining);
            int n = await _session.ReadAsync(dest[..toRead], ct).ConfigureAwait(false);
            if (n == 0) return 0;

            _hash.Append(dest.Span[..n]);
            _dataRead += n;

            if (_dataRead >= _dataAvailable && !_footerParsed)
                await ParseFooterAsync(ct).ConfigureAwait(false);

            return n;
        }

        /// <summary>读全部 data 为 chunk 流。</summary>
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllChunksAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            byte[] buf = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
            try
            {
                int n;
                while ((n = await ReadDataAsync(buf.AsMemory(0, StreamChunkSize), ct).ConfigureAwait(false)) > 0)
                    yield return buf.AsMemory(0, n);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private async ValueTask ParseHeaderAsync(CancellationToken ct)
        {
            byte[] hdr = _headerBuffer;
            int totalRead = 0;
            while (totalRead < StreamFrameHeaderCodec.StructSize)
            {
                int n = await _session.ReadAsync(
                    hdr.AsMemory(totalRead, StreamFrameHeaderCodec.StructSize - totalRead), ct).ConfigureAwait(false);
                if (n == 0) break;
                totalRead += n;
            }

            if (totalRead < StreamFrameHeaderCodec.StructSize)
                throw new IOException("Invalid stream: header too short");

            if (StreamFrameHeaderCodec.Read(hdr.AsSpan(0, StreamFrameHeaderCodec.StructSize)).MagicValue !=
                StreamFrameHeader.Magic)
                throw new IOException("Invalid stream header magic");

            _hash.Append(hdr.AsSpan(0, StreamFrameHeaderCodec.StructSize)); // ★ CRC 累积 Header
            _headerParsed = true;
        }

        private async ValueTask ParseFooterAsync(CancellationToken ct)
        {
            byte[] footer = new byte[StreamFrameFooterCodec.StructSize];
            int totalRead = 0;
            while (totalRead < StreamFrameFooterCodec.StructSize)
            {
                int n = await _session.ReadAsync(
                    footer.AsMemory(totalRead, StreamFrameFooterCodec.StructSize - totalRead), ct).ConfigureAwait(false);
                if (n == 0) break;
                totalRead += n;
            }

            if (totalRead < StreamFrameFooterCodec.StructSize) return;

            var f = StreamFrameFooterCodec.Read(footer.AsSpan(0, StreamFrameFooterCodec.StructSize));
            if (f.Magic != StreamFrameFooter.FooterMagic) return;

            _totalLength = (long)f.TotalLength;
            _entryCount = (long)f.EntryCount;
            _storedChecksum = f.Crc;

            _hash.Append(footer.AsSpan(0, 20)); // ★ CRC 累积 Footer 前 20B

            ulong computed = UnifiedCrc.FinalizeCrc64(_hash);
            _footerValid = computed == _storedChecksum;
            _footerParsed = true;
        }

        /// <summary>释放读取器（幂等）——释放底层读会话。</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
