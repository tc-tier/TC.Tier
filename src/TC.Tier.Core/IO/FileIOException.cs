namespace TC.Tier.Core.IO;

/// <summary>
/// Core/IO 唯一的 IO 异常类型（家族 A·物理层标准）——所有介质实现（磁盘/内存/故障注入）的失败统一出口。
/// <para>★ 铁律：不携带任何存储语义（逻辑地址 / lease / 存储层词汇）——存储语义由消费方在边界包装补充。</para>
/// <para>★ 派生自 <see cref="IOException"/>（BCL）——消费者可用单一 catch (IOException) 兜底。</para>
/// </summary>
public sealed class FileIOException : IOException
{
    /// <summary>创建——带错误分类、操作名与路径上下文。</summary>
    /// <param name="error">跨平台错误码。</param>
    /// <param name="message">异常消息（已含操作与路径描述的成品文案）。</param>
    /// <param name="path">相关文件路径（未知传 null）。</param>
    /// <param name="operation">触发失败的操作名（如 "Write"/"PunchHole"）。</param>
    /// <param name="inner">底层原生异常（无则 null）。</param>
    public FileIOException(IOError error, string message, string? path = null,
        string? operation = null, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
        Path = path;
        Operation = operation ?? string.Empty;
    }

    /// <summary>统一跨平台 IO 错误码。</summary>
    public IOError Error { get; }

    /// <summary>相关文件路径；上下文不可知时为 null。</summary>
    public string? Path { get; }

    /// <summary>触发失败的操作名（如 "Write"/"PunchHole"）；未知为空字符串。</summary>
    public string Operation { get; }

    /// <summary>
    /// 仅 <see cref="IFileHandle.Append"/> 失败时非空——已预留区间的起点（游标空洞位置，D7 失败语义）。
    /// 调用方凭此定位洞的位置，自行决策（重建 / 记账 / 忽略）。
    /// </summary>
    public long? ReservedOffset { get; init; }

    /// <summary>
    /// 仅 <see cref="IFileHandle.CopyRange"/>/<see cref="IFileHandle.CloneRange"/> 部分失败时非空——
    /// 已完成拷贝的字节数。目标文件留半截（不保证原子），调用方凭此决策重续或清尾。
    /// </summary>
    public long? CompletedLength { get; init; }

    /// <inheritdoc/>
    public override string ToString()
    {
        var s = $"{GetType().Name}[{Error}] {Message}";
        if (ReservedOffset is { } ro) s += $" (reservedOffset={ro})";
        if (CompletedLength is { } cl) s += $" (completedLength={cl})";
        return s;
    }
}
