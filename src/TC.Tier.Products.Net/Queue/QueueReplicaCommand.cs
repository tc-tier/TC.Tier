using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net;
using TC.Tier.Products.Queue;

namespace TC.Tier.Products.Net.Queue;

/// <summary>队列复制版协议域（域号分配表见 Products.Net/COORDINATION.md——0x60 队列 / 0x61 KV 预留）。</summary>
public static class QueueReplicaProtocol
{
    /// <summary>协议域号。</summary>
    public const byte Domain = 0x60;
}

/// <summary>GroupCmd 子操作。</summary>
public enum QueueGroupOp : byte
{
    /// <summary>创建消费组（配置创建时定格 + 初始辖权 = 提案节点）。</summary>
    Create = 1,
    /// <summary>删除消费组。</summary>
    Delete = 2,
    /// <summary>复位组（fence 解除 + 位点按 startAt 重置 + 代次 +1）。</summary>
    Reset = 3,
}

/// <summary>ExpireCmd 子操作。</summary>
public enum QueueExpireOp : byte
{
    /// <summary>到期/否认记账（计数终值 + 死信终结——辖权扫描的确定性结果）。</summary>
    UpdateEntries = 1,
    /// <summary>Claim 抢占（组代次 +1——旧持有者迟交 fence；pending 改派为辖权本地簿记）。</summary>
    Claim = 2,
}

/// <summary>
/// 到期回收条目线帧（[BinaryLayout] 21B——声明在本程序集：跨程序集嵌套布局 = 硬编码表判例，
/// [WireMessage] 族嵌套件必须同 assembly）。布局：<c>[Address 16B][RedeliveryCount 4B][DeadLettered 1B]</c>。
/// <para>★ 域类型 = <see cref="QueueExpireEntry"/>（Products 公共端口契约）——apply 入口逐条映射。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 21)]
public struct QueueExpireEntryFrame
{
    /// <summary>回收条目地址。</summary>
    [FieldOffset(0)] public LogicalAddress Address;

    /// <summary>重投计数终值。</summary>
    [FieldOffset(16)] public int RedeliveryCount;

    /// <summary>死信终结标记（1 = true——BinaryLayout Emit 集不含 bool，字节直存）。</summary>
    [FieldOffset(20)] public byte DeadLettered;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="address">回收条目地址。</param>
    /// <param name="redeliveryCount">重投计数终值。</param>
    /// <param name="deadLettered">死信终结标记。</param>
    public QueueExpireEntryFrame(LogicalAddress address, int redeliveryCount, bool deadLettered)
    {
        Address = address;
        RedeliveryCount = redeliveryCount;
        DeadLettered = deadLettered ? (byte)1 : (byte)0;
    }
}

/// <summary>
/// 复制命令族（tierqueue-replicated-spec §2——[WireMessage] 声明式生成，六命令族线编码单点）。
/// <para>★ 线格式：<c>[Tag 1B][CorrId 8B][子类字段声明序]</c>——tag 即命令族；CorrId = 提案方
/// 等待者键（节点内在途唯一）。畸形输入（未知 tag/截断/超上限）= TryDecode false——确定性
/// 拒绝语义由调用方承接（raft 日志损坏形态仍 fail-fast，见状态机消费点）。</para>
/// <para>★ 组名/错误文本 = UTF8 blob（[Len 4B][bytes]，MaxCount 防御上限）；地址/条目批 =
/// [Count 4B][item×N]。Group/Expire 单 tag 带 Op 成员（生命周期低频—— union 全字段恒编码，
/// apply 按 Op 取用）。字节金样：QueueCommandGoldenTests 逐命令钉死。</para>
/// </summary>
[WireMessage]
public abstract record QueueCommand
{
    /// <summary>组名 blob 防御上限（原手写解码同值——512B）。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>单命令 payload 防御上限（16MB——对齐传输帧上界）。</summary>
    public const int MaxPayloadBytes = 1 << 24;

    /// <summary>确认地址批 / 到期条目批防御上限（对齐 MaxInFlight 缺省 4096）。</summary>
    public const int MaxBatchEntries = 4096;

    /// <summary>关联 ID（提案方等待者键——节点内在途唯一，单调计数即可；重放条目无等待者即不存储）。</summary>
    [WireMember]
    public ulong Correlation { get; init; }
}

/// <summary>入队（envelope 决策 + payload——确定性地址 = apply 序）。</summary>
[WireMessageTag(0x01)]
public sealed record QueueEnqueueCommand : QueueCommand
{
    /// <summary>是否延迟消息（due 随命令复制——apply 不看时钟）。</summary>
    [WireMember] public bool Delayed { get; init; }

    /// <summary>延迟时刻（UTC ticks；非延迟 = 0）。</summary>
    [WireMember] public long DueTime { get; init; }

    /// <summary>是否幂等生产。</summary>
    [WireMember] public bool Idempotent { get; init; }

    /// <summary>幂等生产者 ID。</summary>
    [WireMember] public long ProducerId { get; init; }

    /// <summary>幂等序号。</summary>
    [WireMember] public long Seq { get; init; }

    /// <summary>原 payload 字节。</summary>
    [WireMember(MaxCount = QueueCommand.MaxPayloadBytes)]
    public ReadOnlyMemory<byte> Payload { get; init; }
}

/// <summary>确认（组 + epoch + 地址批——游标/skip 推进）。</summary>
[WireMessageTag(0x02)]
public sealed record QueueAckCommand : QueueCommand
{
    /// <summary>消费组名（UTF8）。</summary>
    [WireMember(MaxCount = QueueCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> Name { get; init; }

    /// <summary>提案时组代次（apply 侧 fencing 校验凭据）。</summary>
    [WireMember] public long Epoch { get; init; }

    /// <summary>确认地址批（缓冲确认已由提案侧并入）。</summary>
    [WireMember(MaxCount = QueueCommand.MaxBatchEntries)]
    public IReadOnlyList<LogicalAddress> Addresses { get; init; } = [];
}

/// <summary>组生命周期（Create/Delete/Reset 单 tag——Op 定案，全字段恒编码 apply 按 Op 取用）。</summary>
[WireMessageTag(0x03)]
public sealed record QueueGroupCommand : QueueCommand
{
    /// <summary>子操作（byte 载体——WireMessage 成员集不含 enum；语义值见 <see cref="Operation"/>）。</summary>
    [WireMember] public byte Op { get; init; }

    /// <summary>子操作（Op 的枚举视图——apply 分派用；方法形态规避生成器属性成员扫描）。</summary>
    public QueueGroupOp Operation() => (QueueGroupOp)Op;

    /// <summary>组名（UTF8）。</summary>
    [WireMember(MaxCount = QueueCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> Name { get; init; }

    /// <summary>起点形态（byte 载体——WireMessage 成员集不含 enum；语义值见 <see cref="StartPoint"/>）。</summary>
    [WireMember] public byte StartAt { get; init; }

    /// <summary>起点形态（StartAt 的枚举视图——apply 装配用；方法形态规避生成器属性成员扫描）。</summary>
    public GroupStartAt StartPoint() => (GroupStartAt)StartAt;

    /// <summary>Address 形态起点。</summary>
    [WireMember] public LogicalAddress StartAddress { get; init; }

    /// <summary>可见性超时毫秒（Create）。</summary>
    [WireMember] public long VisibilityTimeoutMs { get; init; }

    /// <summary>重投上限（Create）。</summary>
    [WireMember] public int MaxRedeliveries { get; init; }

    /// <summary>初始辖权节点（Create——提案节点）。</summary>
    [WireMember] public NodeId Home { get; init; }
}

/// <summary>到期/抢占（计数终值回收 or 代次递增——单 tag 带 Op）。</summary>
[WireMessageTag(0x04)]
public sealed record QueueExpireCommand : QueueCommand
{
    /// <summary>子操作（byte 载体——WireMessage 成员集不含 enum；语义值见 <see cref="Operation"/>）。</summary>
    [WireMember] public byte Op { get; init; }

    /// <summary>子操作（Op 的枚举视图——apply 分派用；方法形态规避生成器属性成员扫描）。</summary>
    public QueueExpireOp Operation() => (QueueExpireOp)Op;

    /// <summary>消费组名（UTF8）。</summary>
    [WireMember(MaxCount = QueueCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> Name { get; init; }

    /// <summary>回收条目（UpdateEntries；Claim = 空）。</summary>
    [WireMember(MaxCount = QueueCommand.MaxBatchEntries)]
    public IReadOnlyList<QueueExpireEntryFrame> Entries { get; init; } = [];
}

/// <summary>辖权授予/移交/接管（home 更新 + 组代次 +1——apply 恒 fencing 递增）。</summary>
[WireMessageTag(0x05)]
public sealed record QueueHomeCommand : QueueCommand
{
    /// <summary>消费组名（UTF8）。</summary>
    [WireMember(MaxCount = QueueCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> Name { get; init; }

    /// <summary>新辖权节点。</summary>
    [WireMember] public NodeId NewHome { get; init; }
}

/// <summary>空间回收（retention/truncate 守卫语义）。</summary>
[WireMessageTag(0x06)]
public sealed record QueueRetentionCommand : QueueCommand
{
    /// <summary>是否携带截断目标（false = 按回收线推进）。</summary>
    [WireMember] public bool HasTarget { get; init; }

    /// <summary>截断目标地址。</summary>
    [WireMember] public LogicalAddress Target { get; init; }
}
