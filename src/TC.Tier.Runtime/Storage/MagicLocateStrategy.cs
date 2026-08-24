namespace TC.Tier.Runtime.Storage;

/// <summary>定位算法档位——正确性契约与前置条件的分野。</summary>
public enum MagicLocateStrategy
{
    /// <summary>
    /// 通用档：逐页方向线性扫（页内 alignment 步进匹配）——零布局假设，恒正确。
    /// 零富集载荷（合法数据形态，如索引镜像空桶区）天然免疫。
    /// </summary>
    Linear,

    /// <summary>
    /// 快速档：页级二分 + 页内方向扫，O(log 页数)。
    /// <para>★ 前置条件（使用方断言，错了漏检责任在使用方）：<b>含 magic 页单调</b>——
    /// Last 要求含 magic 页集合是前缀（出现后不再消失，稠密 record 流成立）；
    /// First 要求是后缀（出现之前全无，前缀洞形态成立）。Log/Ring 恢复满足；
    /// 零富集/多洞布局不满足——用 <see cref="Linear"/>。</para>
    /// </summary>
    Monotone,
}