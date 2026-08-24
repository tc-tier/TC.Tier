using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Shared;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// 维护门闩契约测试套（raw-medium-and-conversion-design §8）——同一套断言跑 Disk/Mem/Remote 三介质。
/// 覆盖：WriteOperations 档（写拒/读放行/Flush 不拒）/ AllOperations 档（读写全拒）/
/// 非重入 / RAII 租约解除 / 错误可辨识（UnderMaintenance ≠ Unsupported）/ 打开意图分档。
/// 门闩核心（在途收敛/取消回滚）另见 <see cref="MaintenanceGateCoreTests"/>（直测共享核心件）。
/// </summary>
public abstract class MaintenanceGateTests : IDisposable
{
    /// <summary>创建受测 fs（子类提供介质）。</summary>
    protected abstract IFileSystem Fs { get; }

    protected static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileOpenMode mode = FileOpenMode.OpenOrCreate, FileSharing sharing = FileSharing.ReadWrite)
        => new() { Access = access, Mode = mode, Sharing = sharing };

    public abstract void Dispose();

    // ═══════════════ 能力位 ═══════════════

    [Fact]
    public void Capability_MaintenanceGate_Set()
        => Fs.Capabilities.HasFlag(FileSystemCapabilities.MaintenanceGate).Should().BeTrue("三介质统一置位（设计 §8.1）");

    // ═══════════════ WriteOperations 档 ═══════════════

    [Fact]
    public void WriteScope_HandleWrite_RejectedWithUnderMaintenance()
    {
        using var h = Fs.Open("w", Opts());
        h.Write(0, new byte[16]);   // 门闩外正常写（基线）
        using var lease = Fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations);
        var act = () => h.Write(0, new byte[16]);
        act.Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance, "写被拒且错误可辨识（非 Unsupported）");
    }

    [Fact]
    public void WriteScope_Append_Rejected()
    {
        using var h = Fs.Open("a", Opts());
        using var lease = Fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations);
        var act = () => h.Append(new byte[8]);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.UnderMaintenance);
    }

    [Fact]
    public void WriteScope_NamespaceMutations_Rejected()
    {
        using var lease = Fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations);
        ((Action)(() => Fs.CreateFile("newfile"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance);
        ((Action)(() => Fs.Delete("w"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance);
        ((Action)(() => Fs.CreateDirectory("d"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance);
    }

    [Fact]
    public void WriteScope_OpenForWrite_Rejected_OpenForRead_Allowed()
    {
        using var h = Fs.Open("r", Opts());
        h.Write(0, new byte[16]);
        h.Flush();   // Remote：staging 落对象（fs 级可见性基线——Flush = 唯一持久化点）
        using var lease = Fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations);
        ((Action)(() => Fs.Open("r", Opts()))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance, "写意图打开按变异拒");
        using var hr = Fs.Open("r", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        hr.Read(0, new byte[4]).Should().BeGreaterThan(0, "读意图打开在 Write 档放行");
    }

    [Fact]
    public void WriteScope_Reads_Allowed()
    {
        using var h = Fs.Open("read-ok", Opts());
        h.Write(0, new byte[64]);
        h.Flush();   // Remote：fs 级可见性基线
        using var lease = Fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations);
        var buf = new byte[16];
        h.Read(0, buf).Should().Be(16, "句柄读放行");
        Fs.Stat("read-ok").Length.Should().Be(64, "Stat 放行");
        Fs.Exists("read-ok").Should().BeTrue("Exists 放行");
        Fs.EnumerateEntries(recursive: true).Count().Should().BeGreaterThan(0, "枚举放行");
    }

    [Fact]
    public void WriteScope_Flush_NotRejected()
    {
        using var h = Fs.Open("f", Opts());
        h.Write(0, new byte[16]);
        using var lease = Fs.EnterMaintenance("shutdown", MaintenanceScope.WriteOperations);
        // 契约：Flush 不拒（关闭协议组成：进维护 → 收敛 → Flush 置 clean——设计 §8.1）
        try { h.Flush(); }
        catch (FileIOException e) when (e.Error == IOError.UnderMaintenance)
        {
            throw new Xunit.Sdk.XunitException("Flush 被维护门闩拒绝——违反关闭协议契约");
        }
    }

    // ═══════════════ AllOperations 档 ═══════════════

    [Fact]
    public void AllScope_Reads_Rejected()
    {
        using var h = Fs.Open("all", Opts());
        h.Write(0, new byte[16]);
        using var lease = Fs.EnterMaintenance("isolate", MaintenanceScope.AllOperations);
        ((Action)(() => h.Read(0, new byte[4]))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance);
        ((Action)(() => Fs.Stat("all"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.UnderMaintenance);
    }

    // ═══════════════ 租约语义 ═══════════════

    [Fact]
    public void NonReentrant_SecondEnter_Throws()
    {
        using var lease = Fs.EnterMaintenance("first", MaintenanceScope.WriteOperations);
        var act = () => Fs.EnterMaintenance("second", MaintenanceScope.WriteOperations);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.UnderMaintenance);
    }

    [Fact]
    public void LeaseDispose_RestoresOperations()
    {
        using var h = Fs.Open("restore", Opts());
        var lease = Fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations);
        ((Action)(() => h.Write(0, new byte[4]))).Should().Throw<FileIOException>();
        lease.Dispose();
        var act = () => h.Write(0, new byte[4]);
        act.Should().NotThrow("RAII 释放后恢复可写");
        lease.Dispose();   // 双重 Dispose 幂等
    }
}

// ═══════════════ 三介质子类 ═══════════════

public sealed class DiskMaintenanceGateTests : MaintenanceGateTests
{
    private readonly string _dir = TestTempDir.Create("core-io-maintenance-disk");
    private DiskFileSystem? _fs;

    protected override IFileSystem Fs => _fs ??= DiskFileSystem.OpenOrCreate(_dir);

    public override void Dispose() => TestTempDir.TryCleanup(_dir);
}

public sealed class MemMaintenanceGateTests : MaintenanceGateTests
{
    private readonly MemoryFileSystem _fs = MemoryFileSystem.New();
    protected override IFileSystem Fs => _fs;
    public override void Dispose() => _fs.Dispose();
}

public sealed class RemoteMaintenanceGateTests : MaintenanceGateTests
{
    private readonly MemoryObjectStore _store = new();
    private RemoteFileSystem? _fs;
    protected override IFileSystem Fs => _fs ??= RemoteFileSystem.OpenOrCreate(_store);
    public override void Dispose() => _fs?.Dispose();
}

// ═══════════════ 门闩核心单测（在途收敛 / 取消回滚 / 双检防竞态）═══════════════

/// <summary>
/// MaintenanceGate 共享核心件直测——在途变异收敛等待、ct 取消回滚（现场可恢复）、
/// 迟到计数者被复查拦截（双检协议）。
/// </summary>
public sealed class MaintenanceGateCoreTests
{
    [Fact]
    public void Enter_WaitsForInFlightMutation_ToDrain()
    {
        var gate = new MaintenanceGate();
        var m1 = gate.BeginMutation("Write", "f");
        var entered = new TaskCompletionSource();
        var task = Task.Run(() =>
        {
            using var lease = gate.Enter("backup", MaintenanceScope.WriteOperations, CancellationToken.None);
            entered.SetResult();
        });
        // RM-14 确定性化：Enter 的等待循环 1ms 轮询（SpinWait→Thread.Sleep(1)），
        // 给足确定性窗口后断言"在途未退则 Enter 不可能返回"——原 200ms 裸 WaitAny 在负载机
        // 上调度抖动会偶发假阳（task 尚未起跑就断言）。事件对齐：等 task 进入等待态再断言。
        entered.Task.Wait(500).Should().BeFalse("在途变异未归零，Enter 必须阻塞（500ms 确定性窗口）");
        m1.Dispose();   // 在途退出
        task.Wait(2000).Should().BeTrue("在途归零后 Enter 返回");
    }

    [Fact]
    public void Enter_CancelledWhileWaiting_RollsBack_现场可恢复()
    {
        var gate = new MaintenanceGate();
        var m = gate.BeginMutation("Write", "f");
        using var cts = new CancellationTokenSource(100);
        var act = () => gate.Enter("backup", MaintenanceScope.WriteOperations, cts.Token);
        act.Should().Throw<OperationCanceledException>();
        m.Dispose();
        // 取消回滚：门已开门——新变异可通过、新 Enter 可成功
        using (gate.BeginMutation("Write2", "f")) { }
        using var lease = gate.Enter("backup2", MaintenanceScope.WriteOperations, CancellationToken.None);
    }

    [Fact]
    public void LateMutation_AfterClose_IsRejected_DoubleCheckProtocol()
    {
        var gate = new MaintenanceGate();
        using var lease = gate.Enter("backup", MaintenanceScope.WriteOperations, CancellationToken.None);
        var act = () => gate.BeginMutation("Write", "f");
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.UnderMaintenance);
    }

    [Fact]
    public void ReadsRejected_OnlyInAllScope()
    {
        var gate = new MaintenanceGate();
        using (var lease = gate.Enter("backup", MaintenanceScope.WriteOperations, CancellationToken.None))
        {
            var act = () => gate.ThrowIfReadsRejected("Read", "f");
            act.Should().NotThrow("Write 档读放行");
        }
        using (var lease = gate.Enter("isolate", MaintenanceScope.AllOperations, CancellationToken.None))
        {
            var act = () => gate.ThrowIfReadsRejected("Read", "f");
            act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.UnderMaintenance);
        }
    }
}
