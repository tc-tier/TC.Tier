namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// BlittableRing — Ring 实现类。
/// <para>★ 继承 RingBase，override record 字节几何插槽（override 全 sealed——JIT 去虚化保持）。</para>
/// <para>★ 所有 K/V 读写方法在 RingBase 中实现，仅特定 override + Codec 留在此处。</para>
/// <para>★ 公开且可继承（去类级 sealed）——生成器 [RingKey] 封闭薄类（RingOfLong 等）的派生基座
///   （设计稿 §2：开放泛型内核手写一次，封闭形态生成器产出，消费面只见封闭类型）。</para>
/// </summary>
public partial class BlittableRing<TKey> : RingBase<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// ctor 收 <c>protected internal</c>——★开放泛型不落消费面（设计稿 §2）的编译期闸门：
    /// 外部程序集直接 <c>new BlittableRing&lt;TKey&gt;(...)</c> 即 CS0122 编译错，必须用 [RingKey]
    /// 生成的封闭类型（RingOfLong 等，经 protected 肢调本 ctor）；内核单元测试/基准经 IVT（internal 肢）。
    /// </summary>
    protected internal BlittableRing(BlittableRingSettings settings,
        IFileSystem fs,
        IRecovery<RingRecoveryHints>? recovery = null,
        RingCursorFactory<IRingScanCursor>? cursorFactory = null,
        IRingSnapshot? ringSnapshot = null,
        MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        LightEpoch? epoch = null,
        ILogger? logger = null)
        : base(new Codec(), fs, settings, recovery, cursorFactory, ringSnapshot,
            metaPolicyFactory, metaTransport, epoch, logger)
    {
    }

    /// <inheritdoc/>
    protected override void OnInitialize() => InitializePolicies();

    /// <summary>★ 工厂方法（protected internal 同 ctor 闸门）——外部消费面用封闭类型的同名工厂
    /// （[RingKey] 生成物）；直接 <c>BlittableRing&lt;TKey&gt;.Create(...)</c> 是开放泛型泄漏、CS0122 编译期拒绝
    /// （internal 不可见时绑定器会误绑 IO 扩展同名方法，报错误导——保护级别与 ctor 一致即得清晰 CS0122）。</summary>
    protected internal static BlittableRing<TKey> Create(BlittableRingSettings settings,
        IFileSystem fs,
        IRecovery<RingRecoveryHints>? recovery = null,
        RingCursorFactory<IRingScanCursor>? cursorFactory = null,
        IRingSnapshot? ringSnapshot = null,
        MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        LightEpoch? epoch = null)
    {
        var ring = new BlittableRing<TKey>(settings, fs, recovery, cursorFactory, ringSnapshot, metaPolicyFactory,
            metaTransport, epoch, logger: null);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }

    // === override 字节几何插槽（override sealed → JIT 去虚化；类开放继承但几何不可再变）===
    /// <summary>固定记录大小——0 表示变长（record 大小由 header 自述 PayloadLength/PaddingLength）。</summary>
    protected internal sealed override int FixedRecordSize => 0;

    /// <summary>平均记录大小（溢出阈值/容量估算用）——header + 64B payload 的估算值。</summary>
    protected internal sealed override int AverageRecordSize => RingCodec.HeaderSize + 64;

    /// <summary>读物理地址处 record 的实际几何——header 自述 PayloadLength/PaddingLength 算出（filled=字节数，allocated=对齐后槽长）。</summary>
    /// <param name="phys">record 的物理地址。</param>
    /// <returns>（实际字节数, 对齐后占用槽长）。</returns>
    protected internal sealed override unsafe (int filled, int allocated) GetRecordSize(long phys)
    {
        var headerSpan = new System.ReadOnlySpan<byte>((void*)phys, RingCodec.HeaderSize);
        RingCodec.TryReadHeader(headerSpan, out var fields);
        int total = RingCodec.HeaderSize + (int)fields.PayloadLength + fields.PaddingLength;
        int aligned = (total + RingCodec.Alignment - 1) & ~(RingCodec.Alignment - 1);
        return (total, aligned);
    }

    /// <summary>给定可用字节数下覆盖该 record 所需的最小槽长——BlittableRing 变长估算用平均记录大小。</summary>
    /// <param name="phys">record 的物理地址。</param>
    /// <param name="availableBytes">可用字节数。</param>
    /// <returns>所需槽长（平均记录大小）。</returns>
    protected internal sealed override int GetRequiredRecordSize(long phys, int availableBytes) => AverageRecordSize;
}