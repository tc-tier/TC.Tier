using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 区间状态 byte 编码——高 4 bit=Src（lease 来源）+ 低 4 bit=Phase（阶段）。
/// <para>★ kind 只存在于 byte 内部编码，外部完全不接触 OperationKind。</para>
/// <para>★ 终态（Committed/Wasted/Aborted）不带 Src 前缀——成功/失败不关心来源。</para>
/// <para>★ 只有在途态（Leased）带 Src——Rollback 分发/诊断用。</para>
/// <para>★ 投影/读路径用位运算快速判定（IsCommitted/IsInFlight），无 switch。</para>
/// </summary>
internal static class ExtentStateCode
{
    // ═══ 高 4 bit = Src（lease 来源，仅中间态带）═══
    public const byte SrcAppend  = 0x10;
    public const byte SrcWrite   = 0x20;
    public const byte SrcReclaim = 0x30;   // 中间/头/尾三种空间类型在途共用
    public const byte SrcCompact = 0x40;

    // ═══ 低 4 bit = Phase ═══
    public const byte PhaseLeased    = 0x01;  // 在途（中间态，排他）
    public const byte PhaseCommitted = 0x02;  // 成功终态（有数据，可读）
    public const byte PhaseWasted    = 0x03;  // 空洞（可覆写：Append/Write失败、Reclaim打洞成功）
    public const byte PhaseAborted   = 0x04;  // 毒化洞（数据完好/已归零二态未知——读拒绝；Reclaim 族可幂等重占、Compact 可整理；Write/Append 拒）

    // ═══ 组合值（7 个语义状态）═══
    public const byte AppendLeased   = SrcAppend  | PhaseLeased;     // 0x11
    public const byte WriteLeased    = SrcWrite   | PhaseLeased;     // 0x21
    public const byte ReclaimLeased  = SrcReclaim | PhaseLeased;     // 0x31
    public const byte CompactLeased  = SrcCompact | PhaseLeased;     // 0x41

    public const byte Committed      = PhaseCommitted;               // 0x02（无 Src，成功终态）
    public const byte Wasted         = PhaseWasted;                  // 0x03（无 Src，空洞）
    public const byte Aborted        = PhaseAborted;                 // 0x04（无 Src，毒化洞——Reclaim 族可幂等重占（L1））

    // ═══ 快速判定（位运算，无 switch）═══

    /// <summary>是否 Committed（成功终态，有数据可读）。投影热路径用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCommitted(byte s) => (s & 0x0F) == PhaseCommitted;

    /// <summary>是否在途（中间态，排他）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInFlight(byte s) => (s & 0x0F) == PhaseLeased;

    /// <summary>是否可被 Write/Reclaim/Compact 占（Committed 或 Wasted）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsOccupiable(byte s)
        => (s & 0x0F) == PhaseCommitted || (s & 0x0F) == PhaseWasted;

    /// <summary>是否毒化洞（punch/commit 非原子窗口二态未知——Reclaim 族可幂等重占（L1），Compact 可整理）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAborted(byte s) => (s & 0x0F) == PhaseAborted;

    /// <summary>取 Src（高 4 bit）——Rollback 分发/诊断用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte SourceOf(byte s) => (byte)(s & 0xF0);

    /// <summary>取 Phase（低 4 bit）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte PhaseOf(byte s) => (byte)(s & 0x0F);
}
