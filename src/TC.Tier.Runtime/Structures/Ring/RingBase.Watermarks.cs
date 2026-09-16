using System.Runtime.CompilerServices;
using TC.Tier.Core.NativeInterop;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 水位 partial——CAS128 水位原子化基座（#163）与 8 水位指针的单调推进。
/// </summary>
public abstract partial class RingBase<TKey>
{
    // ═══ 水位原子化基座（#163——CAS-first 正解，用户拍板 CAS128 + native 对齐块）═══
    // 7 个水位指针驻留 16B 对齐块，经 NativeAtomic128（x64 cmpxchg16b 快路径 / 非对齐分片锁兜底）
    // 原子推进；读 = 校验型 CAS 读（撕裂自愈收敛，冷路径 ~5-8ns）。
    // ★ 热路径零参与：读分档判据走 8B 距离水位（_headDist/_safeSnapshotTailDist，volatile 读），
    //   本基座只在 flush/shift/evict/truncate/meta 冷路径触达——性能无损（实测探针口径不变）。
    // ★ _tailAddress/_dataStart 不入块：tail 推进在 _tailLock 内（分配热路径，CAS +5ns/写不可接受），
    //   dataStart 构造后只读。
    // ★ 原字段声明（RingBase.cs 持久化层/内存水位层两组）由下方同名属性接管——全仓引用点零触改。

    private const int WmSlotCount = 7;
    private const int WmBegin = 0, WmFlushedUntil = 1, WmReadOnly = 2, WmSafeReadOnly = 3,
                      WmHead = 4, WmSafeHead = 5, WmClosedUntil = 6;

    private byte[] _wmBlockRaw = null!;    // pinned 托管数组（GC 永不移动，随实例回收）
    private unsafe UInt128Pair* _wmBlock;  // 16B 对齐基址

    /// <summary>构造期分配水位块（pin 数组 + 16B 对齐——LightEpoch 线程表同范式）。槽位零初始化 = Empty。</summary>
    private unsafe void AllocateWatermarkBlock()
    {
        _wmBlockRaw = GC.AllocateArray<byte>(WmSlotCount * 16 + 16, pinned: true);
        fixed (byte* pRaw = _wmBlockRaw)
        {
            _wmBlock = (UInt128Pair*)((long)pRaw + 15 & ~15L);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe UInt128Pair* WmSlot(int slot) => (UInt128Pair*)((byte*)_wmBlock + slot * 16);

    /// <summary>
    /// ★ 原子单调推进（#163 正解）：仅 newValue &gt; 当前值才推进，check-and-set 单条 cmpxchg16b 原子——
    /// 并发 FlushUntil/Shift 的交错不再可能水位回退（旧 check-then-write 的 TOCTOU 根除）。
    /// </summary>
    private unsafe bool MonotonicUpdateAddr(int slot, LogicalAddress newValue, out LogicalAddress oldValue)
    {
        var p = WmSlot(slot);
        var cur = WmReadRaw(p);
        while (true)
        {
            oldValue = FromPair(cur);
            if (newValue.CompareTo(oldValue) <= 0) return false;
            if (NativeAtomic128.CompareExchange(ref *p, cur, ToPair(newValue))) return true;
            cur = WmReadRaw(p);   // CAS 失败：以内存当前完整值重试（单调无 ABA，必然收敛）
        }
    }

    /// <summary>★ 校验型 CAS 读：撕裂/过期读在 CAS(cur,cur) 失败中自愈收敛（native 失败路径
    /// 保证写回内存当前完整值语义，重读直至快照成立）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe UInt128Pair WmReadRaw(UInt128Pair* p)
    {
        var cur = Unsafe.Read<UInt128Pair>(p);
        while (!NativeAtomic128.CompareExchange(ref *p, cur, cur))
            cur = Unsafe.Read<UInt128Pair>(p);
        return cur;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe LogicalAddress WmRead(int slot) => FromPair(WmReadRaw(WmSlot(slot)));

    /// <summary>原子写（构造/恢复单线程初始化路径专用——推进必须走 <see cref="MonotonicUpdateAddr"/>）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WmWrite(int slot, LogicalAddress value) => Unsafe.Write(WmSlot(slot), ToPair(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128Pair ToPair(LogicalAddress addr)
    {
        var v = addr;
        return Unsafe.As<LogicalAddress, UInt128Pair>(ref v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LogicalAddress FromPair(UInt128Pair pair)
    {
        var v = pair;
        return Unsafe.As<UInt128Pair, LogicalAddress>(ref v);
    }

    /// <summary>★ 测试访问器：flushedUntil 槽的单调推进（水位并发压测直打基座）。</summary>
    internal bool MonotonicUpdateFlushedUntilForTest(LogicalAddress newValue, out LogicalAddress oldValue)
        => MonotonicUpdateAddr(WmFlushedUntil, newValue, out oldValue);

    /// <summary>
    /// ★ 快照导入区水位收口（#166）：ReadOnly 单调推进至导入末尾（导入区进入 flush/驱逐管线，
    ///   不再滞留 mutable），SafeReadOnly 经 epoch drain 跟进；读分档 dist 水位同步推进
    ///   （导入记录在页池完整——不推进则自愈读误判冷走设备 → 假 miss）。
    /// </summary>
    private unsafe void CompleteSnapshotRegion(LogicalAddress end)
    {
        MonotonicUpdateAddr(WmReadOnly, end, out _);
        // ★ bump 保护区由本方法自持：调用方可能不持本引擎 epoch（复制状态机 warmup 线程直调
        //   RingSnapshotWriter.Complete——非恢复核心/apply worker 的已保护区路径）；同线程
        //   Resume/Suspend 配对补保护，已保护 = 条件不成立零开销（Debug 绊线不再误炸直调方）。
        var ownProtection = !_epoch.ThisInstanceProtected();
        if (ownProtection)
            _epoch.Resume();
        try
        {
            _epoch.BumpCurrentEpoch(() => MonotonicUpdateAddr(WmSafeReadOnly, end, out _));
        }
        finally
        {
            if (ownProtection)
                _epoch.Suspend();
        }
        AdvanceSafeSnapshotTail(end);
    }

    // ═══ 水位属性（原 7 个 16B 字段同名接管——全仓读写引用点零触改）═══

    /// <summary>头截断边界（= engine.MinAddress，此地址前数据已回收）。</summary>
    private LogicalAddress _beginAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmBegin);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmBegin, value);
    }

    private LogicalAddress _flushedUntilAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmFlushedUntil);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmFlushedUntil, value);
    }

    private LogicalAddress _safeReadOnlyAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmSafeReadOnly);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmSafeReadOnly, value);
    }

    private LogicalAddress _readOnlyAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmReadOnly);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmReadOnly, value);
    }

    // 内存水位层（meta 可选存，恢复时从 Begin 重建）
    private LogicalAddress _headAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmHead);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmHead, value);
    }

    private LogicalAddress _safeHeadAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmSafeHead);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmSafeHead, value);
    }

    // 内存簿记层（永不落盘，恢复时初始化为 SafeHeadAddress）
    private LogicalAddress _closedUntilAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] get => WmRead(WmClosedUntil);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] set => WmWrite(WmClosedUntil, value);
    }
}
