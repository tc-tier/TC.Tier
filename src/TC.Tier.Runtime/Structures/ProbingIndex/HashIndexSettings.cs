namespace TC.Tier.Runtime.Structures.ProbingIndex;

public sealed class HashIndexSettings : ProbingIndexSettings
{
    /// <summary>引擎选项直构（对齐 BlittableRingSettings/EntryLogSettings 双 ctor 形态）。</summary>
    public HashIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    /// <summary>哈希表<b>初始</b>桶数（2 的幂）——非上限：装载超 0.7 由 Insert 自动翻倍增长
    /// （GrowIndex 函数式换代表，均摊 O(1)/插）。按预期规模直设可省早期重散列。
    /// <para>★ 旧默认 1&lt;&lt;20（134MB 构造期常量、与数据量无关）已废——容量自适应落地。</para></summary>
    public int HashTableCapacity { get; init; } = 1 << 14;

    /// <summary>初始溢出池桶数——桶满 7 slot 链式溢出的缓冲。增长换代时新池=新表桶数/2（下限 1024）。</summary>
    public int OverflowPoolCapacity { get; init; } = 1 << 12;
}
