namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 存储引擎接口——提供顺序追加、随机覆写、跨段读取、刷盘、截断、回收、碎片整理等功能。
/// <para>★ 继承 <see cref="IStorageInfo"/>（纯信息视图：定位/配置/路径解析）+
///   <see cref="ILifecycle{EngineRecoveryHints}"/>（生命周期观测：IsReady/RecoveryState/WaitForReady*）。
///   本接口在其上追加 IO 行为（Append/Read/Write 等）+ 动态水位（AllocatedTail 等）。
///   子系统（Meta/Compact）只需依赖 <see cref="IStorageInfo"/> 即可，避免与 IO 耦合。</para>
/// <para>★ 初始化：<b>不在接口面</b>——启动统一经 <c>StorageEngineBuilder.Start/StartAsync</c> 一步到位
///   （内部 Initialize + WaitForReady），不允许外部直接调 Initialize（2026-08-24 用户裁定：接口面消除）。
///   恢复 hints（<see cref="EngineRecoveryHints"/>，只含恢复水位——段表双尾修正）经 Start(hints) 传入；
///   段生长上限/分段开关是构造期配置（Options 配置链传入），不经启动入口。</para>
/// </summary>
public interface IStorageEngine  : IStorageInfo, ILifecycle<EngineRecoveryHints>, IDisposable, IAsyncDisposable
{
    // === 元信息（只读属性已上移到 IStorageInfo，此处不再重复声明）===
    // SectorSize / DataPath / DeviceName / BaseDirectory / Capacity / SegmentGrowthLimit /
    // UnbufferedSupport / EnableSegmentation / PreallocateFile / SegmentFileName — 见 IStorageInfo。

    // === 初始化 ===
    // ★ 启动入口不在接口面（ILifecycle 接口面已消除 Initialize）——统一经 StorageEngineBuilder.Start/StartAsync。
    //   hints 只带恢复水位（committed/allocated tail 修正），经 Start(hints) 传。段生长上限/分段开关是构造期配置——
    //   经 Options 配置链传入（构造 = 配置，启动 = 双尾）。

    // === Append（追加写，推进游标） ===

    /// <summary>追加写入，从当前尾游标 <see cref="AllocatedTail"/> 开始，返回写入后的绝对起始地址。</summary>
    /// <param name="source">源数据。</param>
    /// <returns>写入后的起始 <see cref="LogicalAddress"/>。</returns>
    /// <exception>pwrite 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    LogicalAddress Append(ReadOnlySpan<byte> source);

    /// <summary>异步追加写入，从当前尾游标 <see cref="AllocatedTail"/> 开始，返回写入后的绝对起始地址。</summary>
    /// <param name="source">源数据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入后的起始 <see cref="LogicalAddress"/>。</returns>
    /// <exception>pwrite 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    /// <exception cref="OperationCanceledException">取消。</exception>
    ValueTask<LogicalAddress> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct);

    // === Allocate（预留空间，推进游标，不写数据） ===

   /// <summary>
   ///  预留空间（推进游标，不写数据），返回分配的地址区间 [start, end)。
   /// </summary>
   /// <param name="length">预留字节数（&gt;0）。</param>
   /// <returns>预留区间的起始和结束 <see cref="LogicalAddress"/>。</returns>
    (LogicalAddress Start, LogicalAddress End) Allocate(long length);

    // === CalculationAddress（地址逻辑推算，不分配不 IO） ===

    /// <summary>
    /// 计算给定地址前进指定长度后的新地址。
    /// <para>★ 纯计算（无空间管理），支持 ±：length ≥ 0 前进（跨段进位正确）；length &lt; 0 回退
    ///   （跨段借位正确，越过 MinAddress 返回 <see cref="LogicalAddress.Invalid"/>）。</para>
    /// <para>★ 仅前进/仅回退的方向原语在段表层（AdvanceAddress/RetreatAddress 分立）；
    ///   引擎层 CalculationAddress 是统一的双向计算动词——名字叫"计算"，就得加减都算。</para>
    /// <para>这个不是真实的地址分配，仅用于逻辑推算——应搭配 <see cref="Allocate"/> 分配空间地址后，
    ///   业务层（如 Log 的页缓冲模型）在已预留的地址区间内推算 entry 地址。典型场景：Log 先
    ///   <see cref="Allocate"/>(PageSize) 拿到页起始地址 pageStart，entry 写进内存页时地址 =
    ///   CalculationAddress(pageStart, entryOffsetInPage)，页满 <see cref="Write"/> 覆写预留区。
    ///   这样 entry 地址在写页时即已知，不依赖页提交（解决"先写内存页后 Append 得地址"的顺序死结）。</para>
    /// <para>实现：正值委托 <c>LogicalAddressRegistry.AdvanceAddress</c>、负值委托
    ///   <c>RetreatAddress</c>（跨段进位/借位均正确）。</para>
    /// </summary>
    /// <param name="address">起始地址。</param>
    /// <param name="length">前进（正）/回退（负）的字节数。</param>
    /// <returns>前进/回退后的地址。</returns>
    LogicalAddress CalculationAddress(LogicalAddress address, long length);

    // === GetDistance（地址距离，纯逻辑推算不 IO）===

    /// <summary>
    /// ★ 计算两个地址之间的字节距离（from → to，跨段自动累加）。
    /// <para>返回 <c>to - from</c> 的字节数。若 from > to 返回负值。</para>
    /// <para>★ 这是纯逻辑推算（不分配、不 IO）——委托 <c>LogicalAddressRegistry.GetDistance</c>（跨段进位正确）。</para>
    /// <para>上层（Structures 层）需要知道两个地址之间的字节数时（如区间大小、空间统计），必须调用此方法，
    /// 禁止手动对 <see cref="LogicalAddress.Offset"/> 做减法（段内偏移跨段无意义）。</para>
    /// <para>★ from/to 必须 ≤ <see cref="AllocatedTail"/>（已分配地址空间内）。超出范围抛异常。</para>
    /// </summary>
    /// <param name="from">起始地址（含）。</param>
    /// <param name="to">结束地址（不含）。</param>
    /// <returns>from 到 to 的字节数。</returns>
    /// <exception cref="ArgumentOutOfRangeException">from 或 to 超出已分配空间。</exception>
    long GetDistance(LogicalAddress from, LogicalAddress to);

    // === Write（随机覆写，给定地址） ===
    /// <summary>
    /// 给定地址覆写。地址在前，数据在后。
    /// <para>超出当前 <see cref="CommittedTail"/> 时，空出来的区域利用稀疏文件特性——不写零，
    /// 设备 CAS 推进 _tail 到 destination + length，在 destination 处直接写数据。</para>
    /// </summary>
    /// <param name="destination">目标地址。</param>
    /// <param name="source">源数据。</param>
    /// <returns>写入后的绝对地址（等于 destination）。</returns>
    /// <exception>超出 maxOffset 或 pwrite 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    LogicalAddress Write(LogicalAddress destination, ReadOnlySpan<byte> source);

    /// <summary>异步覆写。</summary>
    /// <exception>超出 maxOffset 或 pwrite 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    /// <exception cref="OperationCanceledException">取消。</exception>
    ValueTask<LogicalAddress> WriteAsync(LogicalAddress destination, ReadOnlyMemory<byte> source, CancellationToken ct);

    // === Read（跨段查表） ===

    /// <summary>从给定地址读取，跨段自动查地址表切分。</summary>
    /// <param name="source">源地址。</param>
    /// <param name="destination">目标缓冲区。</param>
    /// <returns>实际所读字节数（越界可能小于 destination.Length，0 = EOF）。</returns>
    /// <exception>地址指向已删除的段文件。
    ///     <cref>PartitionInvalidException</cref>
    /// </exception>
    /// <exception>pread 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    int Read(LogicalAddress source, Span<byte> destination);

    /// <summary>异步读取。</summary>
    /// <exception>地址指向已删除的段文件。
    ///     <cref>PartitionInvalidException</cref>
    /// </exception>
    /// <exception>pread 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    /// <exception cref="OperationCanceledException">取消。</exception>
    ValueTask<int> ReadAsync(LogicalAddress source, Memory<byte> destination, CancellationToken ct);

    // === Flush（仅同步——fsync 无异步 OS 原语） ===

    /// <summary>全量刷盘（fsync / FlushFileBuffers / F_FULLFSYNC）。</summary>
    /// <exception>fsync/FlushFileBuffers 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    void Flush();

    /// <summary>刷到指定地址（floor 到段，刷 [minSeg..upTo.SegId]）。</summary>
    /// <param name="upTo">目标地址。</param>
    /// <exception>fsync/FlushFileBuffers 失败。
    ///     <cref>FileIOException</cref>（Core IO 统一语义异常透传，IOError 语义码）
    /// </exception>
    void Flush(LogicalAddress upTo);

    // === 截断/回收 ===

    /// <summary>
    /// 回收头部——释放 [MinAddress, address) 区间的段文件，推进 MinAddress。
    /// </summary>
    /// <param name="address">回收的目标地址。</param>
    void ReclaimHead(LogicalAddress address);

   /// <summary>
   /// 回收尾部——释放 [newTail, AllocatedTail) 区间的段文件，推进 AllocatedTail。
   /// </summary>
   /// <param name="newTail">回收的目标地址。</param>
    void ReclaimTail(LogicalAddress newTail);

    /// <summary>
    /// 回收指定区间——释放 [from, to) 区间的段文件，推进 MinAddress / AllocatedTail。
    /// </summary>
    /// <param name="from">回收区间的起始地址（含）。</param>
    /// <param name="to">回收区间的结束地址（不含）。</param>
    void Reclaim(LogicalAddress? from, LogicalAddress? to);

    /// <summary>
    /// 启动后台区间回收——立即返回句柄，物理打洞在线程池上执行，调用线程 0 等待。
    /// <para>★ 命名（2026-08-24 裁定）：Async 后缀保留给可 await 的方法（返回 Task/ValueTask）；
    ///   本方法返回 <see cref="IAsyncOperation"/> 句柄，等待走 <c>await op.WaitAsync()</c>——
    ///   动词 Start 表达"启动后台操作"。失败断点：Failed 事件异常的 <c>Data["lastPunchedOffset"]</c>
    ///   携带最后成功打洞地址，调用方据此重试剩余区间。</para>
    /// </summary>
    /// <returns>后台回收操作句柄（进度 + 事件 + Cancel + WaitAsync）。</returns>
    IAsyncOperation StartReclaim(LogicalAddress? from, LogicalAddress? to, CancellationToken ct);

    // === 地址空间元信息（地址级，不暴露段） ===

    /// <summary>地址租借水位（CAS 推进，含未落盘空洞）。Append 的起点；上层据此算剩余容量。
    /// <para>★ 注意：此值含「租借未写」的空洞，不是真实已写水位。Read/Scan/Reclaim 的合法上界请用 <see cref="CommittedTail"/>。</para>
    /// <para>语义契约：<see cref="MinAddress"/> ≤ <see cref="CommittedTail"/> ≤ AllocatedTail。</para></summary>
    LogicalAddress AllocatedTail { get; }

    /// <summary>真实已写水位（pwrite 后推进，数据已写）。所有非 Append 操作的合法上界。
    /// <para>类比数据库事务提交=逻辑写入确认（pwrite 完成），不暗示 fsync（fsync 由 Flush 负责）。</para></summary>
    LogicalAddress CommittedTail { get; }

    /// <summary>最小有效地址（头部段被 ReclaimHead 删除后后移）。</summary>
    LogicalAddress MinAddress { get; }

    /// <summary>指定区间空洞率——逐段查询 OS 真实物理分配（稀疏文件 allocated ranges）。
    /// <para>★ 0.0 = 无空洞（全部分配），1.0 = 全空洞。返回值始终在 [0.0, 1.0]。</para>
    /// <para>★ 与仅用于估算的计数器不同，此方法查询 OS 真实分配，结果精确但有 syscall 开销。</para>
    /// </summary>
    /// <param name="from">区间起点（含）。</param>
    /// <param name="to">区间终点（不含）。</param>
    double GetHoleRatio(LogicalAddress from, LogicalAddress to);

    /// <summary>单段空洞率——查询 OS 真实物理分配。</summary>
    double GetHoleRatio(int segId);

    // === 顺序读句柄 ===

    /// <summary>
    /// 获取顺序读句柄——游标 + 读/跳分离，自动跨段。
    /// </summary>
    /// <param name="start">读取窗口起点（正序时 Position 初始值；倒序时从 end 往前读到 start 停）。</param>
    /// <param name="end">读取窗口终点（正序读到此处 EOF；倒序时 Position 初始值）。</param>
    /// <param name="direction">正序取后续段 / 倒序取前一段。</param>
    /// <param name="usePageCache"><see langword="true"/> 走 OS 页缓存；<see langword="false"/> 走 DirectIO。</param>
    /// <param name="snapshotMode">快照一致 vs 脏读。</param>
    /// <returns>顺序读句柄。</returns>
    ISequentialReader OpenSequentialReader(LogicalAddress start, LogicalAddress end,
                                           ReadDirection direction = ReadDirection.Forward,
                                           bool usePageCache = true,
                                           SnapshotMode snapshotMode = SnapshotMode.Consistent);

    // === Compact（碎片整理） ===
    // ★ Compact 是威胁操作（整段搬迁 + 删段）——2026-08-24 用户裁定：一律后台句柄形态，
    //   同步入口废除（GetAwaiter().GetResult() 强制等待有线程池耗尽死锁风险；超时经
    //   await op.WaitAsync(ct) 调用方自控）。后台入口返回 IAsyncOperation 句柄。

    /// <summary>启动全量 Compact——[MinAddress, CommittedTail] 全部已提交数据搬迁到紧凑新段。
    /// 0 等待返回句柄；取消/超时/等待由调用方控制（op.Cancel / await op.WaitAsync(ct)）。</summary>
    IAsyncOperation<CompactResult> StartCompact();

    /// <summary>
    /// 启动区间 Compact（带地址翻译的磁盘碎片整理）——把 [from,to) 内的有效数据压实，
    /// 消除碎片间隙，压实区末尾到 to 之间 PunchHole 归还（连续空洞）。
    /// <para>★ <paramref name="addresses"/> 是上层需翻译的地址集合（段内偏移量，与长度无关）。
    ///   每个不同的请求地址都进入 MigrationMap；allocated 地址映射到新地址，hole、不存在或区间外地址映射到 null。</para>
    /// <para>★ 区间外（from 前、to 后）数据原位不动。CommittedTail 不退。</para>
    /// </summary>
    /// <param name="from">整理范围起始（含）。</param>
    /// <param name="to">整理范围结束（不含）。</param>
    /// <param name="addresses">需翻译的地址集合。</param>
    IAsyncOperation<CompactResult> StartRangeCompact(LogicalAddress from, LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses);

    /// <summary>
    /// 启动区间 Compact（申报活区间版——记录粒度精确搬迁）——搬迁规划按使用方申报的活记录区间执行，
    /// 而非物理 allocated 枚举（簇粒度）。
    /// <para>★ A8/§XVIII：Reclaim 打洞是<b>记录粒度</b>，物理 allocated 是<b>簇粒度</b>（NTFS 簇内有
    ///   存活邻居的洞整簇保配，fsutil 实证）——小记录场景物理规划对洞不可见 → 全量拷贝零回收。
    ///   区间表同样无记录粒度洞位（VII-3：洞 OR 并入大记录 sparse 位）——<b>记录粒度真相只在使用方</b>。
    ///   本重载让使用方申报活记录 [Start, Start+Length)，引擎按申报精确搬迁。</para>
    /// <para>★ 契约（严苛，A8 信任模型）：申报区间必须 ⊆ [from, to)（越界抛）；<b>未申报的已分配
    ///   区间视为洞，不搬迁</b>——漏报活数据 = 该数据整理后不可达。范围外（from 前、to 后）仍按
    ///   物理 allocated 保守保留。MigrationMap 覆盖全部申报记录的 Start。</para>
    /// </summary>
    /// <param name="from">整理范围起始（含）。</param>
    /// <param name="to">整理范围结束（不含）。</param>
    /// <param name="liveRecords">使用方申报的活记录区间集合（Start + Length）。</param>
    IAsyncOperation<CompactResult> StartRangeCompact(LogicalAddress from, LogicalAddress to,
        IReadOnlyList<(LogicalAddress Start, long Length)> liveRecords);
}
