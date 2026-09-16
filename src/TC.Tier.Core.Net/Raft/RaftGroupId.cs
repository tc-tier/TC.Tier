namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// Raft 组标识（二期-C1 §3.1——Multi-Raft 组路由的组键）：8B 小端无符号整数，线上以
/// <c>[GroupId 8B]</c> 固定宽前缀承载于 Raft 家族协议域（0x01/0x03/0x04）载荷首部
/// （§2.1 统一组路由——无特例、无双轨）。
/// <para>★ <see cref="Empty"/>（0）为默认组——同样带前缀；非零组由产品/元数据层分配
///   （Net 只承载与路由，spec 组语义归产品）。8B 取值空间消除元数据层 ID 碰撞焦虑，
///   每消息 8B 开销可忽略（帧头 16B v1 不动的既定前提下载荷内前缀为最小入侵面）。</para>
/// </summary>
/// <param name="Value">8B 无符号组号（0 = 默认组 <see cref="Empty"/>；非零组由产品/元数据层分配，Net 层只承载与路由）。</param>
public readonly record struct RaftGroupId(ulong Value)
{
    /// <summary>默认组（0——单组装配形态与历史语义等价，路由面无特例）。</summary>
    public static RaftGroupId Empty => default;

    /// <summary>是否默认组。</summary>
    public bool IsEmpty => Value == 0;

    /// <summary>诊断/日志形态（十六进制 16 位）。</summary>
    /// <returns>形如 <c>gid:XXXXXXXXXXXXXXXX</c> 的诊断串（组号大写十六进制、宽度 16）。</returns>
    public override string ToString() => $"gid:{Value:X16}";
}
