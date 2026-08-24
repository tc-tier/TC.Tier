namespace TC.Tier.Core.Tests.Primitives;

public class AsyncCountDownTests
{
    [Fact]
    public void New_IsEmpty()
    {
        var cd = new AsyncCountDown();
        cd.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilEmptyAsync_InitiallyEmpty_CompletesImmediately()
    {
        var cd = new AsyncCountDown();
        await cd.WaitUntilEmptyAsync();
    }

    [Fact]
    public async Task Add_MakesNotEmpty_BlocksWait()
    {
        var cd = new AsyncCountDown();
        cd.Add();
        cd.IsEmpty.Should().BeFalse();

        var waitTask = cd.WaitUntilEmptyAsync().AsTask();
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_ToZero_CompletesWaiter()
    {
        var cd = new AsyncCountDown();
        cd.Add();

        var waitTask = cd.WaitUntilEmptyAsync().AsTask();
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();

        cd.Remove();
        cd.IsEmpty.Should().BeTrue();

        await waitTask;
        waitTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleAdds_AllMustBeRemovedToComplete()
    {
        var cd = new AsyncCountDown();
        cd.Add();
        cd.Add();
        cd.Add();

        var waitTask = cd.WaitUntilEmptyAsync().AsTask();
        await Task.Delay(30);

        cd.Remove();
        await Task.Delay(30);
        waitTask.IsCompleted.Should().BeFalse(); // 还有 2

        cd.Remove();
        await Task.Delay(30);
        waitTask.IsCompleted.Should().BeFalse(); // 还有 1

        cd.Remove();
        await waitTask; // 0，完成
    }

    [Fact]
    public async Task MultipleWaiters_AllWokenWhenReachesZero()
    {
        var cd = new AsyncCountDown();
        cd.Add();

        int completed = 0;
        var waiters = new Task[4];
        for (int i = 0; i < 4; i++)
        {
            waiters[i] = Task.Run(async () =>
            {
                await cd.WaitUntilEmptyAsync();
                Interlocked.Increment(ref completed);
            });
        }

        await Task.Delay(100);
        Volatile.Read(ref completed).Should().Be(0);

        cd.Remove();
        await Task.WhenAll(waiters);

        Volatile.Read(ref completed).Should().Be(4);
    }

    [Fact]
    public async Task Reuse_AfterZero_AddAgainBlocksAgain()
    {
        var cd = new AsyncCountDown();
        cd.Add();
        cd.Remove();

        // 第一次等待完成
        await cd.WaitUntilEmptyAsync();

        // 再次 Add，应重新阻塞
        cd.Add();
        cd.IsEmpty.Should().BeFalse();

        var waitTask = cd.WaitUntilEmptyAsync().AsTask();
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();

        cd.Remove();
        await waitTask;
    }

    [Fact]
    public async Task WaitUntilEmptyAsync_WithCancellation_ThrowsOCE()
    {
        var cd = new AsyncCountDown();
        cd.Add();
        using var cts = new CancellationTokenSource();

        var waitTask = cd.WaitUntilEmptyAsync(cts.Token).AsTask();
        await Task.Delay(50);

        cts.Cancel();

        var act = async () => await waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Concurrent_AddRemove_ReachesZeroCorrectly()
    {
        for (int iter = 0; iter < 20; iter++)
        {
            var cd = new AsyncCountDown();
            int count = 0;
            int target = 100;

            cd.Add(); // 初始非零

            var workers = new Task[4];
            for (int w = 0; w < 4; w++)
            {
                workers[w] = Task.Run(() =>
                {
                    for (int i = 0; i < target / 4; i++)
                    {
                        cd.Add();
                        Interlocked.Increment(ref count);
                    }
                });
            }
            await Task.WhenAll(workers);

            // 此时 count = target，cd 内部 counter = target + 1（含初始 Add）
            cd.IsEmpty.Should().BeFalse();

            var removerWorkers = new Task[4];
            for (int w = 0; w < 4; w++)
            {
                removerWorkers[w] = Task.Run(() =>
                {
                    for (int i = 0; i < count / 4; i++)
                        cd.Remove();
                });
            }
            await Task.WhenAll(removerWorkers);

            // 还剩初始的 1 个
            cd.IsEmpty.Should().BeFalse();

            // 最后 Remove 应到 0
            cd.Remove();
            cd.IsEmpty.Should().BeTrue();
        }
    }
}
