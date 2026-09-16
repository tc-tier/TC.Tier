using System.Security.Cryptography;
using System.Text;

namespace TC.Tier.Core.IO.S3;

/// <summary>
/// chunked 流式签名内容流——把上游字节实时封装为
/// <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c> 分帧（<c>size-hex;chunk-signature=sig\r\n data \r\n</c>，
/// 链式签名：seed → chunk₁ → chunk₂ → … → 终帧 0）。
/// <para>★ 用途：不可寻流的直传（免整驻内存/免双遍哈希）与未知长度 PUT（外层 spool 后经本流上传）。
///   HTTP 层经 HttpClient 的 Transfer-Encoding: chunked 传输（Content-Length 不设）。</para>
/// <para>★ 单次消费（源流不可回卷）——发送侧不做重试（幂等性由调用方保证或走 spool 路径）。</para>
/// </summary>
internal sealed class ChunkedSignedStream : Stream
{
    private const int ChunkSize = 128 * 1024;

    private readonly Stream _source;
    private readonly byte[] _signingKey;
    private readonly string _amzDate;
    private readonly string _scope;
    private readonly byte[] _chunkBuf = new byte[ChunkSize];
    private readonly MemoryStream _pending = new();
    private string _previousSignature;   // seed 起始——每 chunk 演进
    private bool _finished;

    /// <summary>
    /// 精确预计算分帧编码后的字节长度（chunk 数 × 帧头/尾 + 终帧）——设为 HTTP Content-Length，
    /// 免 HTTP 层 Transfer-Encoding: chunked（部分服务端/中间件对该形态请求体支持不佳）。
    /// </summary>
    /// <param name="decodedLength">原始（未编码）载荷字节数。</param>
    /// <returns>分帧编码后的总字节数（含所有帧头行、CRLF、数据、终帧）。</returns>
    public static long EncodedLength(long decodedLength)
    {
        const int sigLen = 64;   // hex(HMAC-SHA256)
        long total = 0;
        long remaining = decodedLength;
        while (remaining > 0)
        {
            var size = (int)Math.Min(ChunkSize, remaining);
            total += size.ToString("x").Length + ";chunk-signature=".Length + sigLen + 2   // 帧头行 + CRLF
                     + size + 2;                                                            // 数据 + CRLF
            remaining -= size;
        }
        total += 1 + ";chunk-signature=".Length + sigLen + 2 + 2;   // 终帧 "0;..." + CRLF + 空行 CRLF
        return total;
    }

    public ChunkedSignedStream(Stream source, byte[] signingKey, string seedSignature,
                               string amzDate, string scope)
    {
        _source = source;
        _signingKey = signingKey;
        _previousSignature = seedSignature;
        _amzDate = amzDate;
        _scope = scope;
    }

    // ★ 恒 true：耗尽经 Read→0 表达——HttpClient 拷完 CL 后会做余量探测读，CanRead=false 会抛
    //   NotSupportedException（Stream.BeginReadInternal 契约）
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>从分帧签名流读取已编码字节到缓冲区（耗尽返回 0）。</summary>
    /// <param name="buffer">填充目标缓冲区。</param>
    /// <param name="offset">缓冲区写入起始偏移。</param>
    /// <param name="count">最多读取字节数。</param>
    /// <returns>实际读取的字节数；终帧读尽后返回 0。</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_pending.Position >= _pending.Length && !_finished)
        {
            _pending.SetLength(0);
            _pending.Position = 0;
            var n = _source.Read(_chunkBuf, 0, ChunkSize);
            if (n <= 0)
            {
                EmitFinalFrame();
                _pending.Position = 0;
                _finished = true;
                break;
            }
            EmitChunk(_chunkBuf, n);
            _pending.Position = 0;   // ★ 帧就绪回卷读指针（写后停在末尾会被循环误判已取尽而清帧）
        }
        if (_pending.Position >= _pending.Length)
            return 0;   // 终帧已读完——流尽
        var take = (int)Math.Min(count, _pending.Length - _pending.Position);
        _pending.Read(buffer, offset, take);
        return take;
    }

    /// <summary>编码并签名一个数据帧（帧头 + 数据 + CRLF），写入待读缓冲。</summary>
    /// <param name="data">原始数据块。</param>
    /// <param name="length">本帧有效字节数（≤ <c>data</c>.Length，≤ ChunkSize）。</param>
    private void EmitChunk(byte[] data, int length)
    {
        var hash = SigV4.Sha256Hex(data.AsSpan(0, length));
        var sts = SigV4.BuildChunkStringToSign(_amzDate, _scope, _previousSignature, hash);
        var sig = SigV4.SignChunk(_signingKey, sts);
        _previousSignature = sig;
        var header = Encoding.ASCII.GetBytes($"{length:x};chunk-signature={sig}\r\n");
        _pending.Write(header, 0, header.Length);
        _pending.Write(data, 0, length);
        _pending.Write(new byte[] { (byte)'\r', (byte)'\n' }, 0, 2);
    }

    private void EmitFinalFrame()
    {
        var sts = SigV4.BuildChunkStringToSign(_amzDate, _scope, _previousSignature, SigV4.EmptyPayloadHash);
        var sig = SigV4.SignChunk(_signingKey, sts);
        var frame = Encoding.ASCII.GetBytes($"0;chunk-signature={sig}\r\n\r\n");
        _pending.Write(frame, 0, frame.Length);
    }

    /// <summary>释放资源（仅托管资源——关闭待读帧缓冲；不关闭上游源流）。</summary>
    /// <param name="disposing">true = 显式/隐式 Dispose 调用（释放托管资源）；false = 终结器路径（本类无非托管资源，无操作）。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _pending.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>无操作（分帧按需在 <see cref="Read"/> 内实时产生——无缓冲外排概念）。</summary>
    public override void Flush() { }
    /// <summary>不支持 Seek（单向流）。</summary>
    /// <param name="offset">偏移量。</param>
    /// <param name="origin">寻址基准。</param>
    /// <returns>不返回——恒抛。</returns>
    /// <exception cref="NotSupportedException">本流不可寻。</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <summary>不支持 SetLength（单向流）。</summary>
    /// <param name="value">目标长度。</param>
    /// <exception cref="NotSupportedException">本流不可设长。</exception>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>不支持 Write（只读流）。</summary>
    /// <param name="buffer">写入缓冲（未用）。</param>
    /// <param name="offset">偏移量（未用）。</param>
    /// <param name="count">字节数（未用）。</param>
    /// <exception cref="NotSupportedException">本流只读。</exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
