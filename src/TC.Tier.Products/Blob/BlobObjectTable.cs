using System.Buffers.Binary;
using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Snapshot;

using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Products.Blob;

/// <summary>
/// 对象表——Blob 的 meta 真相源（tierblob-spec §2/§5）。
/// <para>★ 形态：表引擎（<see cref="StreamSnapshot"/>，<c>{name}.blob.meta</c>）上的<b>版本链记录帧</b>流——
///   每条登记/删除/标损 = 一帧 [Header 14B][payload 48B+对齐补零][Footer 28B]，CRC64 帧级自校验；
///   恢复 = meta O(1) 水位 + 定步长重放（后记录覆盖同句柄，墓碑即删除）。"VersionedMetadata 版本链"
///   的帧化落地：记录族格式/校验全复用结构层帧机制，零新格式原语。</para>
/// <para>★ 每条记录帧扇区对齐（payload 内补零）——帧长恒为步长整倍数，物理布局 = 逻辑布局，
///   重启后恒等映射读回成立。</para>
/// <para>★ 持久化收口：每条登记 = 帧闭环 → 表引擎 Prepare（fsync + 水位 meta）→ ConfirmCommitted
///   ——表水位恒领先/对齐于登记事实，任何崩溃窗口下句柄不丢（已返句柄重启必在）。</para>
/// <para>★ 2PC（裁定⑤）：会话域（Prepare → Confirm/Abort）内的登记挂 undo 链延迟提交——
///   Abort = 表帧尾截断（ReclaimTail）+ 内存镜像回退。</para>
/// </summary>
/// <summary>
/// 对象表记录 payload（48B [BinaryLayout] 声明式生成）：SegId(4) Ext(4) Offset(8) Length(8)
/// FrameLength(8) CreatedTicks(8) 状态+保留尾(8，低字节 = <see cref="BlobState"/>，高 7B 恒 0——
/// 末字段单 ulong 收口满足 TCSG001 尺寸一致校验）。LogicalAddress 摊平三字段（跨程序集嵌套布局判例）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct BlobObjectEntryPayload
{
    /// <summary>对象句柄 SegId。</summary>
    [FieldOffset(0)] internal int ObjectIdSegId;

    /// <summary>对象句柄 Extension。</summary>
    [FieldOffset(4)] internal int ObjectIdExtension;

    /// <summary>对象句柄 Offset。</summary>
    [FieldOffset(8)] internal long ObjectIdOffset;

    /// <summary>用户数据长度。</summary>
    [FieldOffset(16)] internal long Length;

    /// <summary>帧总长（含头/尾/对齐）。</summary>
    [FieldOffset(24)] internal long FrameLength;

    /// <summary>创建时刻（UTC ticks）。</summary>
    [FieldOffset(32)] internal long CreatedTicks;

    /// <summary>状态（低字节）+ 保留尾 7B（恒 0）。</summary>
    [FieldOffset(40)] internal ulong StateAndReserved;

    /// <summary>状态视图。</summary>
    internal BlobState State => (BlobState)(byte)StateAndReserved;
}

internal sealed class BlobObjectTable
{
    /// <summary>表条目内存镜像。</summary>
    /// <param name="ObjectId">对象句柄（帧流起始地址）。</param>
    /// <param name="Length">用户数据长度。</param>
    /// <param name="FrameLength">帧总长（含头 14B + 数据对齐 + 尾 28B；扇区整倍数）。</param>
    /// <param name="CreatedTicks">创建时刻（UTC Ticks）。</param>
    /// <param name="State">Active / Tombstone。</param>
    public readonly record struct Entry(LogicalAddress ObjectId, long Length, long FrameLength, long CreatedTicks, BlobState State);

    /// <summary>记录 payload 定长（48B = 16B 句柄 + 8B 长度 + 8B 帧长 + 8B 创建时刻 + 1B 状态 + 7B 保留）。</summary>
    public const int PayloadSize = 48;

    /// <summary>帧头大小（结构层流式帧）。</summary>
    public const int FrameHeaderSize = 14;

    /// <summary>帧尾大小（结构层流式帧）。</summary>
    public const int FrameFooterSize = 28;

    private readonly StreamSnapshot _snapshot;
    private readonly object _lock = new();

    /// <summary>内存镜像（句柄 Offset → 条目；地址序迭代 = SortedDictionary）。</summary>
    private readonly SortedDictionary<long, Entry> _entries = new();

    /// <summary>undo 链（非 null = participant 会话域开启，登记延迟提交挂此链）。</summary>
    private List<(long Key, Entry? Previous)>? _undo;

    /// <summary>内部注册序号（表引擎 2PC seq 域——独立于外部参与者 seq，CAS 单调裁决）。</summary>
    private long _internalSeq;

    /// <summary>构造（internal——Builder 装配）。</summary>
    public BlobObjectTable(StreamSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>表引擎（TierBlob 生命周期编排用）。</summary>
    public StreamSnapshot TableSnapshot => _snapshot;

    /// <summary>记录帧步长（payload 补零至扇区整倍数——物理=逻辑不变式）。</summary>
    public long RecordStride
    {
        get
        {
            var raw = FrameHeaderSize + PayloadSize + FrameFooterSize;   // 90B
            var sector = _snapshot.SectorSize;
            return (raw + sector - 1) / sector * sector;
        }
    }

    /// <summary>用户数据长度 → 帧总长（扇区整倍数；数据区含对齐补零）。</summary>
    public static long FrameLengthOf(long userLength, long sectorSize)
    {
        var raw = FrameHeaderSize + userLength + FrameFooterSize;
        return (raw + sectorSize - 1) / sectorSize * sectorSize;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 登记（写侧——产品写闸内单线程调）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>登记条目（帧闭环 + Prepare/Confirm 持久收口 + 内存镜像推进）。会话域内 = 延迟提交挂 undo。</summary>
    public async ValueTask RegisterAsync(Entry entry, CancellationToken ct)
    {
        var undo = BeginMutation(entry.ObjectId.Offset, previous: null);
        try
        {
            await AppendRecordAsync(entry, ct).ConfigureAwait(false);
            if (undo is null)
                CommitInternal();
            ApplyInMemory(entry);
        }
        catch
        {
            // 帧写失败（取消/IO 错）——镜像未动；会话域 undo 残留由 Abort 统一回退
            if (undo is not null)
            {
                lock (_lock) undo.RemoveAt(undo.Count - 1);
            }
            throw;
        }
    }

    /// <summary>同步登记（恢复对账路径——表引擎已就绪，同步帧写）。</summary>
    public void Register(Entry entry)
    {
        BeginMutation(entry.ObjectId.Offset, previous: null);
        AppendRecord(entry);
        CommitInternal();
        ApplyInMemory(entry);
    }

    /// <summary>墓碑登记（状态覆写为 Tombstone 后走常规登记）。</summary>
    public ValueTask RegisterTombstoneAsync(Entry entry, CancellationToken ct)
        => RegisterAsync(entry with { State = BlobState.Tombstone }, ct);

    /// <summary>会话域开启标志（participant Prepare 置位——此后登记延迟提交）。</summary>
    public bool SessionOpen => Volatile.Read(ref _undo) is not null;

    /// <summary>会话域内登记（participant 事务挂起——不落 meta，Confirm/Abort 统一收口；undo 链记录回退）。</summary>
    public void RegisterDeferred(Entry entry)
    {
        BeginMutation(entry.ObjectId.Offset, previous: null);
        AppendRecord(entry);
        ApplyInMemory(entry);
    }

    /// <summary>会话域内墓碑（undo 捕获前状态——Abort 复活对象）。</summary>
    public void RegisterDeferredTombstone(Entry entry)
    {
        TryGet(entry.ObjectId, out var previous);
        var tomb = entry with { State = BlobState.Tombstone };
        BeginMutation(entry.ObjectId.Offset, previous);
        AppendRecord(tomb);
        ApplyInMemory(tomb);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 查询（镜像零 IO）
    // ═══════════════════════════════════════════════════════════════════

    public bool TryGet(LogicalAddress objectId, out Entry entry)
    {
        lock (_lock) return _entries.TryGetValue(objectId.Offset, out entry);
    }

    /// <summary>地址序快照（过滤器内条目拷贝——锁内收集，锁外交付）。
    /// ★ 边界语义：default(LogicalAddress)=Empty(0) 是合法地址——ToAddress.Offset ≤ 0 视为无上界
    /// （真实上界恒 &gt; 0）；FromAddress.Offset 起自然过滤（键恒 ≥ 0）。</summary>
    public List<Entry> Snapshot(BlobListFilter filter)
    {
        lock (_lock)
        {
            var result = new List<Entry>();
            var toOffset = filter.ToAddress.Offset;
            foreach (var (key, entry) in _entries)
            {
                if (key < filter.FromAddress.Offset) continue;
                if (toOffset > 0 && key >= toOffset) break;
                if (entry.State == BlobState.Tombstone && !filter.IncludeTombstones) continue;
                result.Add(entry);
            }
            return result;
        }
    }

    /// <summary>全部条目地址序快照（恢复对账/回收档用）。</summary>
    public List<Entry> SnapshotAll()
    {
        lock (_lock) return new List<Entry>(_entries.Values);
    }

    public (long Active, long Tombstones, long TombstoneBytes) CountStates()
    {
        lock (_lock)
        {
            long active = 0, tombs = 0, tombBytes = 0;
            foreach (var e in _entries.Values)
            {
                if (e.State == BlobState.Active) active++;
                else { tombs++; tombBytes += e.FrameLength; }
            }
            return (active, tombs, tombBytes);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 2PC（裁定⑤——participant 会话域）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>participant Prepare：开启会话域（此后登记延迟提交）+ 表引擎 Prepare（fsync + 悬干水位 meta）。</summary>
    public void PrepareSession(long seq)
    {
        if (Volatile.Read(ref _undo) is null)
        {
            lock (_lock) _undo ??= new List<(long, Entry?)>();
        }
        _snapshot.Prepare(seq);
    }

    /// <summary>participant Confirm：会话域内登记统一提交（fsync + 提交水位）+ undo 链清空。</summary>
    public void ConfirmSession(long seq)
    {
        var pending = Volatile.Read(ref _undo);
        if (pending is { Count: > 0 })
            _snapshot.Prepare(seq);   // 延迟帧此刻 fsync（登记帧此前仅引擎缓冲）
        _snapshot.ConfirmCommitted(seq);
        Volatile.Write(ref _undo, null);
    }

    /// <summary>participant Abort：表帧尾截断回退到提交点 + 内存镜像逆序回退。</summary>
    public void AbortSession(long seq)
    {
        var list = Volatile.Read(ref _undo);
        if (list is not null)
        {
            lock (_lock)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var (key, previous) = list[i];
                    if (previous is { } prev) _entries[key] = prev;
                    else _entries.Remove(key);
                }
            }
            Volatile.Write(ref _undo, null);
        }
        _snapshot.Abort(seq);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 恢复重放（meta O(1) 水位 + 定步长逐帧重放）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 重放表记录链 [TruncatedAddress, WriteAddress)——meta 水位下记录恒完整（Prepare fsync 收口），
    /// 帧校验失败/步长不齐 = 结构损坏，fail-fast。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重放的记录数。</returns>
    public async ValueTask<long> ReplayAsync(CancellationToken ct)
    {
        var stride = RecordStride;
        var start = _snapshot.TruncatedAddress;
        var end = _snapshot.WriteAddress;
        long remaining = _snapshot.Distance(start, end);
        if (remaining == 0) return 0;
        if (remaining % stride != 0)
            throw new InvalidOperationException(
                $"对象表记录链长度 {remaining} 非步长 {stride} 整倍数——表引擎结构损坏（meta 水位下记录应全整）");

        var dataLen = (int)(stride - FrameHeaderSize - FrameFooterSize);
        var count = 0L;
        var cursor = start;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            await using var reader = _snapshot.OpenReadRange(cursor, _snapshot.AdvanceAddress(cursor, stride));
            var buf = new byte[dataLen];
            var totalRead = 0;
            int n;
            while (totalRead < dataLen
                   && (n = await reader.ReadDataAsync(buf.AsMemory(totalRead), ct).ConfigureAwait(false)) > 0)
            {
                totalRead += n;
            }
            if (totalRead != dataLen)
                throw new InvalidOperationException($"对象表记录 @ {cursor} 短读 {totalRead}/{dataLen}——表引擎结构损坏");
            if (!reader.IsFooterValid)
                throw new InvalidOperationException($"对象表记录 @ {cursor} 帧校验失败（CRC64）——表引擎结构损坏");

            ApplyInMemory(ParsePayload(buf));
            count++;
            cursor = _snapshot.AdvanceAddress(cursor, stride);
            remaining -= stride;
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 内部
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>会话域内挂 undo（返回非 null = 延迟提交域）。</summary>
    private List<(long Key, Entry? Previous)>? BeginMutation(long key, Entry? previous)
    {
        var undo = Volatile.Read(ref _undo);
        if (undo is not null)
        {
            lock (_lock) undo.Add((key, previous));
        }
        return undo;
    }

    private void CommitInternal()
    {
        _internalSeq++;
        _snapshot.Prepare(_internalSeq);        // fsync + 悬干水位（登记持久收口点）
        _snapshot.ConfirmCommitted(_internalSeq);
    }

    private void ApplyInMemory(in Entry entry)
    {
        lock (_lock) _entries[entry.ObjectId.Offset] = entry;
    }

    /// <summary>写一条记录帧（payload 补零至步长对齐——物理=逻辑不变式）。</summary>
    private async ValueTask AppendRecordAsync(Entry entry, CancellationToken ct)
    {
        var payload = RentPayloadBuffer();
        WritePayload(payload, entry);
        var writer = _snapshot.OpenWrite();
        try
        {
            await writer.WriteAsync(payload, ct).ConfigureAwait(false);
            await writer.CompleteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>同步对等（恢复对账路径）。</summary>
    private void AppendRecord(Entry entry)
    {
        var payload = RentPayloadBuffer();
        WritePayload(payload, entry);
        var writer = _snapshot.OpenWrite();
        try
        {
            writer.Write(payload);
            writer.Complete();
        }
        finally
        {
#pragma warning disable TCSG137 // 恢复对账同步登记：等帧闭环完成（结构层同步写路径同款惯例）
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore TCSG137
        }
    }

    private byte[] RentPayloadBuffer()
        => new byte[RecordStride - FrameHeaderSize - FrameFooterSize];

    /// <summary>payload 48B 写入（[BinaryLayout] 生成 Codec——布局声明见 <see cref="BlobObjectEntryPayload"/>）。</summary>
    private static void WritePayload(Span<byte> dst, in Entry e)
    {
        BlobObjectEntryPayloadCodec.Write(dst, new BlobObjectEntryPayload
        {
            ObjectIdSegId = e.ObjectId.SegId,
            ObjectIdExtension = e.ObjectId.Extension,
            ObjectIdOffset = e.ObjectId.Offset,
            Length = e.Length,
            FrameLength = e.FrameLength,
            CreatedTicks = e.CreatedTicks,
            StateAndReserved = (byte)e.State,
        });
    }

    /// <summary>payload 反解（生成 Codec 单点；状态值非法 = 结构损坏 fail-fast）。</summary>
    private static Entry ParsePayload(ReadOnlySpan<byte> payload)
    {
        var p = BlobObjectEntryPayloadCodec.Read(payload);
        var state = p.State;
        if (state is not (BlobState.Active or BlobState.Tombstone))
            throw new InvalidOperationException($"对象表记录状态非法 {state}——表引擎结构损坏");
        return new Entry(new LogicalAddress(p.ObjectIdSegId, p.ObjectIdExtension, p.ObjectIdOffset),
            p.Length, p.FrameLength, p.CreatedTicks, state);
    }
}
