namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// Log 基类配置（abstract）。通用字段被所有 Log 实现类共享。
/// <para>★ 心智模型（见 Structures-log-rewrite-design.md §0）：IO 底层 = 持久化的内存。
/// Log 不碰段/对齐/落盘细节，只配 IO 模型 + 持久化模型 + group commit 阈值。</para>
/// <para>★ 每个实现类有专属 sealed Settings 子类（对齐 Metadata/Mirror 的 Settings 范式），
/// 承载该实现类独有的配置（如 <see cref="EntryLogSettings"/> 的 group 提交配置）。</para>
/// <para>参见 docs/spec/structures/Log/base.md。</para>
/// </summary>
public abstract class LogSettings : Settings
{
    /// <summary>完整构造——注入主引擎选项（自定义段几何/hints/清理策略）。</summary>
    protected LogSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——默认引擎选项。</summary>
    protected LogSettings(string name = "tc.log") : base(new StorageEngineOptions(name))
    {
    }


    /// <summary>
    /// ★ 页大小位宽（PageSize = 1 &lt;&lt; LogPageSizeBits）。默认 22（4MB）。
    /// <para>★ 页 = 攒批缓冲 + DIO 对齐提交单位（design §2.1）。entry 攒进扇区对齐的页内存，凑满才提交给引擎。</para>
    /// <para>★ 单 entry 不跨页契约：单 entry 必须 ≤ PageSize，大对象由上层分片成多条 entry。</para>
    /// </summary>
    public int LogPageSizeBits { get; init; } = 22;

}