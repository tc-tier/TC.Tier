using System.Reflection;
using System.Runtime.InteropServices;
using TC.Tier.Core.Primitives;
using FluentAssertions;

namespace TC.Tier.Core.Tests.Primitives;

public sealed class Atomic128Tests
{
    /// <summary>16B blittable 测试载荷（Lo 8B + Hi 8B，与 Int128 同型）。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct Payload16
    {
        public long Lo;
        public ulong Hi;
    }

    /// <summary>含引用字段（非 blittable）——校验应拒。</summary>
    private struct WithRef
    {
        public string? S { get; set; }
    }

    // ── 构造校验 ──

    [Fact]
    public void Constructor_WrongSize_Throws()
    {
        var act = () => new Atomic128<int>();   // int = 4B ≠ 16
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithReferenceFields_Throws()
    {
        var act = () => new Atomic128<WithRef>();
        act.Should().Throw<ArgumentException>();
    }

    // ── Store / Read ──

    [Fact]
    public void Store_Read_Roundtrip()
    {
        using var s = new Atomic128<Payload16>();
        s.Store(new Payload16 { Lo = 0x1111222233334444L, Hi = 0x5555666677778888UL });
        var v = s.Read();
        v.Lo.Should().Be(0x1111222233334444L);
        v.Hi.Should().Be(0x5555666677778888UL);
    }

    [Fact]
    public void Constructor_Initial_StoreAndRead()
    {
        using var s = new Atomic128<Payload16>(new Payload16 { Lo = 42, Hi = 99 });
        s.Read().Lo.Should().Be(42);
        s.Read().Hi.Should().Be(99UL);
    }

    // ── CAS 位精确 ──

    [Fact]
    public void TryCAS_Match_SucceedsAndUpdates()
    {
        using var s = new Atomic128<Payload16>(new Payload16 { Lo = 1, Hi = 2 });
        var ok = s.TryCompareExchange(new Payload16 { Lo = 1, Hi = 2 }, new Payload16 { Lo = 3, Hi = 4 });
        ok.Should().BeTrue();
        s.Read().Lo.Should().Be(3);
        s.Read().Hi.Should().Be(4UL);
    }

    [Fact]
    public void TryCAS_Mismatch_FailsNoChange()
    {
        using var s = new Atomic128<Payload16>(new Payload16 { Lo = 1, Hi = 2 });
        var ok = s.TryCompareExchange(new Payload16 { Lo = 1, Hi = 999 }, new Payload16 { Lo = 3, Hi = 4 });
        ok.Should().BeFalse();
        s.Read().Lo.Should().Be(1);   // 不变
        s.Read().Hi.Should().Be(2UL);
    }

    [Fact]
    public void TryCAS_BitExact_AnyByteDiff_Fails()
    {
        // Hi 改一位也必须失败（位精确，含所有 16 字节）
        using var s = new Atomic128<Payload16>(new Payload16 { Lo = 0, Hi = 0 });
        s.TryCompareExchange(new Payload16 { Lo = 0, Hi = 1 }, new Payload16 { Lo = 0, Hi = 2 })
            .Should().BeFalse("Hi 不匹配应失败");
    }

    // ── 多线程无丢失（CAS 循环推进 counter，无 ABA——单调递增）──

    [Fact]
    public void MultiThread_CAS_Loop_NoLoss()
    {
        using var s = new Atomic128<Payload16>();
        const int threads = 4;
        const int perThread = 2000;
        var successes = new int[threads];

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var localSpin = new SpinWait();
            var done = 0;
            while (done < perThread)
            {
                var cur = s.Read();
                var next = new Payload16 { Lo = cur.Lo + 1, Hi = cur.Hi };
                if (s.TryCompareExchange(cur, next)) { done++; }
                else { localSpin.SpinOnce(); }
            }
            successes[t] = done;
        })).ToArray();

        Task.WaitAll(tasks);

        successes.Sum().Should().Be(threads * perThread, "每次成功推进计数 +1，无丢失");
        s.Read().Lo.Should().Be(threads * perThread, "最终 counter = 总推进次数");
    }

    // ── 降级路径（反射 _casEnabledForTesting=false → lock 分支，行为须与 native 一致）──

    [Fact]
    public void Fallback_LockPath_BehavesSameAsNative()
    {
        var field = typeof(Atomic128<Payload16>).GetField("_casEnabledForTesting",
            BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull("测试钩子字段必须存在");
        var original = (bool)field!.GetValue(null)!;

        try
        {
            field.SetValue(null, false);   // 强制走 lock 降级

            using var s = new Atomic128<Payload16>(new Payload16 { Lo = 10, Hi = 20 });

            // Store/Read
            s.Store(new Payload16 { Lo = 100, Hi = 200 });
            s.Read().Lo.Should().Be(100);
            s.Read().Hi.Should().Be(200UL);

            // CAS 匹配
            s.TryCompareExchange(new Payload16 { Lo = 100, Hi = 200 }, new Payload16 { Lo = 300, Hi = 400 })
                .Should().BeTrue();
            s.Read().Lo.Should().Be(300);

            // CAS 不匹配（位精确）
            s.TryCompareExchange(new Payload16 { Lo = 300, Hi = 999 }, new Payload16 { Lo = 0, Hi = 0 })
                .Should().BeFalse();
            s.Read().Lo.Should().Be(300);
        }
        finally
        {
            field.SetValue(null, original);   // 恢复，避免污染其他测试
        }
    }

    // ── Unsafe 快路径（跳过 CasEnabled/IsDisposed；正常情况行为与 safe 一致）──

    [Fact]
    public void Unsafe_TryCAS_Match_SucceedsAndUpdates()
    {
        using var s = new Atomic128<Payload16>(new Payload16 { Lo = 1, Hi = 2 });
        s.TryCompareExchangeUnsafe(new Payload16 { Lo = 1, Hi = 2 }, new Payload16 { Lo = 3, Hi = 4 })
            .Should().BeTrue();
        s.ReadUnsafe().Lo.Should().Be(3);
        s.ReadUnsafe().Hi.Should().Be(4UL);
    }

    [Fact]
    public void Unsafe_TryCAS_Mismatch_FailsNoChange()
    {
        using var s = new Atomic128<Payload16>(new Payload16 { Lo = 1, Hi = 2 });
        s.TryCompareExchangeUnsafe(new Payload16 { Lo = 1, Hi = 999 }, new Payload16 { Lo = 3, Hi = 4 })
            .Should().BeFalse();
        s.ReadUnsafe().Lo.Should().Be(1, "不匹配时值不变");
    }

    [Fact]
    public void Unsafe_Read_Roundtrip()
    {
        using var s = new Atomic128<Payload16>();
        s.Store(new Payload16 { Lo = 42, Hi = 7 });
        var v = s.ReadUnsafe();
        v.Lo.Should().Be(42);
        v.Hi.Should().Be(7UL);
    }

    // ── Dispose ──

    [Fact]
    public void IsDisposed_AndDispose_AfterDispose_ReadDefault_CASFails()
    {
        var s = new Atomic128<Payload16>(new Payload16 { Lo = 7, Hi = 8 });
        s.IsDisposed.Should().BeFalse();
        s.Dispose();
        s.IsDisposed.Should().BeTrue();
        s.Read().Lo.Should().Be(0, "Disposed 后 Read 返回 default");
        s.TryCompareExchange(new Payload16 { Lo = 0, Hi = 0 }, new Payload16 { Lo = 1, Hi = 1 })
            .Should().BeFalse("Disposed 后 CAS 失败");
    }
}
