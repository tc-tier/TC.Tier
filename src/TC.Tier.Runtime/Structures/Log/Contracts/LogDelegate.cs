namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// 扫描游标工厂委托签名（LogBase owner + 物理区间）。
/// </summary>
public delegate TLogCursor LogCursorFactory<out TLogCursor>(LogicalAddress startAddress, LogicalAddress endAddress,
    bool verifyCrc = false)
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
public delegate ValueTask AsyncEntryReplayHandler(ReadOnlySpan<byte> payload, bool isMeta, LogicalAddress entryAddress,
    CancellationToken ct = default);