namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 存储引擎构建者（builder.build——设计决策：Options 构建配置 → Builder 构建引擎 →
/// Start 启动引擎一步到位）。
/// <para>★ 外部使用者的唯一引擎入口：<c>using var builder = options.Builder(fs); var dev = builder.Start(hints)</c>
///   或 <c>StartAsync(hints)</c>——构建 + Initialize + 就绪等待一体；实现类（StorageEngine）internal
///   不对外，<b>不允许外部直接调 Initialize</b>（启动统一经本类的 Start/StartAsync）。</para>
/// <para>★ <b>启动状态机</b>：Start/StartAsync 只能成功启动一次（重复启动抛
///   <see cref="InvalidOperationException"/>）；<b>失败可重试</b>——启动失败销毁重建引擎实例
///   （对齐 RecoveryBase"Failed 销毁重建"哲学），再次 Start 用新引擎。</para>
/// <para>★ 装配依赖（compact/checkpoint/logger/hub/epoch）在 <c>Options.Builder(...)</c> 传入——
///   构建上下文与配置（Options）分离。</para>
/// </summary>
public sealed class StorageEngineBuilder : IDisposable, IAsyncDisposable
{
    private StorageEngine? _engine; // 非 readonly——启动失败销毁重建
    private bool _ownershipTransferred;
    private int _started;  // 启动状态机：0=未启动, 1=已启动/启动中（CAS 抢启动权——并发安全）
    private readonly IFileSystem _root;
    private readonly StorageEngineOptions _options;
    private readonly ICompact? _compact;
    private readonly ICheckpoint? _checkpoint;
    private readonly ILogger? _logger;
    private readonly ObservabilityHub? _hub;
    private readonly LightEpoch? _epoch;

    internal StorageEngineBuilder(IFileSystem root, StorageEngineOptions options, ICompact? compact = null,
        ICheckpoint? checkpoint = null, ILogger? logger = null, ObservabilityHub? hub = null,
        LightEpoch? epoch = null)
    {
        _root = root;
        _options = options;
        _compact = compact;
        _checkpoint = checkpoint;
        _logger = logger;
        _hub = hub;
        _epoch = epoch;
        _engine = new StorageEngine(root, options, compact, checkpoint, logger, hub, epoch);
    }

    /// <summary>
    /// 内部引擎实例（同程序集/InternalsVisibleTo 白盒访问——测试诊断；外部一律经 <see cref="Start"/> 返回的接口）。
    /// <para>★ 启动前可经此做构建后配置（如测试注入诊断开关）；启动失败重建后指向新实例。</para>
    /// </summary>
    internal StorageEngine Engine => _engine!;

    /// <summary>
    /// 同步启动，返回就绪引擎。只能成功启动一次；失败可重试（销毁重建）。
    /// <para>⚠️ <b>禁止在同步上下文调用</b>（UI/ASP.NET 等同步上下文下，同步阻塞后台 Task 会经典死锁）——
    ///   异步调用方用 <see cref="StartAsync"/>。</para>
    /// </summary>
    /// <param name="hints">恢复 hints（注入已知水位）；default 让引擎自扫。</param>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public IStorageEngine Start(EngineRecoveryHints hints = default)
    {
        // ★ CAS 抢启动权（并发安全）：首个线程 0→1 成功；并发第二个 CAS 失败即抛——
        //   "只能启动一次"语义在并发下成立（启动中=不可并发启动）
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("StorageEngineBuilder 只能成功启动一次（Start/StartAsync）——已启动或正在启动");
        var engine = _engine!;
        try
        {
            engine.Initialize(hints);
            engine.WaitForReady();
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新引擎实例）
            _logger?.LogWarning(ex, "StorageEngineBuilder.Start 启动失败——销毁重建引擎（可重试 Start/StartAsync）");
            try
            {
                engine.Dispose();
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "StorageEngineBuilder.Start: 启动失败后销毁旧引擎 Dispose 异常");
            }
            _engine = new StorageEngine(_root, _options, _compact, _checkpoint, _logger, _hub, _epoch);
            Volatile.Write(ref _started, 0);   // ★ 失败复位（重建完成后）——允许重试 Start/StartAsync
            throw;
        }

        _ownershipTransferred = true; // 标记所有权已转移
        return engine;
    }

    /// <summary>
    /// 异步启动（推荐形态），返回就绪引擎。只能成功启动一次；失败可重试（销毁重建）。
    /// </summary>
    /// <param name="hints">恢复 hints（注入已知水位）；default 让引擎自扫。</param>
    /// <exception cref="InvalidOperationException">已成功启动过。</exception>
    public async ValueTask<IStorageEngine> StartAsync(EngineRecoveryHints hints = default)
    {
        // ★ CAS 抢启动权（同 Start——并发安全，启动中=不可并发启动）
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("StorageEngineBuilder 只能成功启动一次（Start/StartAsync）——已启动或正在启动");
        var engine = _engine!;
        try
        {
            engine.Initialize(hints);
            await engine.WaitForReadyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ★ 失败可重试：销毁重建（Failed 哲学——重试 = 新引擎实例）
            _logger?.LogWarning(ex, "StorageEngineBuilder.StartAsync 启动失败——销毁重建引擎（可重试 Start/StartAsync）");
            try
            {
                await engine.DisposeAsync();
            }
            catch (Exception disposeEx)
            {
                _logger?.LogWarning(disposeEx, "StorageEngineBuilder.StartAsync: 启动失败后销毁旧引擎 DisposeAsync 异常");
            }

            _engine = new StorageEngine(_root, _options, _compact, _checkpoint, _logger, _hub, _epoch);
            Volatile.Write(ref _started, 0);   // ★ 失败复位（重建完成后）——允许重试 Start/StartAsync
            throw;
        }

        _ownershipTransferred = true; // 标记所有权已转移
        return engine;
    }

    public void Dispose()
    {
        // 只有所有权没转移时，才由 Builder 释放
        if (!_ownershipTransferred)
            _engine?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return _ownershipTransferred || _engine is null
            ? ValueTask.CompletedTask
            : _engine.DisposeAsync();
    }
}