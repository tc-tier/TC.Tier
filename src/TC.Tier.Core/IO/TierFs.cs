using System.Collections.Concurrent;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.IO;

/// <summary>
/// TierFs——spec 工厂（medium-protocol-and-parity-design §2.2）："打开一个存储镜像"的统一入口。
/// <para>★ 两级注册表：顶层 = 四本性根（封闭——本类内路由）；二级 = network 协议表（开放注册）。</para>
/// <para>★ 工厂是薄壳：解析 spec → 委托各介质类型化入口（P2 各介质落位 New/Open 后重新对靶）。</para>
/// <para>★ 骨架纪律：spec 参数全量解析校验；介质尚未落地的参数**显式抛 NotSupportedException（带
///   阶段号）**——绝不静默忽略（静默忽略 = 配置写了没生效，比报错恶劣）。</para>
/// </summary>
public static partial class TierFs
{
    /// <summary>
    /// network 协议注册表（开放轴）——第三方 <c>IObjectStore</c> 实现注册
    /// </summary>
    private static readonly ConcurrentDictionary<string, ITierProtocolBuilder> SProtocols = new(StringComparer.Ordinal);

    /// <summary>
    /// 注册 network 协议构建器（二级注册表的开放轴）——第三方 <c>IObjectStore</c> 实现注册
    /// </summary>
    /// <param name="protocol"></param>
    /// <param name="builder"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public static void RegisterProtocol(string protocol, ITierProtocolBuilder builder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(builder);
        SProtocols[protocol] = builder;
    }

    /// <summary>
    /// 创建空镜像（spec 语法见设计 §2.1；纯 spec——调优全走类型缺省）。
    /// </summary>
    /// <param name="spec">镜像的规格字符串</param>
    /// <param name="logger">日志记录器</param>
    /// <returns>创建的文件系统实例</returns>
    public static IFileSystem New(string spec, ILogger? logger = null)
        => Build(TierSpec.Parse(spec), null, TierFsVerb.New, logger);

    /// <summary>
    /// 打开既有镜像（spec 语法见设计 §2.1；纯 spec——调优全走类型缺省）。
    /// </summary>
    /// <param name="spec">镜像的规格字符串</param>
    /// <param name="logger">日志记录器</param>
    /// <returns>创建的文件系统实例</returns>
    public static IFileSystem Open(string spec, ILogger? logger = null)
        => Build(TierSpec.Parse(spec), null, TierFsVerb.Open, logger);

    /// <summary>
    /// 创建空镜像——**spec 定身份 + options 补调优的合流点**。
    /// <para>★ 优先级（防腐纪律的裁决规则）：<b>spec 显式（非缺省值）胜出 → options 同名值 → 类型缺省</b>——spec 是自包含的部署声明（可序列化、可审计），字符串必须可信；要改声明就改 spec，不在 options 里偷改。</para>
    /// <para>★ options 的介质调优字段（PartSize/PageSize/MetadataMode…）全量采用——调优只住 options（§4.2）。</para>
    /// </summary>
    /// <param name="spec">镜像的规格字符串</param>
    /// <param name="options">文件系统的选项</param>
    /// <param name="logger">日志记录器</param>
    /// <returns>创建或打开的文件系统实例</returns>
    public static IFileSystem New(string spec, FileSystemOptions options, ILogger? logger = null)
        => Build(TierSpec.Parse(spec), options, TierFsVerb.New, logger);

    /// <summary>
    /// 懒初始化糖（bind-any 终态——设计 §2.3）：不存在/未格式化则建，存在则开。显式表达"我接受两种状态"。
    /// </summary>
    public static IFileSystem OpenOrCreate(string spec, ILogger? logger = null)
        => Build(TierSpec.Parse(spec), null, TierFsVerb.OpenOrCreate, logger);

    /// <summary>
    /// 懒初始化糖——**spec 定身份 + options 补调优的合流点**（New/Open 同款优先级）。
    /// </summary>
    public static IFileSystem OpenOrCreate(string spec, FileSystemOptions options, ILogger? logger = null)
        => Build(TierSpec.Parse(spec), options, TierFsVerb.OpenOrCreate, logger);

    /// <summary>
    /// 打开既有镜像——**spec 定身份 + options 补调优的合流点**。
    /// <para>★ 优先级（防腐纪律的裁决规则）：<b>spec 显式（非缺省值）胜出 → options 同名值 → 类型缺省</b>——spec 是自包含的部署声明（可序列化、可审计），字符串必须可信；要改声明就改 spec，不在 options 里偷改。</para>
    /// <para>★ options 的介质调优字段（PartSize/PageSize/MetadataMode…）全量采用——调优只住 options（§4.2）。</para>
    /// </summary>
    /// <param name="spec">镜像的规格字符串</param>
    /// <param name="options">文件系统的选项</param>
    /// <param name="logger">日志记录器</param>
    /// <returns>创建或打开的文件系统实例</returns>
    public static IFileSystem Open(string spec, FileSystemOptions options, ILogger? logger = null)
        => Build(TierSpec.Parse(spec), options, TierFsVerb.Open, logger);

    private static IFileSystem Build(TierSpec s, FileSystemOptions? options, TierFsVerb verb, ILogger? logger,
        StorageNature? expectNature = null) =>
        Build(s, options, verb, logger, expectNature, expectedOptionsName: null);

    /// <summary>生成重载的共核入口（P-a）：类型化重载带入期望本性——字符串本性 × 重载期望一致性
    /// （编译期管不到字符串内容；错配错误指明"该重载期望 X，字符串是 Y"）。</summary>
    internal static IFileSystem Build(TierSpec s, FileSystemOptions? options, TierFsVerb verb, ILogger? logger,
        StorageNature expectNature, string expectedOptionsName) =>
        Build(s, options, verb, logger, (StorageNature?)expectNature, expectedOptionsName);

    private static IFileSystem Build(TierSpec s, FileSystemOptions? options, TierFsVerb verb, ILogger? logger,
        StorageNature? expectNature, string? expectedOptionsName)
    {
        if (expectNature is { } expected && s.Nature != expected)
            throw new ArgumentException(
                $"spec 是 {s.Nature}（{NatureHead(s.Nature)}），本重载期望 {expected}（{NatureHead(expected)}）" +
                (expectedOptionsName is null ? "" : $"（options {expectedOptionsName}）") + "——options 类型与 spec 本性错配。");
        return s.Nature switch
        {
            StorageNature.Local => BuildLocal(s, Expect<DiskFileSystemOptions>(options, s, "DiskFileSystemOptions"),
                verb, logger),
            StorageNature.Memory => BuildMemory(s,
                Expect<MemoryFileSystemOptions>(options, s, "MemoryFileSystemOptions"), verb, logger),
            StorageNature.Virtual => verb == TierFsVerb.New
                ? BuildVirtual(s, Expect<TierVolumeFormatOptions>(options, s, "TierVolumeFormatOptions（New）"), verb, logger)
                : BuildVirtual(s, Expect<TierVolumeOpenOptions>(options, s, "TierVolumeOpenOptions（Open）"), verb, logger),
            StorageNature.Network => BuildNetwork(s, options, verb, logger),
            _ => throw new InvalidOperationException($"未知的介质本性：{s.Nature}"),
        };
    }

    // ═══════════════ local（本地文件系统）═══════════════

    private static DiskFileSystem BuildLocal(TierSpec s, DiskFileSystemOptions? user, TierFsVerb verb, ILogger? logger)
    {
        var root = ResolveLocalRoot(s);
        var o = user ?? new DiskFileSystemOptions();
        var mount = MergeMount(s, o);
        var options = new DiskFileSystemOptions
        {
            MetadataMode = o.MetadataMode, // 调优：只住 options
            Preallocation = o.Preallocation, // IS-04 轴：只住 options
            Access = mount.Access, // G2 包络
            Label = mount.Label, // G1：New = 写标记 / Open = 校验
            QuotaBytes = mount.QuotaBytes, // G3：-1 = 不设（零成本）
            Exclusive = mount.Exclusive, // G5：锁文件构造期获取
        };
        return verb switch
        {
            TierFsVerb.New => DiskFileSystem.New(root, options, logger),
            TierFsVerb.OpenOrCreate => DiskFileSystem.OpenOrCreate(root, options, logger),
            _ => DiskFileSystem.Open(root, options, logger),
        };
    }

    /// <summary>local 位置落定：快捷/相对形态在此对 CWD 解析并固化（解析层保持原样——设计 §2.1）。</summary>
    private static string ResolveLocalRoot(TierSpec s)
        => s.IsCwdRoot ? Environment.CurrentDirectory
            : s.RelativePath is not null ? Path.GetFullPath(s.RelativePath) // CWD 固化：构造瞬间钉死
            : s.UncHost is not null ? @$"\\{s.UncHost}{s.UncPath}"
            : s.AbsolutePath!;

    // ═══════════════ memory（内存文件系统）═══════════════

    private static MemoryFileSystem BuildMemory(TierSpec s, MemoryFileSystemOptions? user, TierFsVerb verb,
        ILogger? logger)
    {
        // New/Open/OpenOrCreate 同形：内存无存在性概念，恒成功（设计 §2.3）；四个挂载参数全生效
        _ = verb;
        var o = user ?? new MemoryFileSystemOptions();
        var mount = MergeMount(s, o);
        return MemoryFileSystem.New(new MemoryFileSystemOptions
        {
            Allocation = o.Allocation, // 调优：只住 options
            PageSize = o.PageSize, // 调优：只住 options
            Access = mount.Access,
            QuotaBytes = mount.QuotaBytes,
            Label = mount.Label,
            Exclusive = mount.Exclusive,
        }, logger);
    }

    // ═══════════════ virtual（虚拟文件系统）═══════════════

    private static TierVolumeFs BuildVirtual(TierSpec s, FileSystemOptions? user, TierFsVerb verb, ILogger? logger)
    {
        // exclusive：虚拟文件系统构造即排他（内建）——概念已满足，无需动作
        var carrier = s.SubKind == "dev"
            ? TierVolumeCarrier.Device(s.AbsolutePath!)
            : TierVolumeCarrier.File(s.AbsolutePath!);
        TierVolumeCarrier?[] carriers = s.Members.Count == 0
            ? [carrier]
            : new[] { carrier }.Concat(s.Members.Select(TierVolumeCarrier.File)).ToArray();
        var mount = MergeMount(s, user ?? new TierVolumeOpenOptions());

        if (verb == TierFsVerb.New)
        {
            if (mount.QuotaBytes == -1 && s.Members.Count > 0)
                throw new NotSupportedException(
                    "virtual New 多载体（member=）须显式 quota=供给——自动扩容仅限单文件载体（多载体扩容 = AddCarrier 显式路径）。");
            var fmt = user as TierVolumeFormatOptions;
            var fs = TierVolumeFs.New(carrier, new TierVolumeFormatOptions
            {
                BlockSize = fmt?.BlockSize ?? 4096, // 调优：只住 options
                JournalReserveBytes = fmt?.JournalReserveBytes ?? 8L << 20, // 调优：只住 options
                Preallocation = fmt?.Preallocation ?? PreallocationMode.Metadata, // IS-02 载体档
                CarrierWriteThrough = fmt?.CarrierWriteThrough ?? false, // IS-03 载体档
                WriteConcurrency = fmt?.WriteConcurrency ?? WriteConcurrencyMode.Serial, // V2 §2.1 写并发档
                QuotaBytes = mount.QuotaBytes, // 一词制：供给 = 基类 QuotaBytes（正数 = New 时刻物化位图；-1 = 自动扩容卷）
                Label = mount.Label,
            }, logger);
            // New + access=ro = 建完即封存：格式化 → 关卷提交 → 只读重开（§2.5）
            if (mount.Access != AccessMode.Read) return fs;
            fs.Dispose();
            return TierVolumeFs.Open(carrier, new TierVolumeOpenOptions
            {
                Access = AccessMode.Read,
                Preallocation = fmt?.Preallocation ?? PreallocationMode.Metadata, // 与格式化档一致（full 载体免被稀疏标记）
                CarrierWriteThrough = fmt?.CarrierWriteThrough ?? false,
                WriteConcurrency = fmt?.WriteConcurrency ?? WriteConcurrencyMode.Serial, // V2 §2.1 写并发档
            }, logger);
        }

        var open = user as TierVolumeOpenOptions;
        // Open：quota = 挂载收紧（min(quota, 供给)——分配咽喉执法）；label = 校验（不符即抛）
        // OpenOrCreate：已格式化 → Open（label 断言）；未格式化/不存在 → New 回退（上方 New 分支同款参数）
        TierVolumeFs OpenIt() => TierVolumeFs.Open(carriers, new TierVolumeOpenOptions
        {
            PageCacheBytes = open?.PageCacheBytes ?? 64L << 20, // 调优：只住 options
            AllowDegraded = open?.AllowDegraded ?? false, // 策略：只住 options
            Preallocation = open?.Preallocation ?? PreallocationMode.Metadata, // IS-02 载体档
            CarrierWriteThrough = open?.CarrierWriteThrough ?? false, // IS-03 载体档
            WriteConcurrency = open?.WriteConcurrency ?? WriteConcurrencyMode.Serial, // V2 §2.1 写并发档
            Access = mount.Access, // Read = 只读（dirty 降级形态同）；Write 在 TierVolumeFs.Open 入口即拒
            QuotaBytes = mount.QuotaBytes, // -1 = 不收紧（受供给物理约束）
            Label = mount.Label, // 非 null = 断言卷上 label
        }, logger);
        if (verb != TierFsVerb.OpenOrCreate)
            return OpenIt();
        try
        {
            return OpenIt();
        }
        catch (Exception ex) when (ex is FileIOException { Error: IOError.NotFound or IOError.IOFailure }
            or FileNotFoundException or DirectoryNotFoundException)
        {   // 未格式化/不存在 → 建（bind-any）；裸 FileNotFound = 文件载体本身不存在（OpenMemberCarrier 不包装）
            return TierVolumeFs.New(carrier, new TierVolumeFormatOptions   // 未格式化/不存在 → 建（bind-any）
            {
                BlockSize = 4096, JournalReserveBytes = 8L << 20,   // 格式缺省（懒刈糖不带格式期调优——显式格式用 New）
                QuotaBytes = mount.QuotaBytes,
                Label = mount.Label,
            }, logger);
        }
    }

    // ═══════════════ network（网络文件系统——二级协议注册表）═══════════════
    // ★ 注册契约（定稿——零反射纪律，NativeAOT 兼容）：[NetworkProtocol] 的
    //   ModuleInitializer 只在协议程序集被 CLR 实际加载时执行。程序集加载 = 触碰其中任一类型
    //   （如 `S3ProtocolBuilder.Instance`——ModuleInitializer 随程序集加载自动注册）。
    //   "引用即生效"的措辞收回（JIT 不会仅因 spec 字符串加载引用程序集——实测复现注册缺失）；
    //   也不做运行时反射扫描（README 零反射铁律——Assembly.LoadFrom/GetTypes/Activator 在
    //   NativeAOT 裁剪下全部失效）。消费方两种合规接法：① 触碰协议程序集任一类型（推荐——
    //   程序集加载即自动注册）；② 显式 `TierFs.RegisterProtocol(name, builder)`。

    private static IFileSystem BuildNetwork(TierSpec s, FileSystemOptions? options, TierFsVerb verb, ILogger? logger)
    {
        if (!SProtocols.TryGetValue(s.SubKind!, out var builder))
            throw new FileIOException(IOError.Unsupported,
                $"network 协议 '{s.SubKind}' 未注册——已注册：[{string.Join(", ", SProtocols.Keys.OrderBy(k => k, StringComparer.Ordinal))}]。" +
                "协议程序集未被加载（ModuleInitializer 随程序集加载注册）：请触碰协议程序集任一类型触发加载" +
                "（如 S3ProtocolBuilder.Instance），或显式 TierFs.RegisterProtocol(name, builder)。",
                null, $"network+{s.SubKind}");
        return builder.Build(s, options, verb, logger);
    }

    private static string SchemeHead(this TierSpec s) => NatureHead(s.Nature);

    private static string NatureHead(StorageNature nature) => nature switch
    {
        StorageNature.Local => "local://",
        StorageNature.Memory => "memory:",
        StorageNature.Virtual => "virtual://",
        StorageNature.Network => "network:///",
        _ => nature.ToString(),
    };

    /// <summary>options 类型与 spec 本性匹配检查（local 配 DiskFileSystemOptions 之类的错配 fail-fast）。</summary>
    private static TOptions? Expect<TOptions>(FileSystemOptions? options, TierSpec s, string expected)
        where TOptions : FileSystemOptions
        => options switch
        {
            null => null,
            TOptions typed => typed,
            _ => throw new ArgumentException(
                $"spec 是 {s.Nature}（{s.SchemeHead()}），options 须为 {expected}——收到 {options.GetType().Name}。"),
        };

    /// <summary>基类四成员合流（spec 显式胜出 → options → 类型缺省）。</summary>
    private static (AccessMode Access, string? Label, long QuotaBytes, bool Exclusive) MergeMount(TierSpec s,
        FileSystemOptions o)
        => (s.Access != AccessMode.ReadWrite ? s.Access : o.Access,
            s.Label ?? o.Label,
            s.QuotaBytes != -1 ? s.QuotaBytes : o.QuotaBytes,
            s.Exclusive || o.Exclusive);

    private static void NotYet(string what, string phase)
        => throw new NotSupportedException(
            $"spec 参数已解析校验但介质能力尚未落地：{what}——{phase}。当前为 P1 工厂骨架：" +
            "参数不静默忽略（配置写了没生效比报错恶劣），落地后自动生效、无需改 spec。");
}