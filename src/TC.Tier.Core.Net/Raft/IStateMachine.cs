using TC.Tier.Contracts.Structures;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 业务状态机接口（spec-05 §1 定案）——日志即状态机：业务状态 = 已提交日志前缀的确定性函数；重建 = 重放。
/// <para>★ 业务方唯一必须实现 <see cref="ApplyAsync"/>（按序应用已提交命令——index 单调递增、与日志序一致；
///   只被 ApplyPipeline 单 worker 调用——业务存储无并发写，结构层积木直接可用）。</para>
/// <para>★ at-least-once 契约：apply 可能重复（断电重放 appliedIndex 之后区间）——实现须幂等/容忍重复
///   （raft 标准语义；at-most-once 会话去重归用户面 spec-08）。</para>
/// <para>★ <see cref="ExportStateAsync"/>/<see cref="ImportStateAsync"/> = 可选优化路径（业务状态导出/导入，
///   冷启动免全量重放——存储与传输由使用方自理，协议层零绑定）；不使用不阻塞任何正确性。</para>
/// </summary>
public interface IStateMachine
{
    /// <summary>
    /// 按序应用已提交命令（index 单调递增、与日志序一致；重复调用容忍——幂等）。
    /// </summary>
    /// <param name="index">命令的日志 index。</param>
    /// <param name="command">命令内容（帧 payload——业务方定义格式）。
    /// <para>★ 生命周期契约（零拷贝）：command 仅在本次调用期间有效（apply 读可走存储
    /// 页缓冲零拷贝切片——后续读取推进后视图失效）——<b>禁止跨调用持有/存储</b>，
    /// 需要留存内容的实现须在调用内拷贝。</para></param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default);

    /// <summary>可选优化：业务状态导出（冷启动免全量重放——存储与传输由使用方自理）。</summary>
    /// <param name="writer">异步传输写入面（业务方自定义存储/序列化格式）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask ExportStateAsync(IAsyncTransferWriter writer, CancellationToken cancellationToken = default) => default;

    /// <summary>可选优化：业务状态导入（与 <see cref="ExportStateAsync"/> 配对）。</summary>
    /// <param name="reader">异步传输读取面（与 <see cref="ExportStateAsync"/> 导出格式对称消费）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask ImportStateAsync(IAsyncTransferReader reader, CancellationToken cancellationToken = default) => default;
}
