using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.Epochs;

/// <summary>
/// 状态机操作（如 checkpoint）的当前状态。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct VersionSchemeState : IEquatable<VersionSchemeState>
{
    /// <summary>
    /// 特殊相位值：版本状态机处于静止的稳定态。
    /// </summary>
    public const byte Rest = 0;

    private const int kTotalSizeInBytes = 8;
    private const int kTotalBits = kTotalSizeInBytes * 8;

    // 相位（Phase）
    private const int kPhaseBits = 8;
    private const int kPhaseShiftInWord = kTotalBits - kPhaseBits;
    private const long kPhaseMaskInWord = ((1L << kPhaseBits) - 1) << kPhaseShiftInWord;
    private const long kPhaseMaskInInteger = (1L << kPhaseBits) - 1;

    // 版本号（Version）
    private const int kVersionBits = kPhaseShiftInWord;
    private const long kVersionMaskInWord = (1L << kVersionBits) - 1;

    /// <summary>状态机的内部中间态掩码。</summary>
    private const byte kIntermediateMask = 128;

    [FieldOffset(0)] internal long Word;

    /// <summary>
    /// 自定义相位标记：标识 EPVS 当前处于状态机的哪一步。
    /// </summary>
    public byte Phase
    {
        get { return (byte)((Word >> kPhaseShiftInWord) & kPhaseMaskInInteger); }
        set
        {
            Word &= ~kPhaseMaskInWord;
            Word |= (((long)value) & kPhaseMaskInInteger) << kPhaseShiftInWord;
        }
    }

    /// <summary></summary>
    /// <returns>当前 EPVS 是否处于中间态（正在两个状态之间过渡）。</returns>
    public bool IsIntermediate() => (Phase & kIntermediateMask) != 0;

    /// <summary>
    /// 当前状态的版本号。
    /// </summary>
    public long Version
    {
        get => Word & kVersionMaskInWord;
        private set
        {
            Word &= ~kVersionMaskInWord;
            Word |= value & kVersionMaskInWord;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static VersionSchemeState Copy(ref VersionSchemeState other)
    {
        var info = default(VersionSchemeState);
        info.Word = other.Word;
        return info;
    }

    /// <summary>
    /// 用给定的相位与版本号构造一个状态。
    /// </summary>
    /// <param name="phase">相位。</param>
    /// <param name="version">版本号。</param>
    /// <returns>构造出的状态。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VersionSchemeState Make(byte phase, long version)
    {
        var info = default(VersionSchemeState);
        info.Phase = phase;
        info.Version = version;
        return info;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static VersionSchemeState MakeIntermediate(VersionSchemeState state)
        => Make((byte)(state.Phase | kIntermediateMask), state.Version);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RemoveIntermediate(ref VersionSchemeState state)
    {
        state.Phase = (byte)(state.Phase & ~kIntermediateMask);
    }

    internal static bool Equal(VersionSchemeState s1, VersionSchemeState s2)
    {
        return s1.Word == s2.Word;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"[{Phase},{Version}]";
    }


    public override bool Equals(object? obj)
    {
        return obj is VersionSchemeState other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return Word.GetHashCode();
    }

    public bool Equals(VersionSchemeState other)
    {
        return Word == other.Word;
    }

    /// <summary>
    /// 相等比较。
    /// </summary>
    public static bool operator ==(VersionSchemeState left, VersionSchemeState right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// 不等比较。
    /// </summary>
    public static bool operator !=(VersionSchemeState left, VersionSchemeState right)
    {
        return !(left == right);
    }
}