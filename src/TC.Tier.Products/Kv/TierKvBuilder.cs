using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Products.Kv;

/// <summary>
/// TierKv 装配器（TierWalBuilder 同构：注入面开放 + StartAsync 一步到位）。
/// <para>★ 装配两层（D7 同构）：显式层 = 本 Builder（Ring/索引工厂 + formatter/comparer/resolver 注入，
/// 显式胜出）；缺省层 = <c>[KvStore]</c> 生成的封闭形态（formatter 零代码 + 内嵌派生 Ring/索引实现
/// 自动装配）——两条路共用 <see cref="TierKvAssembly"/> 的 Options→Settings 翻译面。</para>
/// <para>★ 用法：</para>
/// <code>
///   var kv = await new TierKvBuilder&lt;long, long&gt;(fs, options)
///       .WithRingFactory((fso, o) =&gt; new RingOfLong(TierKvAssembly.RingSettings(fso, o), fso))
///       .WithIndexFactory((fso, o, ring) =&gt; new HashOfLong(fso, TierKvAssembly.HashSettings(fso, o), ring))
///       .StartAsync();
/// </code>
/// </summary>
/// <typeparam name="TKey">键类型（unmanaged + IEquatable）。</typeparam>
/// <typeparam name="TValue">值类型（无约束——formatter 承载格式化）。</typeparam>
public sealed class TierKvBuilder<TKey, TValue> : IDisposable, IAsyncDisposable
    where TKey : unmanaged, IEquatable<TKey>
{
    private readonly IFileSystem _fs;
    private readonly TierKvOptions _options;
    private readonly IValueFormatter<TValue> _formatter;
    private ILogger? _logger;

    private Func<IFileSystem, TierKvOptions, RingBase<TKey>>? _ringFactory;
    private Func<IFileSystem, TierKvOptions, RingBase<TKey>, IIndex<TKey>>? _indexFactory;
    private IKeyComparer<TKey>? _keyComparer;
    private IKeyResolver<TKey>? _keyResolver;

    /// <summary>
    /// 构造（fs + options；formatter 缺省 <see cref="ValueFormatters.Auto{TValue}"/> 自动解析——
    /// byte/byte[]/ROM&lt;byte&gt;/unmanaged 值零声明；显式注入胜出）。
    /// </summary>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <param name="formatter">值格式化器（缺省 Auto 自动解析；显式注入胜出）。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    public TierKvBuilder(IFileSystem fs, TierKvOptions options, IValueFormatter<TValue>? formatter = null,
        ILogger? logger = null)
    {
        _fs = fs;
        _options = options;
        _formatter = formatter ?? ValueFormatters.Auto<TValue>();
        _logger = logger;
    }

    /// <summary>With 链——Ring 数据引擎工厂（Settings 构建经 <see cref="TierKvAssembly.RingSettings"/>）。</summary>
    /// <param name="factory">Ring 工厂委托（fs + options → RingBase&lt;TKey&gt;）。</param>
    /// <returns>本构建器（链式）。</returns>
    public TierKvBuilder<TKey, TValue> WithRingFactory(
        Func<IFileSystem, TierKvOptions, RingBase<TKey>> factory)
    {
        _ringFactory = factory;
        return this;
    }

    /// <summary>With 链——主索引工厂（探测族/比较族任一实例——<see cref="TierKv{TKey, TValue}"/> ctor 按族闸门；
    /// resolver 缺省应挂 Ring 本体，探测族 tag 判等闭环回读数据面）。</summary>
    /// <param name="factory">索引工厂委托（fs + options + ring → IIndex&lt;TKey&gt;）。</param>
    /// <returns>本构建器（链式）。</returns>
    public TierKvBuilder<TKey, TValue> WithIndexFactory(
        Func<IFileSystem, TierKvOptions, RingBase<TKey>, IIndex<TKey>> factory)
    {
        _indexFactory = factory;
        return this;
    }

    /// <summary>With 链——键比较器（判等/哈希——缺省 KeyComparer&lt;TKey&gt; 全字段）。</summary>
    /// <param name="comparer">键比较器（判等/哈希）。</param>
    /// <returns>本构建器（链式）。</returns>
    public TierKvBuilder<TKey, TValue> WithKeyComparer(IKeyComparer<TKey> comparer)
    {
        _keyComparer = comparer;
        return this;
    }

    /// <summary>With 链——键解析器（tag 判等闭环回读数据面——缺省挂 Ring 本体，探测族硬依赖）。</summary>
    /// <param name="resolver">键解析器（tag 判等闭环回读数据面）。</param>
    /// <returns>本构建器（链式）。</returns>
    public TierKvBuilder<TKey, TValue> WithKeyResolver(IKeyResolver<TKey> resolver)
    {
        _keyResolver = resolver;
        return this;
    }

    /// <summary>With 链——日志。</summary>
    /// <param name="logger">日志。</param>
    /// <returns>本构建器（链式）。</returns>
    public TierKvBuilder<TKey, TValue> WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>启动（构造 + Initialize + WaitForReady 一步到位——TierWalBuilder.StartAsync 同构）。</summary>
    /// <param name="hints">恢复提示（重放窗口）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已就绪的 TierKv 实例。</returns>
    /// <exception cref="InvalidOperationException">Ring 工厂或索引工厂未注入。</exception>
    public async Task<TierKv<TKey, TValue>> StartAsync(KvRecoveryHints hints = default,
        CancellationToken ct = default)
    {
        if (_ringFactory is null)
            throw new InvalidOperationException(
                "Ring 工厂未注入——WithRingFactory（[KvStore] 生成的封闭形态自动装配，Settings 经 TierKvAssembly）");
        if (_indexFactory is null)
            throw new InvalidOperationException(
                "索引工厂未注入——WithIndexFactory（[KvStore] 生成的封闭形态自动装配，Settings 经 TierKvAssembly）");

        var ring = _ringFactory(_fs, _options);
        var index = _indexFactory(_fs, _options, ring);
        var rangeIndex = _options.EnableRangeIndex
            ? TierKvAssembly.CreateRangeIndex(_fs, _options, ring)
            : null;

        var kv = new TierKv<TKey, TValue>(ring, index, _options, _formatter, _logger, rangeIndex);
        kv.Initialize(hints);
        await kv.WaitForReadyAsync(ct).ConfigureAwait(false);
        return kv;
    }

    /// <summary>释放（未 Start 的装配态无资源；已 Start 实例的生命周期归 TierKv 自身）。</summary>
    public void Dispose()
    {
    }

    /// <summary>异步释放。</summary>
    /// <returns>完成的 ValueTask。</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
