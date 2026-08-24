namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// BlittableRing 专属配置（对齐 DeltaLogSettings : LogSettings）。
/// <para>★ 无额外字段——Ring 通用配置全在基类 RingSettings。</para>
/// <para>★ key 类型由 BlittableRing&lt;TKey&gt; 类型参数提供（sizeof(TKey)），Settings 不配 KeySize。</para>
/// <para>★ 引擎选项直构 ctor（测试/组合根经 StorageEngineOptions 装配，对齐 DeltaLogSettings 形态）。</para>
/// </summary>
public sealed class BlittableRingSettings : RingSettings
{
    /// <summary>缺省配置（引擎名 tc.ring，段 1G）。</summary>
    public BlittableRingSettings() : base() { }

    /// <summary>引擎选项直构。</summary>
    public BlittableRingSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }
}
