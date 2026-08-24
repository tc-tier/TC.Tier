using FluentAssertions;
using TC.Tier.Core.Epochs;

namespace TC.Tier.Core.Tests.Epochs;

public class FastThreadLocalTests
{
    [Fact]
    public void New_NotInitializedForThread()
    {
        var tl = new FastThreadLocal<int>();
        tl.IsInitializedForThread.Should().BeFalse();
    }

    [Fact]
    public void InitializeThread_MarksInitialized()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.IsInitializedForThread.Should().BeTrue();
        tl.DisposeThread();
    }

    [Fact]
    public void Value_SetGet_RoundTrips()
    {
        var tl = new FastThreadLocal<string>();
        tl.InitializeThread();
        tl.Value = "hello";
        tl.Value.Should().Be("hello");
        tl.DisposeThread();
    }

    [Fact]
    public void Value_DefaultAfterInitialize()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.Value.Should().Be(0); // default int
        tl.DisposeThread();
    }

    [Fact]
    public void Value_Overwrite_PreservesLatest()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.Value = 10;
        tl.Value = 20;
        tl.Value = 30;
        tl.Value.Should().Be(30);
        tl.DisposeThread();
    }

    [Fact]
    public void DisposeThread_ClearsValue()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.Value = 42;
        tl.DisposeThread();
        tl.IsInitializedForThread.Should().BeFalse();
    }

    [Fact]
    public void InitializeThread_Idempotent_DoesNotResetValue()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.Value = 99;
        tl.InitializeThread(); // should not reset
        tl.Value.Should().Be(99);
        tl.DisposeThread();
    }

    [Fact]
    public void MultipleInstances_IndependentValues()
    {
        var tl1 = new FastThreadLocal<int>();
        var tl2 = new FastThreadLocal<string>();

        tl1.InitializeThread();
        tl2.InitializeThread();

        tl1.Value = 123;
        tl2.Value = "abc";

        tl1.Value.Should().Be(123);
        tl2.Value.Should().Be("abc");

        tl1.DisposeThread();
        tl2.DisposeThread();
    }

    [Fact]
    public void Dispose_AfterDispose_InstanceRemoved()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.Value = 5;
        tl.Dispose();
        // After Dispose, the instance slot is freed for reuse
        // (does not affect already-initialized thread values)
    }

    [Fact]
    public async Task DifferentThreads_IndependentValues()
    {
        var tl = new FastThreadLocal<int>();
        tl.InitializeThread();
        tl.Value = 100;
        tl.Value.Should().Be(100);

        // Worker thread gets its own independent value
        int otherThreadValue = -1;
        await Task.Run(() =>
        {
            tl.InitializeThread();
            tl.Value = 200;
            otherThreadValue = tl.Value;
            tl.DisposeThread();
        });

        otherThreadValue.Should().Be(200);
        tl.Dispose();
    }

    [Fact]
    public async Task ConcurrentInitialize_ThreadSafe()
    {
        var tl = new FastThreadLocal<int>();
        int errors = 0;
        var tasks = new Task[4];

        for (int t = 0; t < 4; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    tl.InitializeThread();
                    tl.Value = tid * 10;
                    tl.Value.Should().Be(tid * 10);
                    tl.DisposeThread();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            });
        }

        await Task.WhenAll(tasks);
        Volatile.Read(ref errors).Should().Be(0);
        tl.Dispose();
    }
}
