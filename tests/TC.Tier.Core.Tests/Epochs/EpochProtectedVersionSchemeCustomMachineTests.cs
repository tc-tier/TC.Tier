namespace TC.Tier.Core.Tests.Epochs;

/// <summary>
/// 自定义多步版本状态机验证（EPVS 高级用法）。
/// 现有 EPVS 测试都走 AdvanceVersionWithCriticalSection（单步 SimpleVersionSchemeStateMachine），
/// 这里直接继承 VersionSchemeStateMachine 跑一个 3 步过渡，验证文档"自定义状态机"范式成立。
/// </summary>
public class EpochProtectedVersionSchemeCustomMachineTests
{
    private const byte PhasePrepare = 1;   // 自定义阶段标记（避开 Rest=0 与 bit7 中间态位）
    private const byte PhaseCommit = 2;

    /// <summary>3 步状态机：Rest@v → Prepare@v → Commit@v → Rest@(v+1)。</summary>
    private sealed class ThreeStepMachine(long toVersion) : VersionSchemeStateMachine(toVersion)
    {
        // 记录每次 OnEnteringState 的 from→to（临界区内、互斥）
        public List<(byte fromPhase, long fromVer, byte toPhase, long toVer)> Entered { get; } = new();

        public override bool GetNextStep(VersionSchemeState currentState, out VersionSchemeState nextState)
        {
            // 已到目标 Rest 状态——不再推进（EPVS 也会据此停机）
            if (currentState.Phase == VersionSchemeState.Rest &&
                currentState.Version == ActualToVersion)
            {
                nextState = default;
                return false;
            }
            // Rest@v → Prepare@v
            if (currentState.Phase == VersionSchemeState.Rest)
            {
                nextState = VersionSchemeState.Make(PhasePrepare, currentState.Version);
                return true;
            }
            // Prepare@v → Commit@v
            if (currentState.Phase == PhasePrepare)
            {
                nextState = VersionSchemeState.Make(PhaseCommit, currentState.Version);
                return true;
            }
            // Commit@v → Rest@(v+1)
            if (currentState.Phase == PhaseCommit)
            {
                nextState = VersionSchemeState.Make(VersionSchemeState.Rest, currentState.Version + 1);
                return true;
            }
            nextState = default;
            return false;
        }

        public override void OnEnteringState(VersionSchemeState fromState, VersionSchemeState toState)
        {
            lock (Entered)
                Entered.Add((fromState.Phase, fromState.Version, toState.Phase, toState.Version));
        }

        public override void AfterEnteringState(VersionSchemeState state) { }
    }

    [Fact]
    public void CustomMultiStepMachine_RunsAllPhases_InOrder_AndAdvancesVersion()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);
        long startVersion = epvs.CurrentState().Version;   // 初始为 1

        var machine = new ThreeStepMachine(toVersion: startVersion + 1);
        bool ok = epvs.ExecuteStateMachine(machine, spin: true);

        // 执行成功
        ok.Should().BeTrue();
        // 最终落点：Rest@(startVersion+1)
        var final = epvs.CurrentState();
        final.Phase.Should().Be(VersionSchemeState.Rest);
        final.Version.Should().Be(startVersion + 1);
        // 三步临界区都触发，顺序正确
        machine.Entered.Should().HaveCount(3);
        machine.Entered[0].Should().Be((VersionSchemeState.Rest, startVersion, PhasePrepare, startVersion));
        machine.Entered[1].Should().Be((PhasePrepare, startVersion, PhaseCommit, startVersion));
        machine.Entered[2].Should().Be((PhaseCommit, startVersion, VersionSchemeState.Rest, startVersion + 1));
    }

    [Fact]
    public void CustomMachine_NegativeOneToVersion_AutoIncrementsToOneHigher()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);
        long startVersion = epvs.CurrentState().Version;

        // toVersion = -1：不指定目标，自动推进到 当前版本+1
        var machine = new ThreeStepMachine(toVersion: -1);
        bool ok = epvs.ExecuteStateMachine(machine, spin: true);

        ok.Should().BeTrue();
        epvs.CurrentState().Phase.Should().Be(VersionSchemeState.Rest);
        epvs.CurrentState().Version.Should().Be(startVersion + 1);
        machine.Entered.Should().HaveCount(3);
    }

    [Fact]
    public async Task CustomMultiStepMachine_WithConcurrentReaders_CompletesAndReadersNeverSeeIntermediate()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);
        long startVersion = epvs.CurrentState().Version;

        int intermediateObserved = 0;
        int errors = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var state = epvs.Enter();
                    if (state.IsIntermediate())
                        Interlocked.Increment(ref intermediateObserved);
                    epvs.Refresh();
                    epvs.Leave();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        })).ToArray();

        // 读者并发期间执行多步状态机——步骤的 drain action 会在读者线程上推迟触发，
        // 走「非 bump 路径自动链接」分支。
        var machine = new ThreeStepMachine(toVersion: startVersion + 1);
        bool ok = epvs.ExecuteStateMachine(machine, spin: true);
        await Task.WhenAll(readers);

        ok.Should().BeTrue();
        Volatile.Read(ref errors).Should().Be(0);
        Volatile.Read(ref intermediateObserved).Should().Be(0, "Enter 返回的状态永远不该是中间态");
        var final = epvs.CurrentState();
        final.Phase.Should().Be(VersionSchemeState.Rest);
        final.Version.Should().Be(startVersion + 1);
        machine.Entered.Should().HaveCount(3);
    }

    [Fact]
    public async Task CustomMultiStepMachine_NonSpinningExecute_CompletesUnderReaderActivity()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);
        long startVersion = epvs.CurrentState().Version;

        // spin:false——不靠调用方自旋驱动，机器靠 drain action 自动链接
        // 与读者 Enter 的 TryStepStateMachine 接力完成全部三步。
        var machine = new ThreeStepMachine(toVersion: startVersion + 1);
        var status = epvs.TryExecuteStateMachine(machine);
        status.Should().Be(StateMachineExecutionStatus.OK);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!timeout.Token.IsCancellationRequested)
            {
                var state = epvs.CurrentState();
                if (state.Phase == VersionSchemeState.Rest && state.Version == startVersion + 1)
                    return;
                epvs.Enter();
                epvs.Leave();
            }
        })).ToArray();
        await Task.WhenAll(readers);

        var final = epvs.CurrentState();
        final.Phase.Should().Be(VersionSchemeState.Rest);
        final.Version.Should().Be(startVersion + 1);
        machine.Entered.Should().HaveCount(3);
    }
}
