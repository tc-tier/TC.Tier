namespace TC.Tier.Core.Tests.Hazards;

/// <summary>HazardPointers 原语契约测试族（设计档案 docs/design/hazard-pointers-design.md §8.1）。
/// <para>覆盖：Publish/Unpublish 语义、TryProtect 发布-验证语义、Retire-Reclaim 恰好一次（含并发 Scan）、
/// 水位触发、活性契约（池空强制 Scan + 任意线程显式 Scan）、多域隔离、Register/Dispose 配对与绊线、
/// 泄漏守恒、并发风暴 canary（保护期内句柄不得被回收 = F1 绊线）、线程生命周期条目复用、热路径零分配。</para>
/// <para>★ 并发测试纪律：worker 线程内断言收集到 failures 队列（裸线程未处理异常会杀 testhost）；
/// Join 一律带超时护栏——失败可见优于挂死。</para>
/// </summary>
public class HazardPointersTests
{
    private static Thread Run(string name, ThreadStart body)
    {
        var t = new Thread(body) { Name = name, IsBackground = true };
        t.Start();
        return t;
    }

    private static void JoinOrFail(Thread t, string name)
        => t.Join(30_000).Should().BeTrue($"线程 {name} 应在 30s 内结束——疑似卡死（楔死铁律：取证而非干等）");

    // ════════════════════════════════════════════════════════════
    //  构造校验
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Ctor_InvalidArguments_Throws()
    {
        ((Action)(() => _ = new HazardDomain(hazardSlotsPerThread: 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => _ = new HazardDomain(hazardSlotsPerThread: 8))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => _ = new HazardDomain(retireThreshold: 0))).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ════════════════════════════════════════════════════════════
    //  Publish / Unprotect / TryProtect 语义
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Publish_ProtectsFromScan_UntilUnprotect()
    {
        using var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reg = d.Register();
        d.Publish(reg, 0, 42);

        var reclaimed = 0;
        d.Retire(42, _ => reclaimed++);
        d.Scan();
        reclaimed.Should().Be(0, "被 hazard 保护的句柄不得回收");

        d.Unprotect(reg, 0);
        d.Scan();
        reclaimed.Should().Be(1, "解除保护后扫描应回收");

        reg.Dispose();
    }

    [Fact]
    public void TryProtect_StableSource_ReturnsValidatedRef_AndProtects()
    {
        using var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reg = d.Register();
        long src = 7;

        d.TryProtect(reg, 1, ref src, out var v).Should().BeTrue();
        v.Should().Be(7);

        var reclaimed = 0;
        d.Retire(7, _ => reclaimed++);
        d.Scan();
        reclaimed.Should().Be(0, "TryProtect 取得的保护对扫描可见");

        d.Unprotect(reg, 1);
        d.Scan();
        reclaimed.Should().Be(1);

        reg.Dispose();
    }

    [Fact]
    public void TryProtect_EmptySource_ReturnsFalse()
    {
        using var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reg = d.Register();
        long src = 0;

        d.TryProtect(reg, 0, ref src, out var v).Should().BeFalse();
        v.Should().Be(0);

        reg.Dispose();
    }

    /// <summary>验证语义并发形态（R3 合规：句柄唯一、退休仅限已离开 source 的值）：
    /// mutator 把 src 推进到新唯一值并退休旧值；TryProtect 只可能返回"验证过的新鲜值"，
    /// 保护期内该句柄的 reclaim 计数必须冻结（F1 canary——保护不是装饰）。</summary>
    [Fact]
    public void TryProtect_ConcurrentlyChangingSource_HeldRefNeverReclaimed()
    {
        const int cycles = 50_000, vBase = 100;         // 值 = vBase + k，全局唯一（模拟 gen 打包的 slotRef）
        var d = new HazardDomain(maxThreads: 8, hazardSlotsPerThread: 2, retireThreshold: 32);
        var reg = d.Register();
        var src = (long)vBase;
        var counts = new int[cycles];
        var stop = 0;

        var mutator = Run("mutator", () =>
        {
            for (var k = 0; k < cycles - 1 && Volatile.Read(ref stop) == 0; k++)
            {
                var old = Volatile.Read(ref src);       // old == vBase + k（不变式：src 单调推进）
                Volatile.Write(ref src, vBase + k + 1);
                var kk = k;                             // 闭包捕获
                d.Retire(old, _ => Interlocked.Increment(ref counts[kk]));
            }
        });

        var rounds = 0;
        while (mutator.IsAlive && rounds < 200_000)
        {
            if (d.TryProtect(reg, 0, ref src, out var v))
            {
                v.Should().BeInRange(vBase, vBase + cycles, "只能返回来源出现过的值");
                var idx = (int)(v - vBase);
                var before = Volatile.Read(ref counts[idx]);
                Thread.SpinWait(200);
                Volatile.Read(ref counts[idx]).Should().Be(before, "保护期内被保护句柄不得被回收（F1 canary）");
                d.Unprotect(reg, 0);
            }
            rounds++;
        }
        Volatile.Write(ref stop, 1);
        JoinOrFail(mutator, "mutator");

        for (var guard = 0; guard < 100_000 && d.RetiredCount > 0; guard++) d.Scan();
        d.RetiredCount.Should().Be(0);
        counts.Sum().Should().BeGreaterThan(0, "mutator 应产生过退休");

        reg.Dispose();
        d.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  Retire-Reclaim 恰好一次（含并发 Scan）与水位
    // ════════════════════════════════════════════════════════════

    /// <summary>F5 核心：多生产者 + 多并发扫描器，每个句柄的 reclaim 恰好执行一次。</summary>
    [Fact]
    public void RetireReclaim_ExactlyOnce_UnderConcurrentScans()
    {
        const int producers = 4, perProducer = 128, total = producers * perProducer;
        var d = new HazardDomain(maxThreads: 8, hazardSlotsPerThread: 2, retireThreshold: 16, retireCapacity: 256);
        var counts = new int[total];
        var done = 0;

        var scanners = Enumerable.Range(0, 3).Select(i => Run($"scan{i}", () =>
        {
            while (Volatile.Read(ref done) == 0) d.Scan();
            d.Scan();
        })).ToArray();

        var producerFailures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var prod = Enumerable.Range(0, producers).Select(p => Run($"prod{p}", () =>
        {
            try
            {
                for (var k = 0; k < perProducer; k++)
                {
                    var r = p * perProducer + k + 1;          // 1..total（0 保留为空标记）
                    d.Retire(r, v => Interlocked.Increment(ref counts[(int)v - 1]));
                }
            }
            catch (Exception ex) { producerFailures.Enqueue(ex); }
        })).ToArray();
        foreach (var t in prod) JoinOrFail(t, "producer");
        producerFailures.Should().BeEmpty();

        Volatile.Write(ref done, 1);
        foreach (var t in scanners) JoinOrFail(t, "scanner");

        for (var guard = 0; guard < 100_000 && d.RetiredCount > 0; guard++) d.Scan();
        d.RetiredCount.Should().Be(0);
        counts.Should().OnlyContain(c => c == 1, "每个句柄的 reclaim 必须恰好一次（结构保证，非幂等假设）");

        d.Dispose();
    }

    [Fact]
    public void Watermark_CrossedOnRetire_ScansInline()
    {
        using var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 4, retireCapacity: 64);
        var reg = d.Register();                         // 保留句柄——收尾干净注销（Dispose 绊线要求）
        var reclaimed = 0;
        Action<long> act = _ => reclaimed++;

        d.Retire(1, act);
        d.Retire(2, act);
        d.Retire(3, act);
        reclaimed.Should().Be(0, "未达水位不应自动扫描");

        d.Retire(4, act);                              // 4 ≥ 水位 → push 线程顺带内联扫描
        reclaimed.Should().Be(4);
        d.RetiredCount.Should().Be(0);

        reg.Dispose();
    }

    /// <summary>活性契约：水位之下不自动推进，任意线程显式 Scan() 可驱动。</summary>
    [Fact]
    public void ExplicitScan_AnyThread_DrainsBelowWatermark()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reclaimed = 0;
        var worker = Run("retirer", () =>
        {
            for (var i = 1; i <= 3; i++) d.Retire(i, _ => Interlocked.Increment(ref reclaimed));
        });
        JoinOrFail(worker, "retirer");

        d.RetiredCount.Should().Be(3, "低于水位——退休滞留是预期状态");
        reclaimed.Should().Be(0);

        d.Scan();                                      // 扫描者 ≠ 退休者线程
        reclaimed.Should().Be(3);
        d.RetiredCount.Should().Be(0);

        d.Dispose();
    }

    /// <summary>活性契约兜底：退休记录池耗尽 → Retire 内部强制 Scan 推进（HP 下回收常是资源唯一来源）。</summary>
    [Fact]
    public void PoolExhaustion_ForcesScan_MakesProgress()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1000, retireCapacity: 4);
        var reg = d.Register();
        long s1 = 1, s2 = 2;
        d.TryProtect(reg, 0, ref s1, out _).Should().BeTrue();
        d.TryProtect(reg, 1, ref s2, out _).Should().BeTrue();

        var rec = new int[8];
        Action<long> act = v => rec[(int)v]++;
        d.Retire(1, act);                              // 受保护——幸存（记录池剩 2）
        d.Retire(2, act);
        d.Retire(3, act);                              // 无保护（池空；未达水位不自动扫）
        d.Retire(4, act);
        d.Retire(5, act);                              // 池空 → 强制 Scan → 3/4 回收 → 推进

        rec[3].Should().Be(1);
        rec[4].Should().Be(1);
        rec[5].Should().Be(0, "第 5 项在强制扫描之后才入链");
        d.RetiredCount.Should().Be(3, "1/2/5 仍在册（1/2 受保护、5 刚入链）");

        d.Unprotect(reg, 0);
        d.Unprotect(reg, 1);
        d.Scan();
        rec[1].Should().Be(1);
        rec[2].Should().Be(1);
        rec[5].Should().Be(1);
        d.RetiredCount.Should().Be(0);

        reg.Dispose();
        d.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  多域隔离 / 注册幂等 / 条目复用
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void MultiDomain_Isolation_AndPerDomainRegistration()
    {
        var dA = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var dB = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var rA1 = dA.Register();
        var rB = dB.Register();

        rA1.Should().NotBeSameAs(rB, "同线程在不同域各持一份注册");
        dA.Register().Should().BeSameAs(rA1, "幂等：同线程同域返回同一实例（跨域链查找）");

        dA.Publish(rA1, 0, 11);
        dB.Publish(rB, 0, 22);
        var ca = 0;
        var cb = 0;
        dA.Retire(11, _ => ca++);
        dB.Retire(22, _ => cb++);

        dA.Scan();
        dB.Scan();
        ca.Should().Be(0);
        cb.Should().Be(0, "两域各自保护各自的句柄");

        dA.Unprotect(rA1, 0);
        dA.Scan();
        ca.Should().Be(1);
        dB.Scan();
        cb.Should().Be(0, "A 域的扫描看不到 B 域的保护（隔离）");

        dB.Unprotect(rB, 0);
        dB.Scan();                                     // 收尾排空——域 Dispose 无残留（绊线）
        cb.Should().Be(1);
        rA1.Dispose();
        rB.Dispose();
        dA.Dispose();
        dB.Dispose();
    }

    [Fact]
    public void Register_Idempotent_SameThreadSameDomain()
    {
        using var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var r1 = d.Register();
        d.Register().Should().BeSameAs(r1);
        d.Register().Should().BeSameAs(r1);

        r1.Dispose();
        var r2 = d.Register();
        r2.Should().NotBeSameAs(r1, "注销后重注册获得新实例（条目复用）");

        long s = 5;
        d.TryProtect(r2, 0, ref s, out var v).Should().BeTrue();
        v.Should().Be(5);
        d.Unprotect(r2, 0);
        r2.Dispose();
    }

    /// <summary>线程生命周期：顺序短命线程反复 Register/Dispose，2 条目表复用 32 代——条目复用正确性。</summary>
    [Fact]
    public void EntryReuse_SequentialThreadLifetimes()
    {
        var d = new HazardDomain(maxThreads: 2, hazardSlotsPerThread: 2, retireThreshold: 1024);
        for (var k = 0; k < 32; k++)
        {
            var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var t = Run($"life{k}", () =>
            {
                try
                {
                    var r = d.Register();
                    long s = k + 1;
                    d.TryProtect(r, 0, ref s, out var v).Should().BeTrue();
                    v.Should().Be(k + 1);
                    d.Unprotect(r, 0);
                    r.Dispose();
                }
                catch (Exception ex) { failures.Enqueue(ex); }
            });
            JoinOrFail(t, $"life{k}");
            failures.Should().BeEmpty();
        }

        var reg = d.Register();
        long s2 = 999;
        d.TryProtect(reg, 0, ref s2, out var v2).Should().BeTrue();
        v2.Should().Be(999);
        d.Unprotect(reg, 0);
        reg.Dispose();
        d.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  泄漏守恒 + 并发风暴 canary
    // ════════════════════════════════════════════════════════════

    /// <summary>守恒律（R3 合规：保护空间与退休空间不相交——退休句柄每生唯一）：
    /// 风暴收敛后「累计退休 == 累计 reclaim」，且保护空间（1..8）的 reclaim 恒为零
    /// （F1 canary——被保护句柄从未被回收）。域 Dispose 在 DEBUG 下自带悬挂/残留绊线（隐藏断言）。</summary>
    [Fact]
    public void Conservation_Storm_CanaryHolds()
    {
        const int workers = 6, iters = 4000, rBase = 1000;   // 退休值 = rBase + w*iters + i，全局唯一
        var d = new HazardDomain(maxThreads: 8, hazardSlotsPerThread: 2, retireThreshold: 64);
        var edges = new long[8];
        for (var i = 0; i < 8; i++) edges[i] = i + 1;
        var reclaimCounts = new long[rBase + workers * iters];
        long retired = 0, reclaimed = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var ts = Enumerable.Range(0, workers).Select(w => Run($"w{w}", () =>
        {
            try
            {
                var rnd = new Random(w + 1);
                var reg = d.Register();
                for (var i = 0; i < iters; i++)
                {
                    var op = rnd.Next(3);
                    if (op == 0)
                    {
                        if (d.TryProtect(reg, 0, ref edges[rnd.Next(8)], out var v))
                        {
                            Thread.SpinWait(64);
                            Volatile.Read(ref reclaimCounts[(int)v]).Should()
                                .Be(0, "保护空间（1..8）从未被退休——恒为零；非零即 F1（保护失效）");
                            d.Unprotect(reg, 0);
                        }
                    }
                    else if (op == 1)
                    {
                        Interlocked.Increment(ref retired);
                        var value = rBase + w * iters + i;
                        d.Retire(value, v =>
                        {
                            Interlocked.Increment(ref reclaimed);
                            Interlocked.Increment(ref reclaimCounts[(int)v]);
                        });
                    }
                    else d.Scan();
                }
                reg.Dispose();                        // 收尾无保护——干净注销
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();

        foreach (var t in ts) JoinOrFail(t, "worker");
        failures.Should().BeEmpty();

        for (var guard = 0; guard < 1_000_000 && d.RetiredCount > 0; guard++) d.Scan();
        d.RetiredCount.Should().Be(0);
        reclaimed.Should().Be(retired, "守恒律：风暴收敛后每次 Retire 恰好一次 reclaim");
        reclaimed.Should().BeGreaterThan(0);

        d.Dispose();
    }

    /// <summary>零分配纪律：热路径（TryProtect/Unprotect/Retire/Scan）稳定态零分配。</summary>
    [Fact]
    public void HotPath_ZeroAllocations()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 64);
        var reg = d.Register();
        long src = 7;
        Action<long> act = _ => { };

        for (var i = 0; i < 2000; i++)                 // 预热（JIT 分层稳定）
        {
            d.TryProtect(reg, 0, ref src, out _);
            d.Unprotect(reg, 0);
            d.Retire(7, act);
        }
        d.Scan();
        GC.Collect();
        var b0 = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20_000; i++)
        {
            d.TryProtect(reg, 0, ref src, out _);
            d.Unprotect(reg, 0);
            d.Retire(7, act);                          // 水位顺带扫描同样零分配（快照缓冲预分配 + 原地排序）
        }
        var delta = GC.GetAllocatedBytesForCurrentThread() - b0;
        delta.Should().Be(0, "热路径零分配是本原语的存在理由（对照 A 路线 1.3KB/op）");

        for (var guard = 0; guard < 1000 && d.RetiredCount > 0; guard++) d.Scan();   // 水位之下残余 ≤ 阈值-1，排空后收尾
        d.RetiredCount.Should().Be(0);
        reg.Dispose();
        d.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  常开校验（非 DEBUG-only）：错域注册 / 槽越界
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Publish_WrongDomainRegistration_Throws()
    {
        var dA = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var dB = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var rB = dB.Register();

        ((Action)(() => dA.Publish(rB, 0, 1))).Should().Throw<InvalidOperationException>(
            "静默后果是写他域线程表条目——比协议违反严重一档，必须常开检查");

        rB.Dispose();
        dB.Dispose();
        dA.Dispose();
    }

    [Fact]
    public void Publish_SlotOutOfRange_Throws()
    {
        using var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reg = d.Register();
        ((Action)(() => d.Publish(reg, 2, 1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => d.Publish(reg, -1, 1))).Should().Throw<ArgumentOutOfRangeException>();
        reg.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  F2：漏注销滞留（语义，双配置有效）+ DEBUG 绊线族
    // ════════════════════════════════════════════════════════════

    /// <summary>F2：线程退出未注销 → 悬挂 hazard 挡住回收——后果是<b>退休滞留</b>（非内存损坏）。
    /// DEBUG 下域 Dispose 绊线命中（活注册）。</summary>
    [Fact]
    public void LeakedThreadRegistration_StallsReclaim_NotCorrupts()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var leaker = Run("leaker", () =>
        {
            var reg = d.Register();
            d.Publish(reg, 0, 55);                     // 悬挂 hazard——线程退出不注销
        });
        JoinOrFail(leaker, "leaker");

        var reclaimed = 0;
        d.Retire(55, _ => Interlocked.Increment(ref reclaimed));
        d.Scan();
        reclaimed.Should().Be(0, "死线程的悬挂 hazard 挡住回收——滞留而非损坏（F2 语义）");
        d.RetiredCount.Should().Be(1);

#if DEBUG
        ((Action)d.Dispose).Should().Throw<InvalidOperationException>(
            "DEBUG 绊线：Dispose 时仍有活注册（Register/Dispose 未配对）");
#endif
    }

#if DEBUG
    [Fact]
    public void DomainDispose_LeftoverRetired_Throws()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        d.Retire(5, _ => { });                         // 无注册、不扫描——残留
        ((Action)d.Dispose).Should().Throw<InvalidOperationException>().WithMessage("*未回收*");
    }

    [Fact]
    public void RegistrationDispose_HoldingHazard_Throws_ThenRecoverable()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reg = d.Register();
        d.Publish(reg, 1, 42);

        ((Action)reg.Dispose).Should().Throw<InvalidOperationException>().WithMessage("*持保护注销*");

        d.Unprotect(reg, 1);                           // 绊线抛出前回滚标志——现场可恢复
        reg.Dispose();
        d.Dispose();
    }

    [Fact]
    public void Registration_DoubleDispose_Throws()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var reg = d.Register();
        reg.Dispose();
        ((Action)reg.Dispose).Should().Throw<InvalidOperationException>().WithMessage("*双重 Dispose*");
        d.Dispose();
    }

    [Fact]
    public void CrossThreadRegistrationUse_Throws()
    {
        var d = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 2, retireThreshold: 1024);
        var stolen = null as HazardRegistration;
        var owner = Run("owner", () => stolen = d.Register());
        JoinOrFail(owner, "owner");

        ((Action)(() => d.Publish(stolen!, 0, 1))).Should().Throw<InvalidOperationException>()
            .WithMessage("*跨线程*");
        // 条目悬挂（漏注销）——域不再 Dispose（其绊线会抛，由上一个测试覆盖）
    }
#endif
}
