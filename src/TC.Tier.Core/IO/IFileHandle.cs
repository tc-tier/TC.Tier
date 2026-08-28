
namespace TC.Tier.Core.IO;

/// <summary>
/// 单个文件的访问与操作——Core/IO 数据平面（fd 模型 ∪ overlapped 模型，D7）。
/// <para>一个 IFileHandle 实例对应一个文件的一种打开语义（Access × Mode × Sharing × Hints）。</para>
/// <para>★ 磁盘实现：<c>SafeFileHandle + RandomAccess</c>（内含 DIO 对齐校验与逐句柄探测）。</para>
/// <para>★ 内存实现：槽代际寻址（open 时解析一次，热路径零字典开销）。</para>
/// <para>★ 位置语义并集：<c>Write/Read(offset)</c> 保持 pwrite/pread 无状态铁律（不读不推进游标）；
///   句柄同时内建生命周期游标（open 初始化、<see cref="Append"/> 原子预留推进、<see cref="Seek"/> 移动）。</para>
/// </summary>
public interface IFileHandle : IDisposable, IAsyncDisposable
{
    // ═══════════════════════════════════════════════════════════════
    //  身份与 IO 模式（open 时一次探测/解析，句柄生命周期内不变）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>文件路径——本层唯一文件身份（fs 根下的相对名）。</summary>
    string Path { get; }

    /// <summary>
    /// 本句柄无缓冲 IO 探测结果——open 时一次探测并缓存（四态原生枚举，不映射任何上层契约）。
    /// <para>★ 语义链：请求（Hints.NoBuffering）→ 逐句柄探测 → 结果报告（Supported/BestEffort/Ignored/NotRequested）。</para>
    /// <para>★ mem 介质恒 <see cref="UnbufferedIoSupport.NotRequested"/>。</para>
    /// </summary>
    UnbufferedIoSupport UnbufferedSupport { get; }

    /// <summary>
    /// 本句柄全部对齐要求的最大值——DIO 三重对齐（offset/length/buffer 地址）的单一事实源。
    /// <para>★ 平台矩阵：Win=max(扇区, 内存页)；Linux=逻辑块（通常=扇区）；mem=1；缓冲句柄=1。</para>
    /// <para>★ 消费者从句柄读对齐基准分配缓冲（AlignedMemoryManager/PinnedBufferPool），禁止手写对齐运算。</para>
    /// </summary>
    long RequiredAlignment { get; }

    // ═══════════════════════════════════════════════════════════════
    //  位置读写（pwrite/pread 语义——不读不推进游标）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>同步写入——从指定偏移覆写（磁盘=pwrite，内存=MemoryCopy）；越过 EOF 零洞扩展（pwrite 平权）。</summary>
    /// <param name="offset">写入起点</param>
    /// <param name="source">源缓冲</param>
    void Write(long offset, ReadOnlySpan<byte> source);

    /// <summary>异步写入——语义同 <see cref="Write"/>。</summary>
    /// <param name="offset">写入起点</param>
    /// <param name="source">源缓冲</param>
    /// <param name="ct">取消令牌</param>
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct);

    /// <summary>同步读取——返回实际读取字节数（EOF 处可能小于 destination.Length；磁盘 pread 到 EOF 语义）。</summary>
    /// <param name="offset">读取起点</param>
    /// <param name="destination">目标缓冲</param>
    int Read(long offset, Span<byte> destination);

    /// <summary>异步读取——语义同 <see cref="Read"/>。</summary>
    /// <param name="offset">读取起点</param>
    /// <param name="destination">目标缓冲</param>
    /// <param name="ct">取消令牌</param>
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct);

    // ═══════════════════════════════════════════════════════════════
    //  句柄游标（D7——机械位置内建；协调分配仍归逻辑层）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>句柄游标当前值（Tell）——Volatile 读。</summary>
    long Position { get; }

    /// <summary>
    /// 原子预留追加——<c>Interlocked</c> 预留游标区间后 pwrite 落地，返回实际写入偏移。
    /// <para>★ 同一句柄多线程 Append 无覆写、无撕裂（比内核 O_APPEND 更强且任何介质成立）。</para>
    /// <para>★ 失败语义：预留不回滚（回退=吞噬他人预留），失败区间成为读零稀疏洞；
    ///   异常携带 ReservedOffset（预留落点），句柄不因失败废止。</para>
    /// </summary>
    /// <param name="source">源缓冲</param>
    /// <returns>实际写入偏移（预留落点）</returns>
    long Append(ReadOnlySpan<byte> source);

    /// <summary>异步追加——语义同 <see cref="Append"/>。</summary>
    /// <param name="source">源缓冲</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>实际写入偏移（预留落点）</returns>
    ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct);

    /// <summary>移动游标。与 Append 并发是调用方错误（互斥操作，文档纪律声明，不设内部锁）。</summary>
    /// <param name="offset">偏移量</param>
    /// <param name="origin">偏移基准</param>
    long Seek(long offset, SeekOrigin origin);

    // ═══════════════════════════════════════════════════════════════
    //  空间管理
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 幂等预分配到打开语义记录的大小（PreallocateSize&lt;=0 为 no-op）——已分配/文件已有大小则跳过。
    /// open 时已对 PreallocateSize&gt;0 自动执行；此方法供显式重放（恢复场景）。
    /// </summary>
    void Preallocate();

    /// <summary>文件逻辑长度（含稀疏洞）；PunchHole 后不变。</summary>
    long Length { get; }

    /// <summary>物理占用大小——Sparse 介质 PunchHole 后 &lt; Length；mem Reserved 模式打洞后按记账口径。</summary>
    long AllocatedSize { get; }

    /// <summary>设置逻辑长度（截断或零填充扩展）。</summary>
    void SetLength(long length);

    /// <summary>
    /// 物理打洞回收区间——文件大小不变，区间归零。
    /// <para>★ 对齐契约：offset 与 length 必须按 <c>Volume.AllocationUnit</c> 对齐，未对齐抛 <see cref="IOError.AlignmentError"/>（两介质同校验）。</para>
    /// </summary>
    /// <param name="offset">打洞起点</param>
    /// <param name="length">打洞长度</param>
    void PunchHole(long offset, long length);

    /// <summary>枚举已分配区间——块粒度报告，对齐到 AllocationUnit（mem=PageSize / 磁盘=fs 簇）。</summary>
    /// <returns>已分配区间列表（闭区间 [Start, End)）</returns>
    IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges();

    /// <summary>
    /// 已分配区间带状态（采集管线 unwritten 保真用——raw-medium-and-conversion-design §5.2）。
    /// <para>★ 语义：区间并集与 <see cref="EnumerateAllocatedRanges"/> 完全一致，额外标注
    ///   <c>Unwritten</c>（预分配未写区间——物理已留、读零、写转换）。不支持区分的介质
    ///   （Disk/Mem/Remote）默认实现恒 <c>false</c>（全部按 written 报告——诚实默认）。</para>
    /// </summary>
    /// <returns>已分配区间列表（闭区间 [Start, End) + Unwritten 状态）</returns>
    IReadOnlyCollection<(long Start, long End, bool Unwritten)> EnumerateAllocatedRangesDetailed()
    {
        var result = new List<(long, long, bool)>();
        foreach (var (start, end) in EnumerateAllocatedRanges())
            result.Add((start, end, false));
        return result;
    }

    /// <summary>区间塌缩——[offset, offset+length) 移除且后续数据前移，文件缩短（对齐 AllocationUnit；不支持平台抛 Unsupported）。</summary>
    /// <param name="offset">塌缩起点</param>
    /// <param name="length">塌缩长度</param>
    void CollapseRange(long offset, long length);

    /// <summary>区间插入——在 offset 处插入零洞且后续数据后移，文件增长（对齐 AllocationUnit；不支持平台抛 Unsupported）。</summary>
    /// <param name="offset">插入起点</param>
    /// <param name="length">插入长度</param>
    void InsertRange(long offset, long length);

    // ═══════════════════════════════════════════════════════════════
    //  文件间拷贝
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 高效拷贝区间到目标文件（copy_file_range/FICLONE 加速或用户态回退）——返回实际拷贝字节数。
    /// <para>★ 不保证原子：失败不回滚目标，已完成长度经 <see cref="FileIOException.CompletedLength"/> 携带。</para>
    /// </summary>
    /// <param name="destination">目标句柄（须同一介质）。</param>
    /// <param name="sourceOffset">源区间起点。</param>
    /// <param name="destinationOffset">目标写入起点。</param>
    /// <param name="length">拷贝长度。</param>
    /// <returns>实际拷贝字节数（失败不回滚目标，已完成长度经 <see cref="FileIOException.CompletedLength"/> 携带）。</returns>
    long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length);

    /// <summary>整文件引用克隆（FICLONE/写时复制）——不支持平台回退 CopyRange 全量。语义同 <see cref="CopyRange"/> 的部分失败契约。</summary>
    /// <param name="destination">目标句柄（须同一介质）。</param>
    /// <returns>实际克隆字节数（失败不回滚目标，已完成长度经 <see cref="FileIOException.CompletedLength"/> 携带）。</returns>
    /// <exception cref="FileIOException">不支持克隆的介质或平台（Disk/Mem/Remote）</exception>
    long CloneRange(IFileHandle destination);

    // ═══════════════════════════════════════════════════════════════
    //  向量化 IO
    // ═══════════════════════════════════════════════════════════════

    /// <summary>向量写入——多片缓冲按序写入连续区间（readv/writev 或逐片回退）。语义等价逐片 Write。</summary>
    /// <param name="offset">写入起点</param>
    /// <param name="sources">源缓冲片段</param>
    /// <exception cref="ArgumentException">sources 为空</exception>
    void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources);

    /// <summary>异步向量写入——语义同 <see cref="WriteVector"/>。</summary>
    /// <param name="offset">写入起点</param>
    /// <param name="sources">源缓冲片段</param>
    /// <param name="ct">取消令牌</param>
    ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct);

    /// <summary>向量读取——多片缓冲按序填充，返回总读取字节数（EOF 截断）。语义等价逐片 Read。</summary>
    /// <param name="offset">读取起点</param>
    /// <param name="destinations">目标缓冲片段</param>
    int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations);

    /// <summary>异步向量读取——语义同 <see cref="ReadVector"/>。</summary>
    /// <param name="offset">读取起点</param>
    /// <param name="destinations">目标缓冲片段</param>
    /// <param name="ct">取消令牌</param>
    ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct);

    // ═══════════════════════════════════════════════════════════════
    //  持久化谱系
    // ═══════════════════════════════════════════════════════════════

    /// <summary>全量刷盘（fsync/FlushFileBuffers/F_FULLFSYNC；mem no-op）。</summary>
    void Flush();

    /// <summary>数据刷盘（fdatasync 语义——不刷元数据）。仅 Linux 能力位置位时与 Flush 可区分；否则 ≡ Flush 全量回退不抛。</summary>
    void FlushData();

    /// <summary>访问提示（posix_fadvise 族；不支持平台 no-op，能力位表达）。</summary>
    void Advise(FileAdvise advise);

    // ═══════════════════════════════════════════════════════════════
    //  字节范围锁（fd 级协调——advisory 语义；锁属句柄，Dispose 自动释放）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 阻塞获取字节范围锁。同句柄重叠再锁存在平台分歧（Linux OFD 幂等转换；Windows LockFileEx 冲突）——
    /// 可移植契约不依赖同句柄重锁行为。
    /// </summary>
    /// <param name="offset">锁起点</param>
    /// <param name="length">锁长度</param>
    /// <param name="mode">锁模式</param>
    void Lock(long offset, long length, FileLockMode mode);

    /// <summary>非阻塞尝试获取——失败立即返回 false（不抛）。</summary>
    /// <param name="offset">锁起点</param>
    /// <param name="length">锁长度</param>
    /// <param name="mode">锁模式</param>
    bool TryLock(long offset, long length, FileLockMode mode);

    /// <summary>释放字节范围锁（须与 Lock/TryLock 的区间精确配对）。</summary>
    /// <param name="offset">锁起点</param>
    /// <param name="length">锁长度</param>
    void Unlock(long offset, long length);

    // ═══════════════════════════════════════════════════════════════
    //  内存映射（生命周期独立于父句柄——Map 私有化底层引用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 映射区间为内存视图。父句柄被池淘汰/Dispose 不产生野视图（dup/槽引用守护）；
    /// 铁律：<see cref="IMappedSection"/> 必须 Dispose（其持有独立 OS 句柄/引用）。
    /// </summary>
    /// <param name="offset">映射起点。</param>
    /// <param name="length">映射长度（offset+length 须 ≤ Length）。</param>
    /// <param name="access">访问模式。</param>
    IMappedSection Map(long offset, long length, AccessMode access);

    // ═══════════════════════════════════════════════════════════════
    //  扩展属性（xattr / ADS）——best-effort
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ FileExtra 平面（§3.6 终稿）：一份不透明附加数据 ≤<see cref="IFileSystem.MaxFileExtraBytes"/>，
    /// 与 fs 级（<c>IFileSystem.CreateFile(extra:)/Stat→FsEntryInfo.FileExtra</c>）同平面互见。
    /// 心智模型：≤1.5K 的小文件——四成员即其 pread/pwrite/整写投影。
    /// <para>介质语义：Disk=模式路由（xattr/ADS 或 sidecar）；Mem=槽 blob（锁内原子）；Remote=对象用户元数据
    /// （写入 staging 随 Flush/PUT 原子提交；读=staging 优先）。无附加数据 = 空。</para>
    /// </summary>
    ReadOnlyMemory<byte> FileExtra { get; }

    /// <summary>偏移读（pread 契约）：返回实际读数；尾段不足返实际量；offset ≥ 长度 → 0（EOF 不抛）。</summary>
    /// <param name="offset">读取起点</param>
    /// <param name="destination">目标缓冲区</param>
    /// <returns>实际读取字节数</returns>
    int ReadFileExtra(long offset, Span<byte> destination);

    /// <summary>精准字节写（pwrite 契约）：原位覆写；越尾零扩展；扩展后总长超 <see cref="IFileSystem.MaxFileExtraBytes"/>
    /// 抛 <see cref="ArgumentException"/>（预算闭环——仅有的两个长度增长点之一）。并发：同区并发写由调用方协调。</summary>
    /// <param name="offset">写入起点</param>
    /// <param name="data">源缓冲区</param>
    void WriteFileExtra(long offset, ReadOnlySpan<byte> data);

    /// <summary>完全覆盖（长度可增可减；空 = 清除）；超限同抛（预算闭环另一增长点）。</summary>
    /// <param name="extra">源缓冲区</param>
    void SetFileExtra(ReadOnlyMemory<byte> extra);
}
