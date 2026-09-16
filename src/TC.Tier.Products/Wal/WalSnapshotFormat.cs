namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 快照传输格式——写入统一传输面（IAsyncTransferWriter）的三段式，TierWAL 自己的格式
/// （传输面只提供字节容器：header 块 / payload 连续块 / footer 块，边界由消费方格式自判）。
/// <para>布局：</para>
/// <para> - Header 帧：<see cref="WalSnapshotHeader"/>（14B）——一致性点 N₀</para>
/// <para> - Payload 区：<see cref="WalSnapshotFrameHeader"/> + payload（每条 entry 一帧；流式传输无总长
///   前置——导入侧循环读帧，len 非法（footer magic 开头）即进入 Footer 相位，已读的 footer 开头字节补齐校验）</para>
/// <para> - Footer 帧：<see cref="WalSnapshotFooter"/>（24B）——CRC 增量覆盖 Header + 全部 payload 帧字节
///   （footer 自身不参与）</para>
/// </summary>
internal static class WalSnapshotFormat
{
    public const int HeaderSize = WalSnapshotHeaderCodec.StructSize;
    public const int FooterSize = WalSnapshotFooterCodec.StructSize;
    public const int FrameHeaderSize = WalSnapshotFrameHeaderCodec.StructSize;

    /// <summary>单条 payload 上限（len 帧判定用；远超任何合法 entry，防误读 footer magic）。</summary>
    public const int MaxPayloadLength = 1 << 28;   // 256MB——单 entry 不跨页契约（4MB 页）的宽松上界

    // ═══ Header ═══

    /// <summary>序列化 Header 帧到 dst（magic/version + 一致性点 N₀）。</summary>
    /// <param name="dst">写入目标缓冲区，长度不小于 HeaderSize。</param>
    /// <param name="snapshotIndex">快照一致性点 N₀（快照覆盖到的最大 index）。</param>
    public static void WriteHeader(Span<byte> dst, long snapshotIndex)
    {
        var header = new WalSnapshotHeader
        {
            MagicValue = WalSnapshotHeader.Magic,
            Version = WalSnapshotHeader.CurrentVersion,
            SnapshotIndex = snapshotIndex,
        };
        WalSnapshotHeaderCodec.Write(dst, in header, validate: true);
    }

    /// <summary>解析 Header；失败（magic/长度非法）= 非本格式快照。</summary>
    /// <param name="src">快照字节（≥ HeaderSize）。</param>
    /// <param name="snapshotIndex">输出：一致性点 N₀（失败 = 0）。</param>
    /// <returns>true = 解析成功；false = 魔数/版本/长度非法。</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> src, out long snapshotIndex)
    {
        snapshotIndex = 0;
        if (src.Length < HeaderSize) return false;
        if (WalSnapshotHeaderCodec.Read_MagicValue(src) != WalSnapshotHeader.Magic) return false;
        if (WalSnapshotHeaderCodec.Read_Version(src) != WalSnapshotHeader.CurrentVersion) return false;
        snapshotIndex = WalSnapshotHeaderCodec.Read_SnapshotIndex(src);
        return true;
    }

    // ═══ Payload 帧 ═══

    /// <summary>写一条 payload 帧（帧头 [len 4B] + payload 字节）到 dst。</summary>
    /// <param name="dst">写入目标缓冲区，长度不小于 FrameHeaderSize + payload.Length。</param>
    /// <param name="payload">该条 entry 的原始字节（非空帧——len ∈ (0, MaxPayloadLength]）。</param>
    public static void WritePayloadFrame(Span<byte> dst, ReadOnlySpan<byte> payload)
    {
        var header = new WalSnapshotFrameHeader { PayloadLength = payload.Length };
        WalSnapshotFrameHeaderCodec.Write(dst, in header, validate: true);
        payload.CopyTo(dst[FrameHeaderSize..]);
    }

    /// <summary>判定帧头长度合法（len ∈ (0, MaxPayloadLength]）——非法 = Footer 区开始。</summary>
    /// <param name="len">帧头读出的 payload 长度。</param>
    /// <returns>true = 合法帧；false = 进入 Footer 相位。</returns>
    public static bool IsValidFrameLength(int len) => len > 0 && len <= MaxPayloadLength;

    // ═══ Footer ═══

    /// <summary>序列化 Footer 帧（magic + 条数 + payload 总长 + CRC）到 dst。</summary>
    /// <param name="dst">写入目标缓冲区，长度不小于 FooterSize。</param>
    /// <param name="entryCount">快照 entry 总条数（导入侧校验基准）。</param>
    /// <param name="totalPayload">全部 payload 帧的字节总数（导入侧校验基准）。</param>
    /// <param name="crc">覆盖 Header + 全部 payload 帧字节的 CRC32（footer 自身不参与）。</param>
    public static void WriteFooter(Span<byte> dst, long entryCount, long totalPayload, uint crc)
    {
        var footer = new WalSnapshotFooter
        {
            MagicValue = WalSnapshotFooter.Magic,
            EntryCount = entryCount,
            TotalPayload = totalPayload,
            Crc = crc,
        };
        WalSnapshotFooterCodec.Write(dst, in footer, validate: true);
    }

    /// <summary>校验 Footer（magic/条数/总长/CRC——CRC 由调用方对 Header+Payload 字节增量计算）。</summary>
    /// <param name="src">Footer 字节（≥ FooterSize）。</param>
    /// <param name="entryCount">期望的 entry 条数。</param>
    /// <param name="totalPayload">期望的 payload 总字节数。</param>
    /// <param name="crc">调用方增量计算的 CRC32。</param>
    /// <returns>true = Footer 全部校验通过；false = 魔数/条数/总长/CRC 不符。</returns>
    public static bool TryValidateFooter(ReadOnlySpan<byte> src, long entryCount, long totalPayload, uint crc)
    {
        if (src.Length < FooterSize) return false;
        if (WalSnapshotFooterCodec.Read_MagicValue(src) != WalSnapshotFooter.Magic) return false;
        if (WalSnapshotFooterCodec.Read_EntryCount(src) != entryCount) return false;
        if (WalSnapshotFooterCodec.Read_TotalPayload(src) != totalPayload) return false;
        return WalSnapshotFooterCodec.Read_Crc(src) == crc;
    }
}
