namespace TC.Tier.Contracts.Meta;

/// <summary>
/// Meta 策略模式
/// </summary>
public enum MetaPolicyKind
{
    /// <summary>不持久化 meta（no-op，默认）。</summary>
    Disabled,
    /// <summary>独立 meta 引擎 + 固定块覆盖写 + Magic/CRC 校验。</summary>
    Managed,
    /// <summary>统一传输模式——经 <see cref="IMetaTransport"/> 写块/读最后一条：
    /// 上层注入传输实例（自定义介质：单槽文件/远程/KV 皆可），或不注入回落到结构自身的
    /// 存储流（追加 + 倒序扫描）。</summary>
    Transport,
}
