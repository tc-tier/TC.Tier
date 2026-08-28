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
    // ═══════════════ 载体访问（实例内唯一通道——§2.4 无侧门）═══════════════

    /// <summary>打开单个成员载体（锁 + 句柄 + DIO + 身份）——Format/Open/AddCarrier 共用。
    /// 文件载体：缓冲主句柄（内核 writeback 吸收 + 电梯调度——实测本机 O_DIRECT 同步写地板仅 ~500MB/s、
    /// 缓冲档 900MB/s+，硬切 O_DIRECT 缓冲档跌至 80MB/s；OS 缓存驻留由 DONTNEED 流式纪律控制，
    /// 见 <see cref="DropCarrierCache"/>）。设备载体：O_DIRECT 强制（外部写者一致性——RM-05）。</summary>
    private CarrierMember OpenMemberCarrier(TierVolumeCarrier carrier, MemberEntry info, bool writable, bool createIfMissing,
        bool readOnlyNoLock = false)
    {
        FileStream? crossProcLock = null;
        SafeFileHandle handle;
        var direct = false;
        var ioAlign = 512;
        if (!carrier.IsDevice)
        {
            // 跨进程锁：伴生锁文件 FileShare.None（进程崩溃 OS 关闭自愈——与 DiskFileSystem 同机制）。
            // ★ V2 §1.1 快照挂载（readOnlyNoLock）：只读开口不取锁——与活卷写者并发由冻结纪律保证读面稳定。
            if (!readOnlyNoLock)
            {
                var lockPath = carrier.Path + ".lock";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(carrier.Path)!);
                    crossProcLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException ex)
                {
                    throw new FileIOException(IOError.SharingViolation,
                        $"跨进程卷锁获取失败（另一实例持有？）：{lockPath}——{ex.Message}", null, "Open");
                }
            }
            // FileShare.ReadWrite：MMF 直映射需第二次写打开——跨进程互斥由 .lock 锁文件承担（§2.4）
            // 锁文件已持有——主句柄失败必须释放锁（局部变量无人接手 = 泄漏到 GC；OpenOrCreate 的
            // Open→New 回退路径首次踩中：New 再取锁 FileShare.None 冲突）
            try
            {
                // IS-03：载体写穿档——FILE_FLAG_WRITE_THROUGH（写完成即达盘；Linux .NET 映射 O_SYNC）
                handle = File.OpenHandle(carrier.Path,
                    createIfMissing ? FileMode.OpenOrCreate : FileMode.Open,
                    writable ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite,
                    FileOptions.Asynchronous | (_carrierWriteThrough ? FileOptions.WriteThrough : 0));
            }
            catch
            {
                crossProcLock?.Dispose();   // 快照挂载（readOnlyNoLock）无锁文件——null 容忍
                throw;
            }
            // RM-41：Windows 文件载体稀疏标记（FSCTL_SET_SPARSE）——SetLength 元数据化。
            // 非稀疏 NTFS 文件 SetEndOfFile 扩展 = 即时簇分配（大额 quota 物化/自动扩容 = 一次性巨额
            // 分配——TierWAL 256MB 段预分配 11.5s 巨刺的载体侧根因）；稀疏后 SetLength 仅元数据、
            // 簇随写按需窗口分配（ext4 原生同构——载体增长式分配，按需窗口化）。已稀疏/非 Windows =
            // no-op；失败回退非稀疏（语义零差异：NTFS 稀疏洞读零 + TierVolume 层 B1 零基纪律双保险）。
            // IS-02：Preallocation=Full 跳过标记——非稀疏 SetLength 即时物化（full 档创建时付成本）。
            if (OperatingSystem.IsWindows() && _preallocation == PreallocationMode.Metadata)
            {
                try
                {
                    if (!TC.Tier.Core.NativeInterop.Kernel32.SetSparse(handle))
                        _logger?.LogWarning("载体稀疏标记失败（FSCTL_SET_SPARSE）：{Path}——回退非稀疏（SetLength 即时分配）", carrier.Path);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "载体稀疏标记异常：{Path}——回退非稀疏", carrier.Path);
                }
            }
        }
        else
        {
            if (OperatingSystem.IsWindows())
            {

            // ★ Windows 裸设备载体（补齐——用户拍板 2026-08-26：卷 \\.\X: + 物理盘 \\.\PhysicalDriveN 双形态）：
            //   排他 = CreateFile share=0 独占 + FSCTL_LOCK_VOLUME（卷锁定防挂载层写——用户拍板"独占+锁卷"；
            //   物理盘无卷锁语义——独占句柄已足够）；NO_BUFFERING = 直 IO（对齐要求——结构层 DioAlignment
            //   弹跳窗口适配，与 Linux O_DIRECT 同构）；WRITE_THROUGH = 载体写穿档（IS-03 的 Windows 化身）。
            //   语法：virtual:///dev/C: → \\.\C:（卷）；virtual:///dev/PhysicalDrive1 → \\.\PhysicalDrive1（物理盘）。
            var winPath = ToWindowsDevicePath(carrier.Path);
            var winFlags = NativeConstants.FileFlagNoBuffering
                           | (_carrierWriteThrough ? NativeConstants.FileFlagWriteThrough : 0);
            // ★ 快照挂载（readOnlyNoLock）：共享读开口 + 不锁卷（与活卷写者并发由冻结纪律保证读面稳定——Linux 同构）
            var share = readOnlyNoLock
                ? NativeConstants.FileShareRead | NativeConstants.FileShareWrite
                : 0;
            handle = Kernel32.CreateFile(winPath,
                writable ? NativeConstants.GenericRead | NativeConstants.GenericWrite : NativeConstants.GenericRead,
                share, IntPtr.Zero, NativeConstants.OpenExisting, winFlags, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new FileIOException(IOError.NotFound,
                    $"Windows 设备打开失败：{winPath}（错误码 {Marshal.GetLastPInvokeError()}——" +
                    "裸设备须管理员权限且卷/盘已存在；卷被挂载占用时先手动卸载）", carrier.Path, "Open");
            if (!readOnlyNoLock && IsVolumePath(winPath))
            {
                // ★ 卷锁定（独占排他——防挂载层写入；失败 = 卷被系统占用——诚实报错，不自动卸卷（危险））
                if (!Kernel32.DeviceIoControlSimple(handle, NativeConstants.FsctlLockVolume,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new FileIOException(IOError.SharingViolation,
                        $"卷锁定失败：{winPath}（错误码 {Marshal.GetLastPInvokeError()}——卷被系统占用，先手动卸载再打开）",
                        carrier.Path, "Open");
            }
            // 扇区大小（IoAlign——NO_BUFFERING 对齐要求；卷 = GetDiskFreeSpace 适配，物理盘失败默认 512）
            ioAlign = (int)QueryWindowsSectorSize(winPath);
            direct = true;
            return new CarrierMember { Carrier = carrier, Info = info, Handle = handle, CrossProcLock = crossProcLock, Direct = direct, IoAlign = ioAlign };
            }
            // 设备载体：原生 open(2)（绕过 .NET 8 Unix 共享层的 flock 感知）。排他判定归 flock(LOCK_EX|LOCK_NB)。
            // O_DIRECT（RM-05 DIO 纪律）：设备强制——内核缓存外部写者一致性；未对齐访问经对齐窗口弹跳。
            // IS-03：载体写穿档——O_SYNC（写完成即达设备）。
            var flags = (writable ? NativeConstants.ORdwr : 0)
                       | (OperatingSystem.IsLinux() ? NativeConstants.ODirect : 0)
                       | (_carrierWriteThrough ? NativeConstants.OSync : 0);
            var fd = LibC.Open(carrier.Path, flags, 0);
            if (fd < 0)
                throw new FileIOException(IOError.NotFound,
                    $"设备打开失败：{carrier.Path}（errno={Marshal.GetLastPInvokeError()}）",
                    carrier.Path, "Open");
            handle = LibC.WrapFileDescriptor(fd);
            if (OperatingSystem.IsLinux())
            {
                if (readOnlyNoLock)
                {
                    // ★ V2 §1.1 快照挂载：只读开口不 flock（与活卷写者并发由冻结纪律保证读面稳定）
                    direct = true;
                    return new CarrierMember { Carrier = carrier, Info = info, Handle = handle, CrossProcLock = crossProcLock, Direct = direct, IoAlign = ioAlign };
                }
                var borrowed = false;
                try
                {
                    handle.DangerousAddRef(ref borrowed);
                    // flock 排他（★返回值必须检查——RM-05 实测：丢弃 = 跨进程第二实例静默通过）
                    if (LibC.Flock(handle.DangerousGetHandle().ToInt32(), LibC.LockEx | LibC.LockNb) != 0)
                        throw new FileIOException(IOError.SharingViolation,
                            $"设备已被另一实例持有（flock）：{carrier.Path}——一卷一实例（§2.4）",
                            carrier.Path, "Open");
                    direct = true;
                }
                finally
                {
                    if (borrowed) handle.DangerousRelease();
                }
            }
        }
        return new CarrierMember { Carrier = carrier, Info = info, Handle = handle, CrossProcLock = crossProcLock, Direct = direct, IoAlign = ioAlign };
    }

    /// <summary>
    /// 语法映射："/dev/C:" → "\\.\C:"（卷）；"/dev/PhysicalDrive1" → "\\.\PhysicalDrive1"（物理盘）。
    /// Windows 裸设备载体路径（补齐 2026-08-26——Linux /dev/ 路径的 Windows 化身）。
    /// </summary>
    internal static string ToWindowsDevicePath(string carrierPath)
    {
        if (carrierPath.StartsWith("/dev/", StringComparison.Ordinal))
            return @"\\.\" + carrierPath["/dev/".Length..];
        return carrierPath;
    }

    /// <summary>卷形态判定（\\.\C:——盘符形态；物理盘 PhysicalDriveN 无卷锁语义）。</summary>
    internal static bool IsVolumePath(string winPath)
        => winPath.Length == 6 && winPath.StartsWith(@"\\.\", StringComparison.Ordinal)
           && char.IsLetter(winPath[4]) && winPath[5] == ':';

    /// <summary>Windows 设备扇区大小（NO_BUFFERING 对齐要求；卷 = GetDiskFreeSpace 适配；失败默认 512）。</summary>
    private static uint QueryWindowsSectorSize(string winPath)
    {
        if (Kernel32.GetDiskFreeSpace(winPath, out _, out uint bytesPerSector, out _, out _) && bytesPerSector > 0)
            return bytesPerSector;
        return 512;   // ★ 4K 扇区物理盘（PhysicalDriveN——GetDiskFreeSpace 不适用）默认 512——IO 失败时错误传播（对齐弹跳基于 IoAlign）
    }

    private void OpenCarrierHandle(bool writable, bool createIfMissing, bool readOnlyNoLock = false)
    {
        // 单载体主成员（多载体成员经 Open(carriers[]) 装配路径补开）
        var placeholder = new MemberEntry(Guid.Empty, 0, 0, 0);   // 占位（sb 未立——AdoptWinner/FormatCore 即补全；cc9bb2e0 曾误引 _sb.Uuid 使 New/Open 全 NRE）
        var m = OpenMemberCarrier(_carrier, placeholder, writable, createIfMissing, readOnlyNoLock);
        _members = [m];
        RefreshCarrierDio();
    }

    /// <summary>全成员 O_DIRECT 判据刷新（写绕条件化——成员装配/加卸载后调用）。</summary>
    private void RefreshCarrierDio()
        => _carrierDio = _members.Length > 0 && _members.All(m => m.Direct || m.IsMissing);

    /// <summary>多载体装配（RM-04 §3.8）：按成员表顺序补开其余载体 + RAWC 身份校验 + 基块推导。
    /// 载体清单由调用方供给（LVM 同构——路径不入盘上格式）；身份以 UUID/索引匹配，不匹配拒开。
    /// 降级运行（v2b）：AllowDegraded 且清单含 null 占位 → 幽灵成员（只读 + 数据面拒读）。</summary>
    private void AssembleMembers(TierVolumeCarrier?[] carriers, bool writable, bool allowDegraded)
    {
        if (_sb.Members.Count != carriers.Length)
            throw new FileIOException(IOError.NotFound,
                $"成员载体数不符：卷声明 {_sb.Members.Count}，供给 {carriers.Length}（含主载体须全量提交；降级打开用 null 占缺失成员）",
                _carrier.Path, "Open");
        var list = new List<CarrierMember>(_sb.Members.Count) { _members[0] };
        list[0].Info = _sb.Members[0];
        ulong total = 0;
        var missing = 0;
        for (var i = 0; i < _sb.Members.Count; i++)
        {
            var info = _sb.Members[i];
            if (i > 0)
            {
                if (carriers[i] is null)
                {
                    if (!allowDegraded)
                        throw new FileIOException(IOError.NotFound,
                            $"成员 {i} 缺失（null 占位须 AllowDegraded）——§3.8 成员缺失即拒开", _carrier.Path, "Open");
                    missing++;
                    list.Add(new CarrierMember
                    {
                        Carrier = $"<missing:{i}>",
                        Info = info,
                        BaseBlock = 0,
                        IsMissing = true,
                    });
                }
                else
                {
                    ClaimInstance(carriers[i]!, null);
                    var m = OpenMemberCarrier(carriers[i]!, info, writable, createIfMissing: false);
                    VerifyMemberHeader(m, i);
                    list.Add(m);
                }
            }
            list[i].BaseBlock = total;
            total += info.CapacityBlocks;
        }
        _members = list.ToArray();
        RefreshCarrierDio();
        if (missing <= 0) return;
        _degraded = true;   // 数据面路由拒读缺失成员 + 全部变异拒绝（v2b 降级形态）
        _logger?.LogWarning("降级打开：{Count} 个成员缺失（只读形态——缺失成员数据读将失败）", missing);
    }

    /// <summary>成员载体级身份头（"RAWC" 512B：magic|ver|卷UUID|成员索引|bitmapStart|bitmapBlocks|capacity|CRC）。
    /// 写于 AddCarrier（不可变）；Open 装配时校验——UUID/索引不匹配拒开（§3.8）。</summary>
    private static void EncodeMemberHeader(Span<byte> buffer, MemberEntry info, Guid volumeUuid, int carrierIndex, int pageSize)
    {
        buffer.Clear();
        "RAWC"u8.CopyTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4), TierVolumeLayoutVersion);
        volumeUuid.TryWriteBytes(buffer.Slice(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(24), (uint)carrierIndex);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(28), info.BitmapStartLocal);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(36), info.BitmapBlocksLocal);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(44), info.CapacityBlocks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(508),
            System.IO.Hashing.Crc32.HashToUInt32(buffer.Slice(0, 508)));
    }

    private void VerifyMemberHeader(CarrierMember m, int expectedIndex)
    {
        var header = new byte[512];
        ReadMemberLocal(m, 0, header);
        if (!header.AsSpan(0, 4).SequenceEqual("RAWC"u8)
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) != (uint)expectedIndex
            || new Guid(header.AsSpan(8, 16).ToArray()) != _sb.Uuid
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(508))
               != System.IO.Hashing.Crc32.HashToUInt32(header.AsSpan(0, 508)))
            throw new FileIOException(IOError.IOFailure,
                $"成员载体身份不符（UUID/索引/CRC）：{m.Carrier.Path}——期望成员 {expectedIndex}", m.Carrier.Path, "Open");
    }

    /// <summary>成员本地读（绕过全局路由——成员装配/头写入用；512 对齐经成员自己的 DIO 纪律）。</summary>
    private unsafe void ReadMemberLocal(CarrierMember m, long localOffset, Span<byte> dest)
    {
        if (!m.Direct)
        {
            var got = RandomAccess.Read(m.Handle, dest, localOffset);
            if (got != dest.Length)
                throw new FileIOException(IOError.IOFailure, $"成员短读：{m.Carrier.Path} @{localOffset}", m.Carrier.Path, "Open");
            return;
        }
        var align = m.IoAlign;
        var buf = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            var done = 0;
            while (done < dest.Length)
            {
                var take = Math.Min(align, dest.Length - done);
                // 读满对齐窗（4Kn 逻辑块要求整窗长度——512B 头读也按整窗取）
                var got = RandomAccess.Read(m.Handle, new Span<byte>(buf, align), localOffset + done);
                if (got < take)
                    throw new FileIOException(IOError.IOFailure, $"成员短读：{m.Carrier.Path} @{localOffset + done}", m.Carrier.Path, "Open");
                new ReadOnlySpan<byte>(buf, take).CopyTo(dest.Slice(done, take));
                done += take;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    /// <summary>成员本地写（512 对齐窗口——AddCarrier 头/位图初始化用）。</summary>
    private unsafe void WriteMemberLocal(CarrierMember m, long localOffset, ReadOnlySpan<byte> src)
    {
        var align = m.IoAlign;
        var buf = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            var done = 0;
            while (done < src.Length)
            {
                var take = Math.Min(align, src.Length - done);
                new Span<byte>(buf, align).Clear();
                src.Slice(done, take).CopyTo(new Span<byte>(buf, take));
                RandomAccess.Write(m.Handle, new Span<byte>(buf, align), localOffset + done);
                done += take;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    private unsafe byte[] ReadCarrier(long offset, int length)
    {
        var buf = new byte[length];
        ReadCarrierExactly(offset, buf);
        return buf;
    }

    /// <summary>设备容量（字节）——Linux BLKGETSIZE64 ioctl；非 Linux/失败回退 fstat 长度。</summary>
    private unsafe long QueryDeviceCapacityBytes(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            var borrowed = false;
            try
            {
                handle.DangerousAddRef(ref borrowed);
                ulong size = 0;
                if (TC.Tier.Core.NativeInterop.LibC.Ioctl(handle.DangerousGetHandle().ToInt32(),
                        TC.Tier.Core.NativeInterop.LibC.BlkGetSize64, &size) == 0 && size > 0)
                    return (long)size;
            }
            finally
            {
                if (borrowed) handle.DangerousRelease();
            }
        }
        return RandomAccess.GetLength(handle);
    }

    /// <summary>设备逻辑扇区大小（字节）——BLKSSZGET；失败回退 512（DIO 对齐安全侧）。</summary>
    private unsafe int QueryDeviceSectorSize(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            var borrowed = false;
            try
            {
                handle.DangerousAddRef(ref borrowed);
                int sector = 0;
                if (LibC.Ioctl(handle.DangerousGetHandle().ToInt32(),
                        LibC.BlkSszGet, &sector) == 0 && sector > 0)
                    return sector;
            }
            finally
            {
                if (borrowed) handle.DangerousRelease();
            }
        }
        return 512;
    }


    /// <summary>全局字节偏移 → 成员路由（线性拼接：基块 = Σ前序容量）。返回 (成员, 成员内本地偏移, 本段可用字节)。
    /// 成员信息未采纳前（sb 解码阶段——Info 为占位）直通主成员（offset 即本地偏移）。
    /// 降级运行（v2b）：缺失成员数据面访问诚实拒绝（洞数据不可伪造）。</summary>
    private (CarrierMember Member, long LocalOffset, int Segment) Route(long globalOffset, int remaining)
    {
        if (_members.Length == 1 && _members[0].Info.CapacityBlocks == 0)
            return (_members[0], globalOffset, remaining);
        foreach (var m in _members)
        {
            var memberBytes = (long)m.Info.CapacityBlocks * _pageSize;
            if (globalOffset < memberBytes)
            {
                if (m.IsMissing)
                    throw new FileIOException(IOError.IOFailure,
                        $"数据块位于缺失成员（降级运行）：全局偏移 {globalOffset} / 成员 {m.Carrier.Path}——数据不可用（诚实拒绝，v2b）",
                        _carrier.Path, "Read");
                return (m, globalOffset, (int)Math.Min(remaining, memberBytes - globalOffset));
            }
            globalOffset -= memberBytes;
        }
        throw new FileIOException(IOError.IOFailure,
            $"载体访问越界（超出卷容量）：offset={globalOffset}", _carrier.Path, "IO");
    }

    /// <summary>成员 O_DIRECT 读句柄（RM-28）：设备 = 主句柄（本就 O_DIRECT）；文件 = 懒开专用
    /// O_RDONLY|O_DIRECT 只读句柄 + 失败记忆（文件系统不支持时回退缓冲读 + DONTNEED 纪律）。
    /// 读侧专用不破坏一卷一实例（跨进程互斥由锁文件/flock 承担——此句柄只读不出实例）。
    /// 读侧无 RM-34 写侧灾难（O_DIRECT 同步小写每写一次设备往返——那是写路径否决依据）；
    /// 大粒度顺序读恰是 DIO 甜点（台账读粒度曲线：64KB+ 均 9.6GB/s+）。
    /// 与缓冲写在同范围交错由内核 DIO 读纪律保障（先回写重叠脏区再读盘——generic_file_direct_read 同款）。</summary>
    private static SafeFileHandle? GetDioReadHandle(CarrierMember m)
    {
        if (m.IsMissing) return null;
        if (m.Direct) return m.Handle;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return null;   // RM-36：Windows = NO_BUFFERING 通道
        lock (m)
        {
            if (m.DioReadState == 1) return m.DioReadHandle;
            if (m.DioReadState == 2) return null;
            if (OperatingSystem.IsWindows())
            {
                // RM-36：FILE_FLAG_NO_BUFFERING（0x20000000——FileOptions 位域直传 CreateFile）
                // 只读句柄；扇区对齐纪律与 Linux 同道（弹跳窗 4096 对齐 ≥ 512e/4Kn）
                try
                {
                    m.DioReadHandle = File.OpenHandle(m.Carrier.Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, (FileOptions)0x20000000 | FileOptions.Asynchronous);
                    m.DioReadState = 1;
                }
                catch (IOException)
                {
                    m.DioReadState = 2;   // 失败记忆——回退缓冲读
                }
                return m.DioReadHandle;
            }
            const int oRdOnly = 0x0;   // O_RDONLY（FileNative 同款本地常量先例）
            var fd = LibC.Open(m.Carrier.Path,
                oRdOnly | NativeConstants.ODirect, 0);
            if (fd < 0)
            {
                m.DioReadState = 2;   // EINVAL/ENOTSUP 等——失败记忆，此后走缓冲 + DONTNEED 回退
                return null;
            }
            m.DioReadHandle = LibC.WrapFileDescriptor(fd);
            m.DioReadState = 1;
            return m.DioReadHandle;
        }
    }

    /// <summary>直达档载体读（RM-28 + RM-36）：优先 DIO 读通道——Linux = O_RDONLY|O_DIRECT 专用句柄 /
    /// Windows = FILE_FLAG_NO_BUFFERING 只读句柄 / 设备 = 主句柄（本就 O_DIRECT）。
    /// 弹跳窗三重对齐（偏移/长度/缓冲地址）；单段覆盖全剩余且三重对齐时零拷贝直读。
    /// 任一成员 DIO 不可用返回 false——调用方回退缓冲读（Linux 附 DONTNEED 纪律；重读幂等无害）。</summary>
    private unsafe bool TryReadCarrierDio(long offset, Span<byte> destination)
    {
        if (destination.Length == 0) return true;
        var done = 0;
        while (done < destination.Length)
        {
            var (m, localBase, segLen) = Route(offset + done, destination.Length - done);
            var h = GetDioReadHandle(m);
            if (h is null) return false;
            var align = m.Direct ? m.IoAlign : 4096;   // 文件 O_DIRECT：文件系统逻辑块对齐（4K 安全侧）
            var slice = destination.Slice(done, segLen);
            // 零拷贝快道：段覆盖全剩余 + 缓冲地址/偏移/长度全对齐
            var ptr = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref MemoryMarshal.GetReference(slice));
            if (segLen == destination.Length - done
                && ptr % align == 0 && localBase % align == 0 && slice.Length % align == 0)
            {
                var got = RandomAccess.Read(h, slice, localBase);
                if (got != slice.Length)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读（DIO 直读）：offset={offset}+{done}, len={slice.Length} 实得 {got}", m.Carrier.Path, "Read");
                return true;
            }
            // 弹跳窗（对齐窗整读 + 局部拷贝——窗口上限 1MB 同 ReadCarrierExactly 纪律）
            const int chunkAlign = 1 << 20;
            var windowStart = localBase / align * align;
            var windowEnd = Math.Min(
                (localBase + slice.Length + align - 1) / align * align,
                Math.Min(windowStart + chunkAlign, (localBase / align + segLen / align + 1) * align));
            var windowLen = (int)(windowEnd - windowStart);
            var window = (byte*)NativeMemory.AlignedAlloc((nuint)windowLen, 4096);
            try
            {
                var got = RandomAccess.Read(h, new Span<byte>(window, windowLen), windowStart);
                var inOffset = (int)(localBase - windowStart);
                var need = Math.Min(slice.Length, windowLen - inOffset);
                if (got < inOffset + need)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读（DIO 窗口）：offset={offset}+{done}（窗口 {windowStart}+{windowLen} 实得 {got}）",
                        m.Carrier.Path, "Read");
                new ReadOnlySpan<byte>(window + inOffset, need).CopyTo(slice.Slice(0, need));
                done += need;
            }
            finally
            {
                NativeMemory.AlignedFree(window);
            }
        }
        return true;
    }

    private unsafe void ReadCarrierExactly(long offset, Span<byte> destination)
    {
        // 成员分段 + 各自 DIO 纪律（三重对齐：偏移/长度/缓冲地址——RM-05）
        const int chunkAlign = 1 << 20;   // 窗口上限 1MB（O_DIRECT 大段读：4KB 窗口 = 每 4KB 一次 alloc+syscall）
        var done = 0;
        while (done < destination.Length)
        {
            var logicalStart = offset + done;
            var (m, localBase, segLen) = Route(logicalStart, destination.Length - done);
            if (!m.Direct)
            {
                var got = RandomAccess.Read(m.Handle, destination.Slice(done, segLen), localBase);
                if (got != segLen)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读：offset={offset}+{done}, len={segLen} 实得 {got}", m.Carrier.Path, "Read");
                done += segLen;
                continue;
            }
            var align = m.IoAlign;
            var windowStart = localBase / align * align;
            var windowEnd = Math.Min(
                (localBase + (destination.Length - done) + align - 1) / align * align,
                Math.Min(windowStart + chunkAlign, (localBase / align + segLen / align + 1) * align));
            var windowLen = (int)(windowEnd - windowStart);
            var window = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)windowLen, 4096);
            try
            {
                var got = RandomAccess.Read(m.Handle, new Span<byte>(window, windowLen), windowStart);
                var inOffset = (int)(localBase - windowStart);
                var need = Math.Min(destination.Length - done, windowLen - inOffset);
                if (got < inOffset + need)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读：offset={offset}+{done}（窗口 {windowStart}+{windowLen} 实得 {got}）", m.Carrier.Path, "Read");
                new ReadOnlySpan<byte>(window + inOffset, need).CopyTo(destination.Slice(done, need));
                done += need;
            }
            finally
            {
                NativeMemory.AlignedFree(window);
            }
        }
    }

    private unsafe void WriteCarrier(long offset, ReadOnlySpan<byte> source)
    {
        // 成员分段 + 各自 DIO 纪律（对齐窗口 RMW——数据面已按块对齐，免 RMW 热路径）。
        // 写窗口 64KB（实测 O_DIRECT 写成本曲线：64KB=189μs 甜点，256KB=3.3ms/1MB=12ms 灾难段）
        const int chunkAlign = 64 << 10;
        var done = 0;
        while (done < source.Length)
        {
            var logicalStart = offset + done;
            var (m, localBase, segLen) = Route(logicalStart, source.Length - done);
            if (!m.Direct)
            {
                RandomAccess.Write(m.Handle, source.Slice(done, segLen), localBase);
                done += segLen;
                continue;
            }
            var align = m.IoAlign;
            var windowStart = localBase / align * align;
            var windowEnd = Math.Min(
                (localBase + (source.Length - done) + align - 1) / align * align,
                Math.Min(windowStart + chunkAlign, windowStart / align * align + segLen + align));
            var windowLen = (int)(windowEnd - windowStart);
            var window = (byte*)NativeMemory.AlignedAlloc((nuint)windowLen, 4096);
            try
            {
                var wspan = new Span<byte>(window, windowLen);
                if (windowStart != localBase)
                {
                    var got = RandomAccess.Read(m.Handle, wspan, windowStart);
                    if (got < windowLen) wspan.Slice(got).Clear();
                }
                var inOffset = (int)(localBase - windowStart);
                var patch = Math.Min(source.Length - done, windowLen - inOffset);
                source.Slice(done, patch).CopyTo(wspan.Slice(inOffset, patch));
                RandomAccess.Write(m.Handle, wspan, windowStart);
                done += patch;
            }
            finally
            {
                NativeMemory.AlignedFree(window);
            }
        }
        if (source.Length > 0)
            Interlocked.Add(ref _carrierWritePendingBytes, source.Length);   // RM-40：在途载体写记账（写绕/直达/零基/排干——写完成即计数，屏障方归零）
    }

    internal void FlushCarrier()
    {
        foreach (var m in _members)
            RandomAccess.FlushToDisk(m.Handle);
        Interlocked.Exchange(ref _carrierWritePendingBytes, 0);   // RM-40：全成员屏障后归零（fsync 覆盖此前全部载体写——计数清零与屏障成对）
    }

    /// <summary>提交屏障：journal 记录落区后的一次持久化屏障。
    /// 载体写穿档（IS-03——FILE_FLAG_WRITE_THROUGH/O_SYNC）：句柄写完成即达稳定存储（数据页先于
    /// journal 发出且各自写穿完成），journal 写穿完成即单屏障——免独立 fsync（"写数据 + journal + fsync"
    /// 三段压成一次写穿）；记账归零保留（在途语义保持）。非写穿档 = 全成员 fsync。
    /// ★ MMF 直映射写绕句柄（TierVolumeMappedSection.Flush 经 msync）仍走 <see cref="FlushCarrier"/> 全 fsync——
    /// 不随档短路（映射写不经 WT 句柄，屏障不可省）。</summary>
    private void JournalBarrier()
    {
        if (_carrierWriteThrough)
        {
            Interlocked.Exchange(ref _carrierWritePendingBytes, 0);
            return;
        }
        FlushCarrier();
    }

    /// <summary>
    /// DONTNEED 扫描纪律（性能轮结论）：文件载体走缓冲 IO（内核 writeback 吸收是缓冲档
    /// 平权 Disk 的存在条件——实测本机 O_DIRECT 同步写地板仅 ~500MB/s，且 fadvise(DONTNEED) 对脏页
    /// 触发立即写回，写路径任何"弃页"手段都等价于把 O_DIRECT 的每写一次设备往返请回来）。
    /// 因此只对<b>直达档读</b>（干净页——弃之无写回代价）施加：扫描不驻留 OS 缓存。
    /// 设备载体（O_DIRECT）无 OS 缓存，no-op。尽力而为：失败静默（fadvise 是 advisory）。</summary>
    internal void DropCarrierCache(long offset, int length)
    {
        if (!OperatingSystem.IsLinux() || length <= 0) return;
        var done = 0;
        while (done < length)
        {
            var (m, localBase, segLen) = Route(offset + done, length - done);
            if (!m.Direct && !m.IsMissing)
            {
                try
                {
                    var borrowed = false;
                    m.Handle.DangerousAddRef(ref borrowed);
                    try
                    {
                        _ = LibC.PosixFadvise(m.Handle.DangerousGetHandle().ToInt32(),
                            localBase, segLen, LibC.PosixFadvDontNeed);   // CA1806：advisory 尽力——返回值即错误码，无处置动作
                    }
                    finally
                    {
                        if (borrowed) m.Handle.DangerousRelease();
                    }
                }
                catch { /* advisory 尽力 */ }
            }
            done += segLen;
        }
    }

}
