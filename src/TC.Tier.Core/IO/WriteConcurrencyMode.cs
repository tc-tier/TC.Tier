namespace TC.Tier.Core.IO;

/// <summary>
/// 同文件写并发档（V2 §2.1——实测判定门两极显式旋钮；virtual 介质挂载级）。
/// <para>判定门实测（2026-08-26，`--tier-volume-write-probe same`，64KB 覆写，本机快速载体）：
/// Serial 4 写者 ≈ 1×（全串行零争用）、Parallel ≈ 0.57×（争用损失——规划/提交串行 + 载体句柄
/// 共享的代价大于数据段并行收益）；慢载体（真磁盘 IO 主导数据段）Parallel 收益大——按消费形态选档。</para>
/// </summary>
public enum WriteConcurrencyMode
{
    /// <summary>同文件写全串行（强序、零争用——快速载体上的同文件多写者最优；缺省）。</summary>
    Serial = 0,

    /// <summary>同文件不相交区间写并发（数据段锁外 + 合并提交——慢载体并行收益大）。</summary>
    Parallel = 1,
}
