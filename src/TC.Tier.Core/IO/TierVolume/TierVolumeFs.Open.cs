using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

public sealed partial class TierVolumeFs
{
    // ═══════════════ 施工入口（§3.6）═══════════════

    /// <summary>New（原 Format 终态改名）——在载体上创建空虚拟卷根空间（显式语义：已格式化载体抛 AlreadyExists）。</summary>
    public static TierVolumeFs New(TierVolumeCarrier carrier, TierVolumeFormatOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        options ??= new TierVolumeFormatOptions();
        options.Validate();

        ClaimInstance(carrier, null);
        var fs = new TierVolumeFs(carrier, new TierVolumeOpenOptions(), logger)
        {
            _pageSize = options.BlockSize,
            _preallocation = options.Preallocation,
            _carrierWriteThrough = options.CarrierWriteThrough,
            _parallelWrites = options.WriteConcurrency == WriteConcurrencyMode.Parallel,
        };
        try
        {
            fs.OpenCarrierHandle(writable: true, createIfMissing: !carrier.IsDevice);
            fs.ThrowIfAlreadyFormatted();   // 显式语义：已格式化载体拒（幂等由调用方组合）
            fs.FormatCore(options);
            SInstances[carrier.IdentityKey] = fs;
            SInstances[$"uuid:{fs._sb.Uuid}"] = fs;
            return fs;
        }
        catch
        {
            fs.ReleaseResources();
            throw;
        }
    }

    /// <summary>打开已格式化载体为根空间（唯一性检查在此——§2.4）。
    /// 快照挂载（V2 §1.1）：<see cref="TierVolumeOpenOptions.SnapshotName"/> 非空 → 只读冻结态视图
    /// （快照镜像 + 冻结位图；变异全拒；与活卷同载体并发安全——冻结块永不复用/打洞）。</summary>
    public static TierVolumeFs Open(TierVolumeCarrier carrier, TierVolumeOpenOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        options ??= new TierVolumeOpenOptions();
        if (options.SnapshotName is { } snapName)
            return OpenSnapshotMount(carrier, snapName, options, logger);
        return Open([carrier], options, logger);
    }

    /// <summary>多载体卷打开（RM-04 §3.8）：全量成员清单（成员 0 = 主载体），UUID/索引装配匹配。
    /// 降级打开（v2b）：options.AllowDegraded 时缺失成员以 null 占位（只读形态）。</summary>
    public static TierVolumeFs Open(TierVolumeCarrier?[] carriers, TierVolumeOpenOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(carriers);
        if (carriers.Length == 0) throw new ArgumentException("成员清单为空。", nameof(carriers));
        options ??= new TierVolumeOpenOptions();
        if (options.SnapshotName is not null)
            throw new FileIOException(IOError.Unsupported,
                "多载体清单与快照挂载不组合（快照挂载经单载体 Open——V2 §1.1）", null, "Open");

        if (options.Access == AccessMode.Write)
            throw new ArgumentException("虚拟卷无只写形态（AccessMode.Write）——G2：映射/虚拟介质值域受限（ro|rw）");
        var carrier = carriers[0] ?? throw new ArgumentException("主载体（成员 0）不可缺失。", nameof(carriers));
        ClaimInstance(carrier, null);
        var fs = new TierVolumeFs(carrier, options, logger);
        try
        {
            fs.OpenCarrierHandle(writable: options.Access != AccessMode.Read, createIfMissing: false);
            fs._pageSize = 0;   // DecodeWinner 自 superblock 探知
            var (winner, side) = fs.DecodeWinner();
            fs.AdoptWinner(winner);
            if (winner.Members.Count > 1 || carriers.Length > 1)
                fs.AssembleMembers(carriers, writable: options.Access != AccessMode.Read && !options.AllowDegraded,
                    allowDegraded: options.AllowDegraded);   // 多载体装配（身份校验 + 基块；降级 = 只读）
            fs.ContinueLoad(winner, side);

            // G1：Open label 校验（不符即抛 fail-fast——挂错卷的配置错误）
            if (options.Label is not null && options.Label != fs._sb.Label)
                throw new FileIOException(IOError.NotFound,
                    $"label 校验不符：期望 '{options.Label}'，卷上实际 '{fs._sb.Label}'（spec label 在 Open = 断言）。",
                    carrier.Path, "open-label-check");
            // G3：Open 收紧——有效上限 = min(quota, 供给)（§5.3；分配咽喉 AllocateBlocks 执法）。
            // 自动扩容卷例外：供给动态增长，quota 即界（min 规则随增长自然成立——quota ≤ 供给恒真）
            if (options.QuotaBytes > 0)
                fs._quotaCapBlocks = fs._autoExpand
                    ? (ulong)options.QuotaBytes / (ulong)fs._pageSize
                    : Math.Min((ulong)options.QuotaBytes / (ulong)fs._pageSize, fs._sb.CapacityBlocks);

            // UUID 双查（同 UUID 异载体 = 复制卷——一卷一实例同样拒绝）+ 正式登记
            var uuidKey = $"uuid:{fs._sb.Uuid}";
            if (SInstances.TryGetValue(uuidKey, out var existing) && !ReferenceEquals(existing, fs))
                throw new FileIOException(IOError.SharingViolation,
                    $"卷 UUID {fs._sb.Uuid} 已有活跃实例（复制卷？）——一卷一实例（§2.4）", null, "Open");
            foreach (var c in carriers)
                if (c is not null) SInstances[c.IdentityKey] = fs;
            SInstances[uuidKey] = fs;

            // 写意图打开 clean 卷：置 dirty（此后崩溃 → 恢复路径，§4.1；降级形态零写）
            if (options.Access != AccessMode.Read && !options.AllowDegraded && (fs._sb.Flags & FlagClean) != 0)
            {
                lock (fs.MetadataLock)
                {
                    fs._sb.Flags = (ushort)(fs._sb.Flags & ~FlagClean);
                    fs.RotateSuperblocks();
                }
            }
            return fs;
        }
        catch
        {
            fs.ReleaseResources();
            throw;
        }
    }

    private static void ClaimInstance(TierVolumeCarrier carrier, Guid? uuid)
    {
        if (SInstances.ContainsKey(carrier.IdentityKey))
            throw new FileIOException(IOError.SharingViolation,
                $"载体已有活跃实例：{carrier.Path}——一卷一实例（§2.4）", null, "Open");
        if (uuid is { } u && SInstances.ContainsKey($"uuid:{u}"))
            throw new FileIOException(IOError.SharingViolation,
                $"卷 UUID {u} 已有活跃实例——一卷一实例（§2.4）", null, "Open");
    }

    private void FormatCore(TierVolumeFormatOptions options)
    {
        long capacity;
        if (_carrier.IsDevice)
        {
            // 设备容量：BLKGETSIZE64 ioctl（块设备 fstat.st_size 恒 0——RM-05 loop 实测抓到的坑）
            capacity = QueryDeviceCapacityBytes(_members[0].Handle);
            if (capacity <= 0)
                throw new FileIOException(IOError.IOFailure, $"设备容量非法：{capacity}", _carrier.Path, "Format");
            var sector = QueryDeviceSectorSize(_members[0].Handle);
            if (sector > options.BlockSize)
                throw new ArgumentException(
                    $"块大小 {options.BlockSize} 小于设备逻辑扇区 {sector}（4Kn 设备须 ≥ 扇区——DIO 对齐基准）。");
            if (options.QuotaBytes > 0 && capacity > options.QuotaBytes)
                capacity = options.QuotaBytes;   // 供给 = min(设备, quota)（New = 供给时刻——物化进卷记录）
            capacity -= capacity % (options.BlockSize * BitmapAlignBlocks);   // 64 块对齐（位字不跨成员——RM-04）
        }
        else
        {
            if (options.QuotaBytes == -1)
            {
                // 自动扩容卷（medium-protocol §5.3）：初始小界 + 按需倍增——直到磁盘物理满（与 disk 的 -1 同形）
                capacity = AutoExpandInitialBytes;
                _autoExpand = true;
            }
            else
            {
                capacity = options.QuotaBytes;
                if (capacity <= 0)
                    throw new ArgumentException(
                        "QuotaBytes 非法：正数 = 供给；-1 = 自动扩容（文件载体——按需增长；设备载体 = 设备大小）。");
            }
            capacity -= capacity % (options.BlockSize * BitmapAlignBlocks);   // 64 块对齐（位字不跨成员——RM-04 §3.8）
            RandomAccess.SetLength(_members[0].Handle, capacity);    // 声明上限一次成形（NTFS 稀疏）
            // IS-02：full 档载体物理物化（非稀疏 SetLength 已即时分配 + SetFileValidData/fallocate/零写兜底）——
            // 创建时付成本换运行时零分配抖动；物理占位失败 fail-fast，不允许静默降级为稀疏。
            if (_preallocation == PreallocationMode.Full
                && !TC.Tier.Core.NativeInterop.FileNative.EnsurePhysicalAllocation(_members[0].Handle, capacity, _logger))
                throw new FileIOException(IOError.IOFailure,
                    $"载体物理占位失败（Preallocation=Full）：{_carrier.Path}——full 档不允许静默降级为稀疏",
                    _carrier.Path, "Format");
        }

        var bs = (long)options.BlockSize;
        var capacityBlocks = (ulong)(capacity / bs);
        var bitmapBytes = (capacityBlocks + 7) / 8;
        var bitmapBlocks = (bitmapBytes + (ulong)bs - 1) / (ulong)bs;
        var bitmapStart = (ulong)((HeaderBytes + bs - 1) / bs);

        _sb = new SuperblockData
        {
            Flags = (ushort)(_autoExpand ? FlagAutoExpand : 0),
            BlockSize = (uint)options.BlockSize,
            CapacityBlocks = capacityBlocks,
            BitmapStart = bitmapStart,
            BitmapBlocks = bitmapBlocks,
            Generation = 1,
            Uuid = Guid.NewGuid(),
            Label = options.Label ?? "",   // 基类 Label 缺省 null——superblock 空串即无标签
        };
        _sb.Members = [new MemberEntry(_sb.Uuid, capacityBlocks, bitmapStart, bitmapBlocks)];   // RM-04：成员 0 自登记
        _members[0].Info = _sb.Members[0];   // 路由就位（格式化路径无 AdoptWinner）
        _members[0].BaseBlock = 0;
        _bitmapWords = new ulong[(bitmapBytes + 7) / 8];
        _freeBlocks = capacityBlocks;
        for (var w = 0UL; w < (ulong)_bitmapWords.LongLength; w++) _dirtyBitmapWords.Add(w);   // 首次提交全量写（增量基线：设备可能有残留字节）

        // 保留区：头部 + 位图 + 日志物理保留（§3.9——对数据不可见）
        for (var b = 0UL; b < bitmapStart; b++) MarkBlocks(b, 1, true);
        MarkBlocks(bitmapStart, (uint)bitmapBlocks, true);
        var journalBytes = Math.Min(options.JournalReserveBytes, capacity / 8);   // 保留封顶容量 1/8（小卷自适）
        if (journalBytes > 0)
        {
            var jb = (uint)(journalBytes / bs);
            var jstart = AllocateBlocks(jb, "JournalReserve");
            for (var i = 0UL; i < jb; i++) _journalReserveBlocks.Add(jstart + i);
            // 日志启用（raw-journal §3.1）：字段随首次 superblock 轮写持久
            _sb.Flags |= FlagJournaled;
            _sb.JournalStart = jstart;
            _sb.JournalBlocks = jb;
            _sb.JournalGeneration = 1;
            _sb.JournalState = 1;
        }

        MetadataDirty = true;
        lock (MetadataLock)
        {
            CommitMetadata();       // 初始空镜像（代数 1）
            _sb.Flags |= FlagClean; // 格式化完成即 clean
            RotateSuperblocks();
        }
        MetadataDirty = false;
        JournalInitFromSuperblock();   // 日志运行态就位（格式化即 Journaled——默认启用）
    }

}
