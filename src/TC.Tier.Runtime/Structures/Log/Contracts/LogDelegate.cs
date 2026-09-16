namespace TC.Tier.Runtime.Structures.Log.Contracts;
using TC.Tier.Contracts.Storage;

/// <summary>
/// 扫描游标工厂委托签名（LogBase owner + 物理区间）。
/// <para>★ snapshotMode（spec-10 根治）：Consistent = 构造期区间段共享锁全程持有（快照隔离——恢复扫描等
/// 单线程场景）；DirtyRead = 逐段短暂共享锁（游标锁——读写并发常态，复制读等高频路径；异步轨以
/// drain worker 段独占锁提供物理互斥，见 SequentialReader 头注释）。默认 Consistent（既有行为）。</para>
/// <para>★ leasePages：页租借（零拷贝读——2026-08-29 性能：apply 读热路径 ToArray 消除）。
/// false（默认）= 换页即还池（CurrentPayload/CurrentPayloadMemory 禁跨 MoveNext 持有）；
/// true = 游标持有全部已读页至 Dispose（CurrentPayloadMemory 有效至 Dispose——批量切片直用的前提）。</para>
/// </summary>
/// <param name="startAddress">扫描起点 LogicalAddress（default = data 区头；entry 地址则游标自动跳过其前字节）。</param>
/// <param name="endAddress">扫描终点 LogicalAddress（default = 结构自定的可靠落盘水位）。</param>
/// <param name="verifyCrc">true = 逐条校验记录 CRC；false = 仅整页 PageFrame CRC 校验。默认 false。</param>
/// <param name="snapshotMode">一致性模式：Consistent（快照隔离）/ DirtyRead（游标锁）。默认 Consistent。</param>
/// <param name="leasePages">true = 页租借（换页不还池）；false = 换页即还池。默认 false。</param>
/// <returns>打开的扫描游标实例（类型参数约束为 <see cref="ILogCursor"/>）。</returns>
public delegate TLogCursor LogCursorFactory<out TLogCursor>(LogicalAddress startAddress, LogicalAddress endAddress,
    bool verifyCrc = false, SnapshotMode snapshotMode = SnapshotMode.Consistent, bool leasePages = false)
    where TLogCursor : ILogCursor;

/// <summary>
/// ★ EntryLog 重放回调（同步）：每条已 commit entry 触发一次。
/// </summary>
/// <param name="payload">零拷贝 Span（指向游标读帧内），回调返回前有效，禁止持有跨调用。</param>
/// <param name="isMeta">是否为元数据</param>
/// <param name="entryAddress">entry 起始 LogicalAddress（可用于断点续传/去重）</param>
public delegate void EntryReplayHandler(ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress entryAddress);

/// <summary>
/// ★ EntryLog 重放回调（异步）：每条已 commit entry 触发一次。
/// </summary>
/// <param name="payload">零拷贝 Span（指向游标读帧内），回调返回前有效，禁止持有跨调用。</param>
/// <param name="isMeta">是否为元数据</param>
/// <param name="entryAddress">entry 起始 LogicalAddress（可用于断点续传/去重）</param>
/// <param name="ct">取消令牌</param>
/// <returns>异步回调须完成的 ValueTask；重放循环 await 其完成后再推进下一条 entry。</returns>
public delegate ValueTask AsyncEntryReplayHandler(ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress entryAddress,
    CancellationToken ct = default);