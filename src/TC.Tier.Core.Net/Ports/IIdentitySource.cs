namespace TC.Tier.Core.Net.Ports;

/// <summary>
/// 身份供给端口（spec-12 §8.3——Core.Net 零文件 IO：只定义端口，持久化形态由组装层用
/// Core.IO 实现——两个核心永远在组装层汇合）。
/// <para>★ 契约：首次调用生成（NewRandom）并保存，此后加载同一身份——NodeId 跨重启稳定；
///   实现保证原子性（生成与保存一体，半写不可见）。</para>
/// <para>★ 两供给形态（§8.3）：配置注入（builder .WithIdentity——K8s secret 等运维成熟环境）∥
///   本端口（缺省便利形态——TierFsIdentityStore 等适配器，D6 组装层落地）。</para>
/// </summary>
public interface IIdentitySource
{
    /// <summary>加载或首次创建身份（跨调用/跨重启稳定返回同一 <see cref="NodeIdentity.Id"/>）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask<NodeIdentity> LoadOrCreateAsync(CancellationToken ct = default);
}
