using System.Collections.Concurrent;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Testing;

/// <summary>
/// 故障注入装饰器——按「路径 × 操作 × 概率」三维组合注入异常（默认 <see cref="FileIOException"/>，测试替身）。
/// <para>★ 概率注入（seeded Random——可复现）与确定性注入（第 N 次匹配调用时注入——部分失败测试用）双模式。</para>
/// <para>★ 路径匹配：精确名或 <c>"*"</c>（全部）；操作匹配：操作名（"Read"/"Write"/"CopyRange"/…）或 <c>"*"</c>。</para>
/// <para>★ 注入点：fs 命名空间操作 + 句柄数据面操作（句柄被包装为 FaultInjectingFileHandle）。</para>
/// <para>★ 规则族（六族可叠加，各管各的注入维度，对齐传输面哲学）：错误（<see cref="AddRule"/>/
///   <see cref="AddExceptionRule"/>，前置抛出）、延迟（<see cref="AddDelayRule"/>，前置延迟）、
///   挂起（<see cref="AddHangRule"/>，永不到达内层）、乱序（<see cref="AddReorderRule"/>，异步写族延后提交——
///   仅异步操作生效）、腐败（<see cref="AddCorruptRule"/>，写转发成功后翻转落盘字节）。</para>
/// <para>★ Dispose 转发内层 fs（装饰器持有内层——典型用法为测试自建内层）。</para>
/// </summary>
internal sealed class FaultInjectingFileSystem : IFileSystem
{
    /// <summary>注入规则（六族共用载体——按家族进入各自队列，家族字段互斥）。</summary>
    public sealed class FaultRule
    {
        /// <summary>路径匹配（精确名或 "*"）。</summary>
        public required string PathPattern { get; init; }

        /// <summary>操作匹配（操作名或 "*"）。</summary>
        public required string OperationPattern { get; init; }

        /// <summary>类型化 I/O 规则注入的错误码；自定义异常规则为 null。</summary>
        public IOError? Error { get; init; }

        /// <summary>自定义异常规则的异常工厂；类型化 I/O 规则为 null。</summary>
        internal Func<Exception>? ExceptionFactory { get; init; }

        /// <summary>注入概率 [0,1]（与 <see cref="FailAtCallIndex"/> 互斥使用——两者都设时确定性优先）。</summary>
        public double Probability { get; init; }

        /// <summary>确定性注入：第 N 次（1 起）匹配调用时注入一次（部分失败/中途失败测试用）。</summary>
        public long? FailAtCallIndex { get; init; }

        /// <summary>附加上下文（进异常消息）。</summary>
        public string? Detail { get; init; }

        /// <summary>延迟规则的注入时长；其它族为 null。</summary>
        internal TimeSpan? Delay { get; init; }

        /// <summary>腐败规则的目标偏移（字节，绝对寻址）；其它族为 null。</summary>
        internal long? CorruptOffset { get; init; }

        /// <summary>腐败规则的字节掩码（按 <c>mask[i % Length]</c> 循环应用于 [Offset, Offset+Length)）；其它族为 null。</summary>
        internal byte[]? CorruptMask { get; init; }

        /// <summary>乱序规则的提交延迟（命中操作延后到独立执行流提交）；其它族为 null。</summary>
        internal TimeSpan? ReorderHold { get; init; }

        /// <summary>挂起规则的释放门（ClearRules/Dispose 置位——仅供测试拆除，故障语义 = 调用方视角永不到达）；其它族为 null。</summary>
        internal TaskCompletionSource? HangGate { get; init; }

        internal long MatchCount;
    }

    private readonly IFileSystem _inner;
    private readonly Random _random;
    private readonly ConcurrentQueue<FaultRule> _rules = new();
    private readonly ConcurrentQueue<FaultRule> _delayRules = new();
    private readonly ConcurrentQueue<FaultRule> _hangRules = new();
    private readonly ConcurrentQueue<FaultRule> _reorderRules = new();
    private readonly ConcurrentQueue<FaultRule> _corruptRules = new();
    private int _disposed;

    /// <summary>构造——包装内层 fs（seed 固定则故障序列可复现）。</summary>
    public FaultInjectingFileSystem(IFileSystem inner, int seed = 12345)
    {
        _inner = inner;
        _random = new Random(seed);
    }

    /// <param name="pathPattern">路径匹配（精确名或 "*" 全部）。</param>
    /// <param name="operationPattern">操作匹配（操作名或 "*"）。</param>
    /// <param name="error">注入的类型化错误码。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1.0 = 恒注入；与 failAtCallIndex 互斥，确定性优先）。</param>
    /// <param name="failAtCallIndex">第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</param>
    /// <param name="detail">附加上下文（进异常消息，可选）。</param>
    /// <returns>规则实例（可观测 MatchCount）。</returns>
    /// <summary>添加注入规则（返回规则实例——调用方可观测 MatchCount）。</summary>
    public FaultRule AddRule(string pathPattern, string operationPattern, IOError error,
        double probability = 1.0, long? failAtCallIndex = null, string? detail = null)
    {
        ValidateProbability(probability);
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            Error = error,
            Probability = probability,
            FailAtCallIndex = failAtCallIndex,
            Detail = detail,
        };
        _rules.Enqueue(rule);
        return rule;
    }

    /// <param name="pathPattern">路径匹配（精确名或 "*"）。</param>
    /// <param name="operationPattern">操作匹配（操作名或 "*"）。</param>
    /// <param name="exceptionFactory">异常工厂（每次注入调用一次取新实例）。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1.0；与 failAtCallIndex 互斥，确定性优先）。</param>
    /// <param name="failAtCallIndex">第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</param>
    /// <param name="detail">附加上下文（进异常消息，可选）。</param>
    /// <returns>规则实例（可观测 MatchCount）。</returns>
    /// <summary>添加自定义异常规则（用于验证非 I/O 致命异常的传播边界）。</summary>
    public FaultRule AddExceptionRule(string pathPattern, string operationPattern, Func<Exception> exceptionFactory,
        double probability = 1.0, long? failAtCallIndex = null, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        ValidateProbability(probability);
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            ExceptionFactory = exceptionFactory,
            Probability = probability,
            FailAtCallIndex = failAtCallIndex,
            Detail = detail,
        };
        _rules.Enqueue(rule);
        return rule;
    }

    /// <summary>清空全部规则（六族全清）并释放全部挂起门（拆除语义——被挂线程放行）。</summary>
    public void ClearRules()
    {
        _rules.Clear();
        _delayRules.Clear();
        _reorderRules.Clear();
        _corruptRules.Clear();
        ReleaseHangGates();
    }

    /// <summary>释放全部挂起门（拆除专用——故障语义下门永不置位，仅 ClearRules/Dispose 放行被挂线程）。</summary>
    private void ReleaseHangGates()
    {
        while (_hangRules.TryDequeue(out var rule)) rule.HangGate?.TrySetResult();
    }

    /// <summary>添加慢 IO 规则——命中操作前置延迟（引擎 watchdog / 节流降档 / 有界等待语义的确定性触发器）。</summary>
    /// <param name="pathPattern">路径匹配（精确名或 "*"）。</param>
    /// <param name="operationPattern">操作匹配（操作名或 "*"）。</param>
    /// <param name="delay">注入的前置延迟时长。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1.0；与 failAtCallIndex 互斥，确定性优先）。</param>
    /// <param name="failAtCallIndex">第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</param>
    /// <param name="detail">附加上下文（可选）。</param>
    /// <returns>规则实例（可观测 MatchCount）。</returns>
    public FaultRule AddDelayRule(string pathPattern, string operationPattern, TimeSpan delay,
        double probability = 1.0, long? failAtCallIndex = null, string? detail = null)
    {
        ValidateProbability(probability);
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            Delay = delay,
            Probability = probability,
            FailAtCallIndex = failAtCallIndex,
            Detail = detail,
        };
        _delayRules.Enqueue(rule);
        return rule;
    }

    /// <summary>添加挂起规则——命中操作永不到达内层（调用方视角永挂；上层 watchdog/有界等待的确定性触发器）。
    /// <para>★ 释放仅供测试拆除：<see cref="ClearRules"/>/Dispose 置位释放门后操作才继续——
    ///   正常故障语义下门永不置位，被测方必须以自身超时/取消路径先行处置。</para></summary>
    /// <param name="pathPattern">路径匹配（精确名或 "*"）。</param>
    /// <param name="operationPattern">操作匹配（操作名或 "*"）。</param>
    /// <param name="detail">附加上下文（可选）。</param>
    /// <returns>规则实例（可观测 MatchCount）。</returns>
    public FaultRule AddHangRule(string pathPattern, string operationPattern, string? detail = null)
    {
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            HangGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Probability = 1,
            Detail = detail,
        };
        _hangRules.Enqueue(rule);
        return rule;
    }

    /// <summary>添加乱序提交规则——命中的异步写族操作延后到独立执行流提交（mem 卷保序前提下的受控乱序，
    /// 掉电重排下恢复协议的确定性测试；真盘乱序仍归云上，二者互补）。
    /// <para>★ 仅异步写族操作（WriteAsync/AppendAsync/WriteVectorAsync）生效；同步操作永不命中乱序规则。</para></summary>
    /// <param name="pathPattern">路径匹配（精确名或 "*"）。</param>
    /// <param name="operationPattern">操作匹配（操作名或 "*"）。</param>
    /// <param name="holdDelay">命中操作延后提交的时长。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1.0；与 failAtCallIndex 互斥，确定性优先）。</param>
    /// <param name="failAtCallIndex">第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</param>
    /// <param name="detail">附加上下文（可选）。</param>
    /// <returns>规则实例（可观测 MatchCount）。</returns>
    public FaultRule AddReorderRule(string pathPattern, string operationPattern, TimeSpan holdDelay,
        double probability = 1.0, long? failAtCallIndex = null, string? detail = null)
    {
        ValidateProbability(probability);
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            ReorderHold = holdDelay,
            Probability = probability,
            FailAtCallIndex = failAtCallIndex,
            Detail = detail,
        };
        _reorderRules.Enqueue(rule);
        return rule;
    }

    /// <summary>添加静默腐败规则——命中写族操作转发成功后，对介质 [offset, offset+mask.Length) 现存字节
    /// 按掩码循环翻转（读-XOR-写回；确定性或概率）——读自愈/隔离防线、MagicLocator 恢复扫描、
    /// CRC 判废路径的对抗抓手。</summary>
    /// <param name="pathPattern">路径匹配（精确名或 "*"）。</param>
    /// <param name="operationPattern">操作匹配（写族操作名或 "*"）。</param>
    /// <param name="offset">腐败目标偏移（字节，绝对寻址）。</param>
    /// <param name="mask">字节掩码（按 <c>mask[i % Length]</c> 循环 XOR；非空）。</param>
    /// <param name="probability">注入概率 [0,1]（默认 1.0；与 failAtCallIndex 互斥，确定性优先）。</param>
    /// <param name="failAtCallIndex">第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</param>
    /// <param name="detail">附加上下文（可选）。</param>
    /// <returns>规则实例（可观测 MatchCount）。</returns>
    public FaultRule AddCorruptRule(string pathPattern, string operationPattern, long offset, ReadOnlyMemory<byte> mask,
        double probability = 1.0, long? failAtCallIndex = null, string? detail = null)
    {
        if (mask.IsEmpty) throw new ArgumentException("腐败掩码不能为空。", nameof(mask));
        ValidateProbability(probability);
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            CorruptOffset = offset,
            CorruptMask = mask.ToArray(),
            Probability = probability,
            FailAtCallIndex = failAtCallIndex,
            Detail = detail,
        };
        _corruptRules.Enqueue(rule);
        return rule;
    }

    private static void ValidateProbability(double probability)
    {
        if (probability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(probability));
    }

    /// <summary>错误规则评估（抛出注入——六族注入链的第一环）。</summary>
    private void MaybeThrow(string? path, string operation)
    {
        foreach (var rule in _rules)
        {
            if (!ShouldFire(rule, path, operation, out var n)) continue;
            if (rule.ExceptionFactory is { } exceptionFactory)
                throw exceptionFactory();

            var error = rule.Error
                        ?? throw new InvalidOperationException("Fault rule must define an I/O error or exception factory.");
            throw new FileIOException(error,
                $"[FaultInject] 注入 {error}（path={path}, op={operation}, match#{n}{(rule.Detail is null ? null : $", {rule.Detail}")}）",
                path, operation);
        }
    }

    private static bool Matches(string pattern, string? value)
        => pattern == "*" || string.Equals(pattern, value, StringComparison.Ordinal);

    /// <summary>规则命中判定（含 MatchCount 记账与概率/atCallIndex 仲裁——六族共用）。</summary>
    private bool ShouldFire(FaultRule rule, string? path, string operation, out long matchIndex)
    {
        if (!Matches(rule.PathPattern, path) || !Matches(rule.OperationPattern, operation))
        {
            matchIndex = 0;
            return false;
        }
        var n = Interlocked.Increment(ref rule.MatchCount);
        matchIndex = n;
        if (rule.FailAtCallIndex is { } idx) return n == idx;
        return _random.NextDouble() < rule.Probability;
    }

    /// <summary>同步入口注入链（fs 级操作 + 句柄同步数据面共用）：错误规则（抛）→ 延迟规则（睡）→ 挂起规则（阻塞至释放门置位）。</summary>
    private void MaybeInject(string? path, string operation)
    {
        MaybeThrow(path, operation);
        foreach (var rule in _delayRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
#pragma warning disable TCSG137 // 故障注入语义本体：同步前置延迟即 Thread.Sleep（测试替身，非生产等待）
            Thread.Sleep(rule.Delay!.Value);
#pragma warning restore TCSG137
        }
        foreach (var rule in _hangRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
#pragma warning disable TCSG137 // 故障注入语义本体：永挂（释放仅供测试拆除）——阻塞至 ClearRules/Dispose 置位
            rule.HangGate!.Task.Wait();
#pragma warning restore TCSG137
        }
    }

    /// <summary>异步入口注入链：错误规则（抛）→ 延迟规则（异步等）→ 挂起规则（等释放门，外部 ct 可取消）。</summary>
    private async Task MaybeInjectAsync(string? path, string operation, CancellationToken ct)
    {
        MaybeThrow(path, operation);
        foreach (var rule in _delayRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
            await Task.Delay(rule.Delay!.Value, ct).ConfigureAwait(false);
        }
        foreach (var rule in _hangRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
            await rule.HangGate!.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>乱序规则裁决（仅异步写族调用）——命中则返回延后提交任务：hold 到期后才转发内层，
    /// 与其后到达的并发操作形成受控乱序（介质可见顺序 = hold 后的提交顺序）。</summary>
    /// <param name="path">路径。</param>
    /// <param name="operation">操作名。</param>
    /// <param name="ct">调用方取消令牌（hold 期间取消 = 延后操作不执行）。</param>
    /// <param name="forward">内层转发委托。</param>
    /// <param name="deferred">命中时的延后提交任务（完成 = 内层已执行，结果透传）。</param>
    /// <returns>true = 命中乱序规则（调用方 await 后直接返回）；false = 未命中，调用方照常内联转发。</returns>
    private bool TryDeferReorder<T>(string? path, string operation, CancellationToken ct,
        Func<ValueTask<T>> forward, out ValueTask<T> deferred)
    {
        foreach (var rule in _reorderRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
            deferred = DelayThenForward(rule.ReorderHold!.Value, ct, forward);
            return true;
        }
        deferred = default;
        return false;
    }

    /// <summary>乱序规则裁决（无返回值版——WriteAsync/WriteVectorAsync 用）。</summary>
    private bool TryDeferReorder(string? path, string operation, CancellationToken ct,
        Func<ValueTask> forward, out ValueTask deferred)
    {
        foreach (var rule in _reorderRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
            deferred = DelayThenForward(rule.ReorderHold!.Value, ct, forward);
            return true;
        }
        deferred = default;
        return false;
    }

    /// <summary>乱序提交执行体——延后 hold 再转发内层（调用方等待完成；乱序体现于并发到达操作的介质可见顺序）。</summary>
    private static async ValueTask DelayThenForward(TimeSpan hold, CancellationToken ct, Func<ValueTask> forward)
    {
        await Task.Delay(hold, ct).ConfigureAwait(false);
        await forward().ConfigureAwait(false);
    }

    /// <summary>乱序提交执行体（带结果透传）。</summary>
    private static async ValueTask<T> DelayThenForward<T>(TimeSpan hold, CancellationToken ct, Func<ValueTask<T>> forward)
    {
        await Task.Delay(hold, ct).ConfigureAwait(false);
        return await forward().ConfigureAwait(false);
    }

    /// <summary>腐败规则评估（写族操作转发成功后调用）——读-XOR-写回翻转介质现存字节（静默：不抛不日志）。</summary>
    private void PostCorrupt(IFileHandle inner, string? path, string operation)
    {
        if (_corruptRules.IsEmpty) return;
        foreach (var rule in _corruptRules)
        {
            if (!ShouldFire(rule, path, operation, out _)) continue;
            var mask = rule.CorruptMask!;
            var offset = rule.CorruptOffset!.Value;
            var buffer = new byte[mask.Length];
            var read = inner.Read(offset, buffer);
            if (read <= 0) continue;
            for (var i = 0; i < read; i++) buffer[i] ^= mask[i % mask.Length];
            inner.Write(offset, buffer.AsSpan(0, read));
        }
    }

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc/>
    public VolumeInfo Volume => _inner.Volume;

    /// <inheritdoc/>
    /// <param name="path">文件相对路径。</param>
    /// <param name="options">打开选项。</param>
    /// <returns>注入包装句柄（数据面操作入口评估规则后转发内层）。</returns>
    public IFileHandle Open(string path, FileOpenOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Open");
        return new FaultInjectingFileHandle(this, _inner.Open(path, options), path);
    }

    /// <inheritdoc/>
    public void EnsureRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnsureRoot");
        _inner.EnsureRoot();
    }

    /// <inheritdoc/>
    public void FlushRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "FlushRoot");
        _inner.FlushRoot();
    }

    /// <inheritdoc/>
    /// <param name="path">文件相对路径。</param>
    /// <returns>内层判定结果（true = 存在）。</returns>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Exists");
        return _inner.Exists(path);
    }

    /// <inheritdoc/>
    /// <param name="path">文件相对路径。</param>
    public void Delete(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Delete");
        _inner.Delete(path);
    }

    /// <inheritdoc/>
    /// <param name="source">源文件相对路径。</param>
    /// <param name="dest">目标文件相对路径。</param>
    /// <param name="overwrite">true = 目标存在时覆盖（默认 false）。</param>
    public void Move(string source, string dest, bool overwrite = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(source, "Move");
        _inner.Move(source, dest, overwrite);
    }

    // ═══════════════ 根空间新成员转发（目录族/创建解耦/元数据/枚举族——注入操作名同名）═══════════════

    /// <inheritdoc/>
    /// <param name="path">目录相对路径。</param>
    public void CreateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "CreateDirectory");
        _inner.CreateDirectory(path);
    }

    /// <inheritdoc/>
    /// <param name="path">目录相对路径。</param>
    public void DeleteDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "DeleteDirectory");
        _inner.DeleteDirectory(path);
    }

    /// <inheritdoc/>
    /// <param name="path">目录相对路径。</param>
    /// <returns>内层判定结果（true = 存在）。</returns>
    public bool DirectoryExists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "DirectoryExists");
        return _inner.DirectoryExists(path);
    }

    /// <inheritdoc/>
    /// <param name="source">源目录相对路径。</param>
    /// <param name="dest">目标目录相对路径。</param>
    public void MoveDirectory(string source, string dest)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(source, "MoveDirectory");
        _inner.MoveDirectory(source, dest);
    }

    /// <inheritdoc/>
    /// <param name="path">文件相对路径。</param>
    /// <param name="preallocateSize">预分配字节数（默认 0）。</param>
    /// <param name="metadata">初始 FileExtra 内容（默认空）。</param>
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> metadata = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "CreateFile");
        _inner.CreateFile(path, preallocateSize, metadata);
    }

    /// <inheritdoc/>
    /// <param name="path">条目相对路径。</param>
    /// <returns>内层条目信息。</returns>
    public FsEntryInfo Stat(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Stat");
        return _inner.Stat(path);
    }

    /// <inheritdoc/>
    /// <param name="pattern">文件名通配模式（默认 "*"）。</param>
    /// <param name="recursive">true = 递归子目录（默认 false）。</param>
    /// <returns>内层文件条目序列。</returns>
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnumerateFiles");
        return _inner.EnumerateFiles(pattern, recursive);
    }

    /// <inheritdoc/>
    /// <param name="path">起始目录相对路径。</param>
    /// <param name="pattern">文件名通配模式。</param>
    /// <param name="recursive">true = 递归子目录（默认 false）。</param>
    /// <returns>内层文件条目序列。</returns>
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "EnumerateFiles");
        return _inner.EnumerateFiles(path, pattern, recursive);
    }

    /// <inheritdoc/>
    /// <param name="pattern">目录名通配模式（默认 "*"）。</param>
    /// <param name="recursive">true = 递归子目录（默认 false）。</param>
    /// <returns>内层目录条目序列。</returns>
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnumerateDirectories");
        return _inner.EnumerateDirectories(pattern, recursive);
    }

    /// <inheritdoc/>
    /// <param name="path">起始目录相对路径。</param>
    /// <param name="pattern">目录名通配模式。</param>
    /// <param name="recursive">true = 递归子目录（默认 false）。</param>
    /// <returns>内层目录条目序列。</returns>
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "EnumerateDirectories");
        return _inner.EnumerateDirectories(path, pattern, recursive);
    }

    /// <inheritdoc/>
    /// <param name="pattern">名称通配模式（默认 "*"）。</param>
    /// <param name="recursive">true = 递归子目录（默认 false）。</param>
    /// <returns>内层文件 + 目录条目序列。</returns>
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnumerateEntries");
        return _inner.EnumerateEntries(pattern, recursive);
    }

    /// <inheritdoc/>
    /// <param name="path">起始目录相对路径。</param>
    /// <param name="pattern">名称通配模式。</param>
    /// <param name="recursive">true = 递归子目录（默认 false）。</param>
    /// <returns>内层文件 + 目录条目序列。</returns>
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "EnumerateEntries");
        return _inner.EnumerateEntries(path, pattern, recursive);
    }

    /// <inheritdoc/>
    /// <inheritdoc/>
    /// <param name="reason">维护原因。</param>
    /// <param name="scope">维护范围。</param>
    /// <param name="ct">取消令牌（默认 default）。</param>
    /// <returns>内层维护租约。</returns>
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnterMaintenance");
        return _inner.EnterMaintenance(reason, scope, ct);
    }

    /// <summary>获取内层排他锁（入口评估注入规则后转发）。</summary>
    /// <param name="timeout">抢锁等待上限。</param>
    /// <returns>内层租约（Dispose 即释放）。</returns>
    public IDisposable AcquireExclusive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "AcquireExclusive");
        return _inner.AcquireExclusive(timeout);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ReleaseHangGates(); // ★ 拆除放行——被挂线程不跨 Dispose 阻塞
        _inner.Dispose();
    }

    /// <summary>句柄级注入包装——数据面每个操作入口评估规则。</summary>
    private sealed class FaultInjectingFileHandle(FaultInjectingFileSystem owner, IFileHandle inner, string path)
        : IFileHandle, IPoolAttachable
    {
        /// <summary>同步入口注入链（错误 → 延迟 → 挂起）。</summary>
        private void Inject(string operation) => owner.MaybeInject(path, operation);

        /// <summary>同步写族提交后评估腐败规则（静默翻转介质字节）。</summary>
        private void Corrupt(string operation) => owner.PostCorrupt(inner, path, operation);

        public string Path => inner.Path;
        public UnbufferedIoSupport UnbufferedSupport => inner.UnbufferedSupport;
        public long RequiredAlignment => inner.RequiredAlignment;

        /// <summary>注入链评估后转发内层写（提交后评估腐败规则）。</summary>
        /// <param name="offset">写入起始偏移（字节）。</param>
        /// <param name="source">源数据。</param>
        public void Write(long offset, ReadOnlySpan<byte> source)
        {
            Inject("Write");
            inner.Write(offset, source);
            Corrupt("Write");
        }

        /// <summary>注入链评估后转发内层异步写（乱序命中 = 延后提交；提交后评估腐败规则）。</summary>
        /// <param name="offset">写入起始偏移（字节）。</param>
        /// <param name="source">源数据。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>内层异步写结果。</returns>
        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
            => WriteAsyncCore(offset, source, ct);

        private async ValueTask WriteAsyncCore(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            await owner.MaybeInjectAsync(path, "Write", ct).ConfigureAwait(false);
            if (owner.TryDeferReorder(path, "Write", ct, () => inner.WriteAsync(offset, source, ct), out var deferred))
            {
                await deferred.ConfigureAwait(false);
                return;
            }
            await inner.WriteAsync(offset, source, ct).ConfigureAwait(false);
            Corrupt("Write");
        }

        /// <summary>注入评估后转发内层读。</summary>
        /// <param name="offset">读取起始偏移（字节）。</param>
        /// <param name="destination">接收缓冲。</param>
        /// <returns>内层实际读取的字节数。</returns>
        public int Read(long offset, Span<byte> destination)
        {
            Inject("Read");
            return inner.Read(offset, destination);
        }

        /// <summary>注入链评估后转发内层异步读。</summary>
        /// <param name="offset">读取起始偏移（字节）。</param>
        /// <param name="destination">接收缓冲。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>完成后得到内层实际读取的字节数。</returns>
        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
        {
            await owner.MaybeInjectAsync(path, "Read", ct).ConfigureAwait(false);
            return await inner.ReadAsync(offset, destination, ct).ConfigureAwait(false);
        }

        public long Position => inner.Position;

        /// <summary>注入链评估后转发内层追加（提交后评估腐败规则）。</summary>
        /// <param name="source">要追加的数据。</param>
        /// <returns>内层返回的预留起始偏移（字节）。</returns>
        public long Append(ReadOnlySpan<byte> source)
        {
            Inject("Append");
            var start = inner.Append(source);
            Corrupt("Append");
            return start;
        }

        /// <summary>注入链评估后转发内层异步追加（乱序命中 = 延后提交；提交后评估腐败规则）。</summary>
        /// <param name="source">要追加的数据。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>完成后得到内层返回的预留起始偏移（字节）。</returns>
        public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
            => AppendAsyncCore(source, ct);

        private async ValueTask<long> AppendAsyncCore(ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            await owner.MaybeInjectAsync(path, "Append", ct).ConfigureAwait(false);
            if (owner.TryDeferReorder(path, "Append", ct, () => inner.AppendAsync(source, ct), out var deferred))
                return await deferred.ConfigureAwait(false);
            var start = await inner.AppendAsync(source, ct).ConfigureAwait(false);
            Corrupt("Append");
            return start;
        }

        /// <summary>移动游标（直通内层——无注入点）。</summary>
        /// <param name="offset">相对基准的偏移（字节）。</param>
        /// <param name="origin">基准位置。</param>
        /// <returns>内层返回的绝对位置（字节）。</returns>
        public long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        /// <summary>注入评估后转发内层预分配。</summary>
        public void Preallocate()
        {
            Inject("Preallocate");
            inner.Preallocate();
        }

        public long Length => inner.Length;
        public long AllocatedSize => inner.AllocatedSize;

        /// <summary>注入评估后转发内层改长。</summary>
        /// <param name="length">目标长度（字节）。</param>
        public void SetLength(long length)
        {
            Inject("SetLength");
            inner.SetLength(length);
        }

        /// <summary>注入评估后转发内层打洞。</summary>
        /// <param name="offset">洞起始偏移（字节）。</param>
        /// <param name="length">洞长度（字节）。</param>
        public void PunchHole(long offset, long length)
        {
            Inject("PunchHole");
            inner.PunchHole(offset, length);
        }

        public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges() => inner.EnumerateAllocatedRanges();

        /// <summary>注入评估后转发内层折叠区间。</summary>
        /// <param name="offset">区间起始偏移（字节）。</param>
        /// <param name="length">区间长度（字节）。</param>
        public void CollapseRange(long offset, long length)
        {
            Inject("CollapseRange");
            inner.CollapseRange(offset, length);
        }

        /// <summary>注入评估后转发内层插入区间。</summary>
        /// <param name="offset">区间起始偏移（字节）。</param>
        /// <param name="length">区间长度（字节）。</param>
        public void InsertRange(long offset, long length)
        {
            Inject("InsertRange");
            inner.InsertRange(offset, length);
        }

        /// <summary>注入链评估后转发内层文件间拷贝（提交后评估腐败规则）。</summary>
        /// <param name="destination">目标句柄。</param>
        /// <param name="sourceOffset">源起始偏移（字节）。</param>
        /// <param name="destinationOffset">目标起始偏移（字节）。</param>
        /// <param name="length">计划拷贝字节数。</param>
        /// <returns>内层实际拷贝的字节数（字节）。</returns>
        public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
        {
            Inject("CopyRange");
            var copied = inner.CopyRange(destination, sourceOffset, destinationOffset, length);
            Corrupt("CopyRange");
            return copied;
        }

        /// <summary>注入链评估后转发内层整文件克隆（提交后评估腐败规则）。</summary>
        /// <param name="destination">目标句柄。</param>
        /// <returns>内层实际拷贝的字节数（字节）。</returns>
        public long CloneRange(IFileHandle destination)
        {
            Inject("CloneRange");
            var copied = inner.CloneRange(destination);
            Corrupt("CloneRange");
            return copied;
        }

        /// <summary>注入链评估后转发内层向量写（提交后评估腐败规则）。</summary>
        /// <param name="offset">写入起始偏移（字节）。</param>
        /// <param name="sources">写入片段序列。</param>
        public void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
        {
            Inject("WriteVector");
            inner.WriteVector(offset, sources);
            Corrupt("WriteVector");
        }

        /// <summary>注入链评估后转发内层向量异步写（乱序命中 = 延后提交；提交后评估腐败规则）。</summary>
        /// <param name="offset">写入起始偏移（字节）。</param>
        /// <param name="sources">写入片段序列。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>内层异步写结果。</returns>
        public ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct)
            => WriteVectorAsyncCore(offset, sources, ct);

        private async ValueTask WriteVectorAsyncCore(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources,
            CancellationToken ct)
        {
            await owner.MaybeInjectAsync(path, "WriteVector", ct).ConfigureAwait(false);
            if (owner.TryDeferReorder(path, "WriteVector", ct, () => inner.WriteVectorAsync(offset, sources, ct),
                    out var deferred))
            {
                await deferred.ConfigureAwait(false);
                return;
            }
            await inner.WriteVectorAsync(offset, sources, ct).ConfigureAwait(false);
            Corrupt("WriteVector");
        }

        /// <summary>注入评估后转发内层向量读。</summary>
        /// <param name="offset">读取起始偏移（字节）。</param>
        /// <param name="destinations">接收片段序列。</param>
        /// <returns>内层实际读取的总字节数（字节）。</returns>
        public int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations)
        {
            Inject("ReadVector");
            return inner.ReadVector(offset, destinations);
        }

        /// <summary>注入链评估后转发内层向量异步读。</summary>
        /// <param name="offset">读取起始偏移（字节）。</param>
        /// <param name="destinations">接收片段序列。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>完成后得到内层实际读取的总字节数（字节）。</returns>
        public async ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
        {
            await owner.MaybeInjectAsync(path, "ReadVector", ct).ConfigureAwait(false);
            return await inner.ReadVectorAsync(offset, destinations, ct).ConfigureAwait(false);
        }

        /// <summary>注入评估后转发内层刷盘（持久化点——注入可模拟持久化失败）。</summary>
        public void Flush()
        {
            Inject("Flush");
            inner.Flush();
        }

        /// <summary>注入评估后转发内层数据面刷盘。</summary>
        public void FlushData()
        {
            Inject("FlushData");
            inner.FlushData();
        }

        /// <summary>访问提示（直通内层——无注入点）。</summary>
        /// <param name="advise">访问提示。</param>
        public void Advise(FileAdvise advise) => inner.Advise(advise);

        /// <summary>注入评估后转发内层加锁。</summary>
        /// <param name="offset">锁区间起始偏移（字节）。</param>
        /// <param name="length">锁区间长度（字节）。</param>
        /// <param name="mode">锁模式。</param>
        public void Lock(long offset, long length, FileLockMode mode)
        {
            Inject("Lock");
            inner.Lock(offset, length, mode);
        }

        /// <summary>注入评估后转发内层尝试加锁。</summary>
        /// <param name="offset">锁区间起始偏移（字节）。</param>
        /// <param name="length">锁区间长度（字节）。</param>
        /// <param name="mode">锁模式。</param>
        /// <returns>内层判定结果（true = 获取成功）。</returns>
        public bool TryLock(long offset, long length, FileLockMode mode)
        {
            Inject("TryLock");
            return inner.TryLock(offset, length, mode);
        }

        /// <summary>解锁（直通内层——无注入点）。</summary>
        /// <param name="offset">锁区间起始偏移（字节）。</param>
        /// <param name="length">锁区间长度（字节）。</param>
        public void Unlock(long offset, long length) => inner.Unlock(offset, length);

        /// <summary>注入评估后转发内层映射。</summary>
        /// <param name="offset">映射起始偏移（字节）。</param>
        /// <param name="length">映射长度（字节）。</param>
        /// <param name="access">映射访问模式。</param>
        /// <returns>内层映射区段。</returns>
        public IMappedSection Map(long offset, long length, AccessMode access)
        {
            Inject("Map");
            return inner.Map(offset, length, access);
        }

        public ReadOnlyMemory<byte> FileExtra => inner.FileExtra;

        /// <summary>读取 FileExtra 片段（直通内层——无注入点）。</summary>
        /// <param name="offset">FileExtra 内起始偏移（字节）。</param>
        /// <param name="destination">接收缓冲。</param>
        /// <returns>内层实际读取的字节数。</returns>
        public int ReadFileExtra(long offset, Span<byte> destination) => inner.ReadFileExtra(offset, destination);

        /// <summary>按偏移写 FileExtra（直通内层——无注入点）。</summary>
        /// <param name="offset">FileExtra 内起始偏移（字节）。</param>
        /// <param name="data">写入数据。</param>
        public void WriteFileExtra(long offset, ReadOnlySpan<byte> data) => inner.WriteFileExtra(offset, data);

        /// <summary>整体替换 FileExtra（直通内层——无注入点）。</summary>
        /// <param name="extra">新 FileExtra 内容。</param>
        public void SetFileExtra(ReadOnlyMemory<byte> extra) => inner.SetFileExtra(extra);

        /// <summary>释放句柄（直通内层——Dispose 不触发注入）。</summary>
        public void Dispose() => inner.Dispose();

        /// <summary>异步释放句柄（直通内层）。</summary>
        /// <returns>内层异步释放结果。</returns>
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        // ═══ 池挂载协议转发 ═══
        // ★ 装饰器与 FileHandlePool 组合（引擎消费面必经）：池以本装饰器为池化对象，
        //   挂载/归还/真关闭整体转发内层——四介质句柄全实现 IPoolAttachable，内层必可转。
        //   （曾缺席：池 Acquire 强转 IPoolAttachable 直接 InvalidCastException——任何句柄装饰器
        //   与池不组合，故障注入测试首跑即炸实锤。）
        HandlePoolAttachment? IPoolAttachable.PoolAttachment => ((IPoolAttachable)inner).PoolAttachment;

        HandlePoolAttachment IPoolAttachable.AttachPool(FileHandlePool pool)
            => ((IPoolAttachable)inner).AttachPool(pool);

        void IPoolAttachable.CloseUnderlying() => ((IPoolAttachable)inner).CloseUnderlying();
    }
}
