namespace TC.Tier.Core.IO;

/// <summary>
/// 预分配方式轴（IS-04 统一词汇——四介质同一套语义；缺省 = 现行行为）。
/// <para><see cref="Metadata"/>：逻辑占位（稀疏——未写区域不占物理块；创建快、写时按需分配）。</para>
/// <para><see cref="Full"/>：物理占位（非稀疏——创建时物化全部空间，一次性成本显式化由部署方承担，
///   换运行时零分配抖动；生产数据库形态"raw + 预分配 + 非稀疏 + DIO"的预分配维度）。</para>
/// </summary>
public enum PreallocationMode
{
    /// <summary>逻辑占位（稀疏）——缺省。</summary>
    Metadata = 0,

    /// <summary>物理占位（非稀疏——创建时物化全部空间）。</summary>
    Full = 1,
}
