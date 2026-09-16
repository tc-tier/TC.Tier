using System.Collections.Concurrent;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 协议域注册分流单源（spec-12 §3.5——五区制机器纪律）：公开口只放行注册区、
/// 内部口只放行内部核心区、隔离/保留 fail-fast。全部介质实现（TCP/InProcess/…）共用同一校验——
/// 同构门的注册面保障（消费面零差别）。
/// </summary>
internal static class ProtocolRegistration
{
    /// <summary>公开口校验（使用方空间 0x60-0xAF——结构上无法占用内部号）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> 不在注册区 0x60-0xAF。</exception>
    public static void ValidateUserPort(byte protocolId)
    {
        if (!ProtocolIds.IsUserRegistrable(protocolId))
            throw new ArgumentOutOfRangeException(nameof(protocolId),
                $"协议域 0x{protocolId:X2} 不在注册区（0x60-0xAF）——使用方号码自管；内部核心区走 RegisterCoreProtocol（机制面专用）。");
    }

    /// <summary>内部口校验（机制面专用 0x00-0x4F）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> 不在内部核心区 0x00-0x4F。</exception>
    public static void ValidateCorePort(byte protocolId)
    {
        if (!ProtocolIds.IsCore(protocolId))
            throw new ArgumentOutOfRangeException(nameof(protocolId),
                $"内部注册口只放行内部核心区（0x00-0x4F）：收到 0x{protocolId:X2}。");
    }

    /// <summary>登记（handler 非空 + 同 ID 重复注册抛——全介质/全形态同规则）。</summary>
    /// <param name="table">handler 注册表。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="handler">入站处理器。</param>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">同 ID 重复注册。</exception>
    public static void Add<T>(ConcurrentDictionary<byte, T> table, byte protocolId, T handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!table.TryAdd(protocolId, handler))
            throw new InvalidOperationException($"协议域 0x{protocolId:X2} 重复注册。");
    }
}
