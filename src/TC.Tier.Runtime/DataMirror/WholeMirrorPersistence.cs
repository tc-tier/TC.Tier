using TC.Tier.Contracts.Meta;
using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Mirror;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.DataMirror;

/// <summary>
/// WholeMirrorPersistence——<see cref="ITransferPersistence"/> 的内置桥接器（3a 托管形态，
/// 与 <c>Meta/MetadataMetaTransport</c> 托管 VersionedMetadata 同构）。
/// <para>★ 自持 WholeMirror：版本链 / CRC64 / N=2 轮替 / 2PC 全复用——零新存储逻辑；
///   组合根构造 + Dispose，就绪自管（首次使用时 Initialize + WaitForReady——调用点本就在
///   恢复核心 / checkpoint 路径）。</para>
/// <para>★ 桥接职责：三段式相位 ↔ WholeMirror v2 流式会话（<see cref="WholeMirror.BeginSession"/>
///   一族——尺寸后知，逐段直写零缓冲）；<b>Confirm 挂 WriteFooter</b>（checkpoint 单人写完即提交）；
///   <b>Abort 挂未写尾 Dispose</b>（镜像会话尾截断回退，悬干弃置）；读侧 Verify = 账面机制——
///   内容有效性由消费方格式裁决（设计稿 §2 契约 7）。</para>
/// <para>★ 传输上限：会话打开声明 maxTransferBytes（默认 128K），协调器按此预准备；单次超限直接抛。</para>
/// <para>规范：Runtime COORDINATION §4 铁律 8。</para>
/// </summary>
public sealed class WholeMirrorPersistence : ITransferPersistence
{
    private readonly WholeMirror _mirror;
    private bool _started;      // 惰性启动守卫（构造零 IO——接口零生命周期方法，契约 10）
    private bool _writeActive;  // 单写者（并发双开 → TryOpenWrite false，非抛）
    private long _opSeq;        // 直读提交/中止的单调操作号（非 2PC 惯例：调用方自管 seq）
    private bool _disposed;

    /// <summary>构造（= 配置，零 IO）——镜像宿主由本桥接器自持（3a 托管）。</summary>
    /// <param name="fileSystem">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">WholeMirror 设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）。</param>
    public WholeMirrorPersistence(
        IFileSystem fileSystem,
        WholeMirrorSettings settings,
        IRecovery<MirrorRecoveryHints>? recovery = null,
        MetaPolicyFactory<MirrorMetaHeader, MirrorMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null)
    {
        _mirror = new WholeMirror(fileSystem, settings, recovery, metaPolicyFactory, metaTransport);
    }

    /// <summary>宿主 WholeMirror——跨结构 2PC 编排用（经其事务参与面注册进 TransactionLog）。</summary>
    public WholeMirror Storage => _mirror;

    /// <inheritdoc />
    public bool TryOpenWrite(out ITransferWriter? writer,int maxTransferBytes=ITransferPersistence.DefaultMaxTransferBytes)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTransferBytes, 0);
        EnsureStarted();
        _mirror.WaitForReady();   // checkpoint 路径天然是等待点
        if (_writeActive)
        {
            writer = null;
            return false;   // 单写者：并发双开 → false（Try 语义，非抛）
        }

        _writeActive = true;
        writer = new WriterSession(this, maxTransferBytes);
        return true;
    }

    /// <inheritdoc />
    public bool TryOpenRead(out ITransferReader? reader,int maxTransferBytes=ITransferPersistence.DefaultMaxTransferBytes)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTransferBytes, 0);
        EnsureStarted();
        _mirror.WaitForReady();   // 恢复路径天然是等待点
        // 账面判定：有已提交版本且自身 CRC 过（三态等价：无/未提交/自校不过 → false）
        if (!_mirror.HasCommittedVersion || !_mirror.Verify(_mirror.HighestVersionAddress))
        {
            reader = null;
            return false;
        }

        reader = new ReaderSession(_mirror, _mirror.HighestVersionAddress, maxTransferBytes);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mirror.Dispose();
    }

    private void EnsureStarted()
    {
        if (_started) return;
        _mirror.Initialize();   // 启动后台恢复；就绪在会话开门时等待（checkpoint/恢复路径天然是等待点）
        _started = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // ══ 写会话：三段式相位（头/数据/尾 = 消费方格式字节——桥接器不解读） ══
    // ★ V2 流式形态：镜像原生会话直映射（WriteHeader → BeginSession+AppendChunk；
    //   Write → AppendChunk 逐段直写，<b>零缓冲</b>；WriteFooter → AppendChunk+EndSession+Confirm）。
    //   totalSize 前置询问已随 v2 帧格式出局（尺寸写尾时才知——长度=尾位−头，格式不存长度字段）。
    //   Abort 挂未写尾 Dispose（镜像会话尾截断回退，零残留）。

    private sealed class WriterSession(WholeMirrorPersistence owner, int maxTransferBytes) : ITransferWriter
    {
        private int _phase;     // 0=未开 1=写头后 2=已写尾（会话完成）
        private bool _disposed;
        public int MaxTransferBytes => maxTransferBytes;
        /// <summary>会话完成/失败收官：true = 三段式完整后的成功收官（幂等）；
        /// false = 主动失败 = Abort（镜像尾截断回退，本次内容对读侧不可见）；
        /// 未写尾调 Complete(true) = 相位违约抛（WriteFooter 才是原子提交点）。</summary>
        public void Complete(bool isSuccess = true)
        {
            ThrowIfDisposed();
            if (_phase == 2 || _phase == 0) return;   // 已完成幂等 / 未开会话无内容 no-op
            if (!isSuccess)
            {
                owner._mirror.Abort(Interlocked.Increment(ref owner._opSeq));   // 尾截断回退（_writeActive 由 Dispose 收口）
                _phase = 2;
                return;
            }
            throw new InvalidOperationException("未写尾不可完成——先 WriteFooter（写尾 = 原子提交点）。");
        }
        public void WriteHeader(ReadOnlySpan<byte> header)
        {
            ThrowIfDisposed();
            if (_phase != 0)
                throw new InvalidOperationException("WriteHeader 已调用——三段式相位不可重复。");
            Bound(header.Length);
            owner._mirror.BeginSession();
            owner._mirror.AppendChunk(header);
            _phase = 1;
        }

        public void WritePayload(ReadOnlySpan<byte> chunk)
        {
            ThrowIfDisposed();
            if (_phase == 0)
                throw new InvalidOperationException("先 WriteHeader 开相位（三段式协议）。");
            if (_phase == 2)
                throw new InvalidOperationException("已写尾——会话完成。");
            Bound(chunk.Length);
            owner._mirror.AppendChunk(chunk);
        }

        public void WriteFooter(ReadOnlySpan<byte> footer)
        {
            ThrowIfDisposed();
            if (_phase != 1)
                throw new InvalidOperationException("写尾前必须已写头（三段式协议）。");
            Bound(footer.Length);
            owner._mirror.AppendChunk(footer);
            owner._mirror.EndSession();
            owner._mirror.ConfirmCommitted(Interlocked.Increment(ref owner._opSeq));   // 写尾 = 原子提交点
            _phase = 2;
        }

        /// <summary>未写尾 Dispose = Abort（镜像会话尾截断回退——悬干物理丢弃）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_phase == 1)
                owner._mirror.Abort(Interlocked.Increment(ref owner._opSeq));   // 会话开启中 → 尾截断回退
            owner._writeActive = false;
        }

        private void Bound(int len)
            => ArgumentOutOfRangeException.ThrowIfGreaterThan(len, maxTransferBytes, "chunk");

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    }

    // ══ 读会话：三段式相位游标（payload 连续供给——格式由消费方解读） ══

    private sealed class ReaderSession(WholeMirror mirror, LogicalAddress head, int maxTransferBytes) : ITransferReader
    {
        // ★ v2 帧无长度字段——像长 = 尾位−头−头尾结构（推导的事实，账面快路径）
        private readonly long _payloadLen = mirror.GetPayloadLength(head);
        private long _cursor;
        private int _phase;   // 0=未读头 1=读头后 2=已读尾（完成）
        private bool _disposed;

        public int MaxTransferBytes => maxTransferBytes;
        public void Complete(bool isSuccess = true)
        {
            _phase = 2;
        }
        public int ReadHeader(Span<byte> dst)
        {
            ThrowIfDisposed();
            if (_phase != 0)
                throw new InvalidOperationException("ReadHeader 已调用——三段式相位不可重复。");
            int got = ReadCore(dst);
            _phase = 1;
            return got;
        }

        public int ReadPayload(Span<byte> dst)
        {
            ThrowIfDisposed();
            if (_phase == 0)
                throw new InvalidOperationException("先 ReadHeader 开相位（三段式协议）。");
            if (_phase == 2)
                throw new InvalidOperationException("已读尾——会话完成。");
            return ReadCore(dst);
        }

        public int ReadFooter(Span<byte> dst)
        {
            ThrowIfDisposed();
            if (_phase != 1)
                throw new InvalidOperationException(_phase == 0
                    ? "先 ReadHeader 开相位（三段式协议）。"
                    : "已读尾——会话完成。");
            int got = ReadCore(dst);
            _phase = 2;
            return got;
        }

        public void Dispose() => _disposed = true;

        private int ReadCore(Span<byte> dst)
        {
            Bound(dst.Length);
            int n = (int)Math.Min(dst.Length, _payloadLen - _cursor);   // 越界防护：供给不越过账面像长
            if (n <= 0) return 0;   // EOF（消费方按自己的格式判定头/尾合法性）
            int got = mirror.ReadChunk(head, _cursor, dst[..n]);
            _cursor += got;
            return got;
        }

        private void Bound(int len)
            => ArgumentOutOfRangeException.ThrowIfGreaterThan(len, maxTransferBytes, "buffer");

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    }
}
