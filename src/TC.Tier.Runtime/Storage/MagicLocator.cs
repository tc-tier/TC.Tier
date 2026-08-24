namespace TC.Tier.Runtime.Storage;

/// <summary>
/// <see cref="IStorageEngine"/> 地址空间的魔术字方向性定位（不透明字节——零/非零全部是有效数据，
/// 无任何格式/布局假设）。
/// <para>★ 契约（使用方三输入，工具零假设）：只匹配传入的魔术值；方向性（需要最后=地址最大匹配点,
///   需要最小=地址最小匹配点）；<b>范围由使用方给</b>（格式知识剪小搜索域）；alignment 是扫描步进
///   速度提示——步进跳过的偏移不检查，使用方必须传自己格式的真实对齐（无对齐保证传 1）。</para>
/// <para>★ 两步定位协议：本类（引擎侧）按 magic 值扫描做<b>粗锚点</b>——返回精确匹配地址 + 所在页起点；
///   上层使用者（Log/Ring/Mirror 恢复）从锚点起结合自身格式<b>精确查找</b>记录边界（magic 只提名候选，
///   结构/CRC 才是裁决）。</para>
/// <para>★ 扩展方法形态（纯消费面算法——只吃 IStorageEngine 公开面，无引擎内部状态）：
///   <c>engine.Locate(...)</c> 或静态形态 <c>MagicLocator.Locate(engine, ...)</c>。</para>
/// </summary>
public static class MagicLocator
{
    /// <summary>
    /// ★ 方向性 magic 定位（同步）。在 [from, to) 半开范围内按方向找 magic 匹配点。
    /// <para>from/to 自动收敛到 [MinAddress, AllocatedTail]（引擎已分配域——范围外的区域不可能有有效数据）。</para>
    /// </summary>
    /// <param name="engine">目标引擎。</param>
    /// <param name="magics">使用方魔术值集合（可多值）。</param>
    /// <param name="direction">First=地址最小匹配点 / Last=地址最大匹配点。</param>
    /// <param name="from">搜索范围起点（含）。</param>
    /// <param name="to">搜索范围终点（不含）。</param>
    /// <param name="scanPageSize">扫描读页步进（2 的幂，建议 = 使用方 PageSize）。</param>
    /// <param name="magicAlignment">magic 对齐步进（使用方 record 对齐，2 的幂；无对齐保证传 1）。</param>
    /// <param name="strategy">Linear=保证正确逐页扫 / Monotone=页级二分（使用方断言含 magic 页单调）。</param>
    /// <returns>定位结果。空范围 / 无命中 → <see cref="MagicLocation.NotFound"/>。</returns>
    public static MagicLocation Locate(this IStorageEngine engine, ReadOnlySpan<uint> magics,
        MagicDirection direction, LogicalAddress from, LogicalAddress to,
        int scanPageSize, int magicAlignment, MagicLocateStrategy strategy = MagicLocateStrategy.Linear)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (magics.IsEmpty) throw new ArgumentException("magics 不能为空", nameof(magics));

        var bounds = ClampRange(engine, from, to);
        if (!bounds.HasValue) return MagicLocation.NotFound;
        (from, to) = bounds.Value;

        return strategy switch
        {
            MagicLocateStrategy.Monotone => LocateMonotone(engine, magics, direction, from, to, scanPageSize,
                magicAlignment),
            _ => LocateLinear(engine, magics, direction, from, to, scanPageSize, magicAlignment),
        };
    }

    /// <summary>
    /// ★ 方向性 magic 定位（异步）。在 [from, to) 半开范围内按方向找 magic 匹配点。
    /// </summary>
    /// <param name="engine">目标引擎。</param>
    /// <param name="magics">使用方魔术值集合（可多值）。</param>
    /// <param name="direction">First=地址最小匹配点 / Last=地址最大匹配点。</param>
    /// <param name="from">搜索范围起点（含）。</param>
    /// <param name="to">搜索范围终点（不含）。</param>
    /// <param name="scanPageSize">扫描读页步进（2 的幂，建议 = 使用方 PageSize）。</param>
    /// <param name="magicAlignment">magic 对齐步进（使用方 record 对齐，2 的幂；无对齐保证传 1）。</param>
    /// <param name="strategy">Linear=保证正确逐页扫 / Monotone=页级二分（使用方断言含 magic 页单调）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>定位结果。空范围 / 无命中 → <see cref="MagicLocation.NotFound"/>。</returns>
    /// <exception cref="ArgumentNullException">engine 不能为空。</exception>
    /// <exception cref="ArgumentNullException">magics 不能为空。</exception>
    /// <exception cref="ArgumentException">magics 不能为空。</exception>
    public static async ValueTask<MagicLocation> LocateAsync(this IStorageEngine engine, IReadOnlyList<uint> magics,
        MagicDirection direction, LogicalAddress from, LogicalAddress to,
        int scanPageSize, int magicAlignment, MagicLocateStrategy strategy = MagicLocateStrategy.Linear,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(magics);
        if (magics.Count == 0) throw new ArgumentException("magics 不能为空", nameof(magics));

        var bounds = ClampRange(engine, from, to);
        if (!bounds.HasValue) return MagicLocation.NotFound;
        (from, to) = bounds.Value;

        return strategy switch
        {
            MagicLocateStrategy.Monotone => await LocateMonotoneAsync(engine, magics, direction, from, to, scanPageSize,
                magicAlignment, ct).ConfigureAwait(false),
            _ => await LocateLinearAsync(engine, magics, direction, from, to, scanPageSize, magicAlignment, ct)
                .ConfigureAwait(false),
        };
    }

    /// <summary>范围收敛到引擎已分配域 [MinAddress, AllocatedTail]；空范围（from ≥ to）返回 null。</summary>
    private static (LogicalAddress From, LogicalAddress To)? ClampRange(IStorageEngine engine, LogicalAddress from,
        LogicalAddress to)
    {
        if (from.CompareTo(engine.MinAddress) < 0) from = engine.MinAddress;
        if (to.CompareTo(engine.AllocatedTail) > 0) to = engine.AllocatedTail;
        if (to.CompareTo(from) <= 0) return null;
        return (from, to);
    }

    // ════════════════════════════════════════════════════════════════
    // ★ 通用档（Linear）：逐页方向线性扫——零布局假设，恒正确
    // ════════════════════════════════════════════════════════════════

    private static MagicLocation LocateLinear(IStorageEngine engine, ReadOnlySpan<uint> magics,
        MagicDirection direction, LogicalAddress from, LogicalAddress to, int scanPageSize, int magicAlign)
    {
        long dist = engine.GetDistance(from, to);
        using var buf = new AlignedMemoryManager(scanPageSize, (int)engine.SectorSize);

        if (direction == MagicDirection.First)
        {
            for (long off = 0; off < dist; off += scanPageSize)
            {
                var page = engine.CalculationAddress(from, off);
                int len = (int)Math.Min(scanPageSize, dist - off);
                int got = engine.Read(page, buf.GetSpan(0, len));
                if (got > 0 && ScanSpanForward(buf.GetSpan(0, got), magics, magicAlign, out int hit))
                    return new MagicLocation(true, engine.CalculationAddress(page, hit), page);
            }
        }
        else
        {
            // Last：从最后一页往回——首个命中即全局最后一个
            for (long off = (dist - 1) / scanPageSize * scanPageSize; off >= 0; off -= scanPageSize)
            {
                var page = engine.CalculationAddress(from, off);
                int len = (int)Math.Min(scanPageSize, dist - off);
                int got = engine.Read(page, buf.GetSpan(0, len));
                if (got > 0 && ScanSpanReverse(buf.GetSpan(0, got), magics, magicAlign, out int hit))
                    return new MagicLocation(true, engine.CalculationAddress(page, hit), page);
            }
        }

        return MagicLocation.NotFound;
    }

    private static async ValueTask<MagicLocation> LocateLinearAsync(IStorageEngine engine, IReadOnlyList<uint> magics,
        MagicDirection direction, LogicalAddress from, LogicalAddress to, int scanPageSize, int magicAlign,
        CancellationToken ct)
    {
        long dist = engine.GetDistance(from, to);
        using var buf = new AlignedMemoryManager(scanPageSize, (int)engine.SectorSize);

        if (direction == MagicDirection.First)
        {
            for (long off = 0; off < dist; off += scanPageSize)
            {
                ct.ThrowIfCancellationRequested();
                var page = engine.CalculationAddress(from, off);
                int len = (int)Math.Min(scanPageSize, dist - off);
                int got = await engine.ReadAsync(page, buf.Memory[..len], ct).ConfigureAwait(false);
                if (got > 0 && ScanSpanForward(buf.GetSpan(0, got), magics, magicAlign, out int hit))
                    return new MagicLocation(true, engine.CalculationAddress(page, hit), page);
            }
        }
        else
        {
            for (long off = (dist - 1) / scanPageSize * scanPageSize; off >= 0; off -= scanPageSize)
            {
                ct.ThrowIfCancellationRequested();
                var page = engine.CalculationAddress(from, off);
                int len = (int)Math.Min(scanPageSize, dist - off);
                int got = await engine.ReadAsync(page, buf.Memory[..len], ct).ConfigureAwait(false);
                if (got > 0 && ScanSpanReverse(buf.GetSpan(0, got), magics, magicAlign, out int hit))
                    return new MagicLocation(true, engine.CalculationAddress(page, hit), page);
            }
        }

        return MagicLocation.NotFound;
    }

    // ════════════════════════════════════════════════════════════════
    // ★ 快速档（Monotone）：页级二分 + 页内方向扫——前置条件归使用方断言
    // ════════════════════════════════════════════════════════════════

    private static MagicLocation LocateMonotone(IStorageEngine engine, ReadOnlySpan<uint> magics,
        MagicDirection direction, LogicalAddress from, LogicalAddress to, int scanPageSize, int magicAlign)
    {
        using var buf = new AlignedMemoryManager(scanPageSize, (int)engine.SectorSize);
        if (direction == MagicDirection.Last)
        {
            var (found, pageAddr) = BinarySearchPage(magics, false, engine, from, to, scanPageSize, magicAlign, buf);
            if (!found) return MagicLocation.NotFound;
            var (hitFound, hitAddr) = ScanPageReverse(engine, magics, scanPageSize, magicAlign, pageAddr, buf);
            return hitFound ? new MagicLocation(true, hitAddr, pageAddr) : MagicLocation.NotFound;
        }
        else
        {
            var (found, pageAddr) = BinarySearchPage(magics, true, engine, from, to, scanPageSize, magicAlign, buf);
            if (!found) return MagicLocation.NotFound;
            var (hitFound, hitAddr) = ScanPageForward(engine, magics, scanPageSize, magicAlign, pageAddr, buf);
            return hitFound ? new MagicLocation(true, hitAddr, pageAddr) : MagicLocation.NotFound;
        }
    }

    private static async ValueTask<MagicLocation> LocateMonotoneAsync(IStorageEngine engine, IReadOnlyList<uint> magics,
        MagicDirection direction, LogicalAddress from, LogicalAddress to, int scanPageSize, int magicAlign,
        CancellationToken ct)
    {
        using var buf = new AlignedMemoryManager(scanPageSize, (int)engine.SectorSize);
        if (direction == MagicDirection.Last)
        {
            var (found, pageAddr) =
                await BinarySearchPageAsync(magics, false, engine, from, to, scanPageSize, magicAlign, buf, ct)
                    .ConfigureAwait(false);
            if (!found) return MagicLocation.NotFound;
            var (hitFound, hitAddr) =
                await ScanPageReverseAsync(engine, magics, scanPageSize, magicAlign, pageAddr, buf, ct)
                    .ConfigureAwait(false);
            return hitFound ? new MagicLocation(true, hitAddr, pageAddr) : MagicLocation.NotFound;
        }
        else
        {
            var (found, pageAddr) =
                await BinarySearchPageAsync(magics, true, engine, from, to, scanPageSize, magicAlign, buf, ct)
                    .ConfigureAwait(false);
            if (!found) return MagicLocation.NotFound;
            var (hitFound, hitAddr) =
                await ScanPageForwardAsync(engine, magics, scanPageSize, magicAlign, pageAddr, buf, ct)
                    .ConfigureAwait(false);
            return hitFound ? new MagicLocation(true, hitAddr, pageAddr) : MagicLocation.NotFound;
        }
    }

    /// <summary>
    /// ★ 页级二分（同步统一版）：first=false 找最后一个含 magic 页（页集合是前缀），
    /// first=true 找第一个含 magic 页（页集合是后缀）。前置条件归使用方断言。
    /// </summary>
    private static (bool Found, LogicalAddress PageAddr) BinarySearchPage(ReadOnlySpan<uint> magics, bool first,
        IStorageEngine engine, LogicalAddress lo, LogicalAddress hi, int scanPageSize, int magicAlign,
        AlignedMemoryManager buf)
    {
        int pageMask = scanPageSize - 1;
        bool found = false;
        LogicalAddress result = LogicalAddress.Invalid; // 未命中哨兵 = Invalid（Empty 是合法 seg0@0）
        long hiDist = engine.GetDistance(lo, hi);
        // ★ hiDist > 0 即有数据：即使不足一页（alignedHiDist=0），lo 所在页也要扫。
        if (hiDist <= 0) return (false, result);
        long alignedHiDist = (hiDist - 1) & ~(long)pageMask;
        var alignedHi = engine.CalculationAddress(lo, alignedHiDist); // alignedHiDist=0 时 = lo，合法

        while (lo <= alignedHi)
        {
            long span = engine.GetDistance(lo, alignedHi);
            long midDist = (span >> 1) & ~(long)pageMask;
            if (midDist < 0) break;
            if (midDist == 0)
            {
                // ★ 终局：lo 与 alignedHi 至多相差一页（span ∈ {0, pageSize}）——两页起点都查，
                //   按方向序（First 从前往后要最早的；Last 从后往前要最晚的）。
                //   只查一页会漏掉另一页（尾部页 magic 丢失——0% 覆盖期潜伏的历史 bug）。
                if (first)
                {
                    if (engine.PageContainsMagic(magics, magicAlign, lo, scanPageSize, buf))
                    {
                        found = true;
                        result = lo;
                    }
                    else if (engine.PageContainsMagic(magics, magicAlign, alignedHi, scanPageSize, buf))
                    {
                        found = true;
                        result = alignedHi;
                    }
                }
                else
                {
                    if (engine.PageContainsMagic(magics, magicAlign, alignedHi, scanPageSize, buf))
                    {
                        found = true;
                        result = alignedHi;
                    }
                    else if (engine.PageContainsMagic(magics, magicAlign, lo, scanPageSize, buf))
                    {
                        found = true;
                        result = lo;
                    }
                }

                break;
            }

            var mid = engine.CalculationAddress(lo, midDist);
            bool hasMagic = engine.PageContainsMagic(magics, magicAlign, mid, scanPageSize, buf);
            if (hasMagic)
            {
                found = true;
                result = mid;
                if (first) alignedHi = engine.CalculationAddress(lo, midDist - scanPageSize); // 往左找更早
                else lo = engine.CalculationAddress(mid, scanPageSize); // 往右找更晚
            }
            else
            {
                // ★ 后撤用距离算术（midDist ≥ pageSize 恒成立 → 非负；CalculationAddress 已支持 ±）
                if (first) lo = engine.CalculationAddress(mid, scanPageSize); // 无 magic 段在左 → 往右
                else alignedHi = engine.CalculationAddress(lo, midDist - scanPageSize);
            }
        }

        return (found, result);
    }

    private static async ValueTask<(bool Found, LogicalAddress PageAddr)> BinarySearchPageAsync(
        IReadOnlyList<uint> magics, bool first,
        IStorageEngine engine, LogicalAddress lo, LogicalAddress hi, int scanPageSize, int magicAlign,
        AlignedMemoryManager buf, CancellationToken ct)
    {
        int pageMask = scanPageSize - 1;
        bool found = false;
        LogicalAddress result = LogicalAddress.Invalid;
        long hiDist = engine.GetDistance(lo, hi);
        if (hiDist <= 0) return (false, result);
        long alignedHiDist = (hiDist - 1) & ~(long)pageMask;
        var alignedHi = engine.CalculationAddress(lo, alignedHiDist);

        while (lo <= alignedHi)
        {
            ct.ThrowIfCancellationRequested();
            long span = engine.GetDistance(lo, alignedHi);
            long midDist = (span >> 1) & ~(long)pageMask;
            if (midDist < 0) break;
            if (midDist == 0)
            {
                if (first)
                {
                    if (await engine.PageContainsMagicAsync(magics, magicAlign, lo, scanPageSize, buf, ct)
                            .ConfigureAwait(false))
                    {
                        found = true;
                        result = lo;
                    }
                    else if (await engine.PageContainsMagicAsync(magics, magicAlign, alignedHi, scanPageSize, buf, ct)
                                 .ConfigureAwait(false))
                    {
                        found = true;
                        result = alignedHi;
                    }
                }
                else
                {
                    if (await engine.PageContainsMagicAsync(magics, magicAlign, alignedHi, scanPageSize, buf, ct)
                            .ConfigureAwait(false))
                    {
                        found = true;
                        result = alignedHi;
                    }
                    else if (await engine.PageContainsMagicAsync(magics, magicAlign, lo, scanPageSize, buf, ct)
                                 .ConfigureAwait(false))
                    {
                        found = true;
                        result = lo;
                    }
                }

                break;
            }

            var mid = engine.CalculationAddress(lo, midDist);
            bool hasMagic = await engine.PageContainsMagicAsync(magics, magicAlign, mid, scanPageSize, buf, ct)
                .ConfigureAwait(false);
            if (hasMagic)
            {
                found = true;
                result = mid;
                if (first) alignedHi = engine.CalculationAddress(lo, midDist - scanPageSize);
                else lo = engine.CalculationAddress(mid, scanPageSize);
            }
            else
            {
                if (first) lo = engine.CalculationAddress(mid, scanPageSize);
                else alignedHi = engine.CalculationAddress(lo, midDist - scanPageSize);
            }
        }

        return (found, result);
    }

    /// <summary>★ 页内正向扫 magic——返回第一个 magic 命中的精确 LogicalAddress。</summary>
    private static (bool Found, LogicalAddress MagicAddr) ScanPageForward(this IStorageEngine engine,
        ReadOnlySpan<uint> magics,
        int scanPageSize, int magicAlign, LogicalAddress pageAddr, AlignedMemoryManager buf)
    {
        int got = engine.Read(pageAddr, buf.GetSpan(0, scanPageSize));
        if (got <= 0) return (false, LogicalAddress.Invalid);
        return ScanSpanForward(buf.GetSpan(0, got), magics, magicAlign, out int hit)
            ? (true, engine.CalculationAddress(pageAddr, hit))
            : (false, LogicalAddress.Invalid);
    }

    /// <summary>★ 页内反向扫 magic——返回最后一个 magic 命中的精确 LogicalAddress。</summary>
    private static (bool Found, LogicalAddress MagicAddr) ScanPageReverse(this IStorageEngine engine,
        ReadOnlySpan<uint> magics,
        int scanPageSize, int magicAlign, LogicalAddress pageAddr, AlignedMemoryManager buf)
    {
        int got = engine.Read(pageAddr, buf.GetSpan(0, scanPageSize));
        if (got <= 0) return (false, LogicalAddress.Invalid);
        return ScanSpanReverse(buf.GetSpan(0, got), magics, magicAlign, out int hit)
            ? (true, engine.CalculationAddress(pageAddr, hit))
            : (false, LogicalAddress.Invalid);
    }

    private static async ValueTask<(bool Found, LogicalAddress MagicAddr)> ScanPageForwardAsync(
        this IStorageEngine engine,
        IReadOnlyList<uint> magics, int scanPageSize, int magicAlign, LogicalAddress pageAddr, AlignedMemoryManager buf,
        CancellationToken ct)
    {
        int got = await engine.ReadAsync(pageAddr, buf.Memory[..scanPageSize], ct).ConfigureAwait(false);
        if (got <= 0) return (false, LogicalAddress.Invalid);
        return ScanSpanForward(buf.GetSpan(0, got), magics, magicAlign, out int hit)
            ? (true, engine.CalculationAddress(pageAddr, hit))
            : (false, LogicalAddress.Invalid);
    }

    private static async ValueTask<(bool Found, LogicalAddress MagicAddr)> ScanPageReverseAsync(
        this IStorageEngine engine,
        IReadOnlyList<uint> magics, int scanPageSize, int magicAlign, LogicalAddress pageAddr, AlignedMemoryManager buf,
        CancellationToken ct)
    {
        int got = await engine.ReadAsync(pageAddr, buf.Memory[..scanPageSize], ct).ConfigureAwait(false);
        if (got <= 0) return (false, LogicalAddress.Invalid);
        return ScanSpanReverse(buf.GetSpan(0, got), magics, magicAlign, out int hit)
            ? (true, engine.CalculationAddress(pageAddr, hit))
            : (false, LogicalAddress.Invalid);
    }

    private static bool PageContainsMagic(this IStorageEngine engine, ReadOnlySpan<uint> magics,
        int magicAlign, LogicalAddress pageAddr, int scanPageSize, AlignedMemoryManager buf)
    {
        int got = engine.Read(pageAddr, buf.GetSpan(0, scanPageSize));
        return got > 0 && PageSpanContainsMagic(buf.GetSpan(0, got), magics, magicAlign);
    }

    private static async ValueTask<bool> PageContainsMagicAsync(this IStorageEngine engine, IReadOnlyList<uint> magics,
        int magicAlign, LogicalAddress pageAddr, int scanPageSize, AlignedMemoryManager buf, CancellationToken ct)
    {
        int got = await engine.ReadAsync(pageAddr, buf.Memory[..scanPageSize], ct).ConfigureAwait(false);
        return got > 0 && PageSpanContainsMagic(buf.GetSpan(0, got), magics, magicAlign);
    }

    /// <summary>页内按 alignment 步进正向匹配（任一偏移命中任一 magic）。</summary>
    private static bool ScanSpanForward(Span<byte> page, ReadOnlySpan<uint> magics, int align, out int hitOffset)
    {
        for (int off = 0; off + 4 <= page.Length; off += align)
        {
            uint v = BitConverter.ToUInt32(page.Slice(off, 4));
            foreach (uint m in magics)
                if (v == m)
                {
                    hitOffset = off;
                    return true;
                }
        }

        hitOffset = 0;
        return false;
    }

    /// <summary>页内按 alignment 步进反向匹配（返回最后一个命中偏移）。</summary>
    private static bool ScanSpanReverse(Span<byte> page, ReadOnlySpan<uint> magics, int align, out int hitOffset)
    {
        int alignMask = align - 1;
        for (int off = (page.Length - 4) & ~alignMask; off >= 0; off -= align)
        {
            uint v = BitConverter.ToUInt32(page.Slice(off, 4));
            foreach (uint m in magics)
                if (v == m)
                {
                    hitOffset = off;
                    return true;
                }
        }

        hitOffset = 0;
        return false;
    }

    /// <summary>页内按 alignment 步进，任一偏移命中任一 magic 即 true。</summary>
    private static bool PageSpanContainsMagic(Span<byte> page, ReadOnlySpan<uint> magics, int align)
    {
        for (int off = 0; off + 4 <= page.Length; off += align)
        {
            uint v = BitConverter.ToUInt32(page.Slice(off, 4));
            foreach (uint m in magics)
                if (v == m)
                    return true;
        }

        return false;
    }

    // ══ IReadOnlyList 重载（异步轨——magics 经参数进入不跨 await 的页内扫描）══

    /// <summary>页内按 alignment 步进正向匹配（任一偏移命中任一 magic；IReadOnlyList 版）。</summary>
    private static bool ScanSpanForward(Span<byte> page, IReadOnlyList<uint> magics, int align, out int hitOffset)
    {
        int count = magics.Count;
        for (int off = 0; off + 4 <= page.Length; off += align)
        {
            uint v = BitConverter.ToUInt32(page.Slice(off, 4));
            for (int i = 0; i < count; i++)
                if (v == magics[i])
                {
                    hitOffset = off;
                    return true;
                }
        }

        hitOffset = 0;
        return false;
    }

    /// <summary>页内按 alignment 步进反向匹配（返回最后一个命中偏移；IReadOnlyList 版）。</summary>
    private static bool ScanSpanReverse(Span<byte> page, IReadOnlyList<uint> magics, int align, out int hitOffset)
    {
        int alignMask = align - 1;
        int count = magics.Count;
        for (int off = (page.Length - 4) & ~alignMask; off >= 0; off -= align)
        {
            uint v = BitConverter.ToUInt32(page.Slice(off, 4));
            for (int i = 0; i < count; i++)
                if (v == magics[i])
                {
                    hitOffset = off;
                    return true;
                }
        }

        hitOffset = 0;
        return false;
    }

    /// <summary>页内按 alignment 步进，任一偏移命中任一 magic 即 true（IReadOnlyList 版）。</summary>
    private static bool PageSpanContainsMagic(Span<byte> page, IReadOnlyList<uint> magics, int align)
    {
        int count = magics.Count;
        for (int off = 0; off + 4 <= page.Length; off += align)
        {
            uint v = BitConverter.ToUInt32(page.Slice(off, 4));
            for (int i = 0; i < count; i++)
                if (v == magics[i])
                    return true;
        }

        return false;
    }
}