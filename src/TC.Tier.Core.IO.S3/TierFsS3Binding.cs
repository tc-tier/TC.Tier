using TC.Tier.CodeGen;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.Logging;

namespace TC.Tier.Core.IO.S3;

/// <summary>
/// s3 协议构建器（medium-protocol-and-parity-design §2.2 两级注册表的二级开放轴）——
/// spec <c>network:///s3/host[:port]/bucket/prefix</c> → S3ObjectStore + RemoteFileSystem 全栈组装。
/// <para>★ 依赖方向合法：IO.S3 → Core（注册表在 Core，实现在此——引用本程序集即自动注册，
///   ModuleInitializer 挂载，消费方零代码）。</para>
/// <para>★ spec 映射：endpoint=host[:port]（tls 缺省 https，tls=0 → http——本地 MinIO 类）；
///   vhost/region → S3ClientOptions；prefix → KeyPrefix；spill=local:///…→Spill.ToDisk、
///   spill=memory:→Spill.ToMemory（G7 收编：单一概念两形态）；cred=env:NAME → 环境变量 NAME
///   （值格式 <c>accessKey:secretKey</c>——引用永不携值）。</para>
/// </summary>
[NetworkProtocol("s3")]   // P-c：注册生成（TierFsGenerator 发射 ModuleInitializer——手写件退役）
public sealed class S3ProtocolBuilder : ITierProtocolBuilder
{
    /// <summary>单例（兼容既有引用；注册生成走无参构造）。</summary>
    public static readonly S3ProtocolBuilder Instance = new();

    public S3ProtocolBuilder() { }

    public IFileSystem Build(TierSpec spec, FileSystemOptions? options, TierFsVerb verb, ILogger? logger)
    {

        if (spec.Members.Count > 0)
            throw new NotSupportedException("member（多载体清单）仅 virtual。");

        var store = S3ObjectStore.Create(new S3ClientOptions
        {
            Endpoint = (spec.Tls ? "https://" : "http://") + spec.Endpoint,
            Bucket = spec.Bucket!,
            Region = spec.Region ?? "us-east-1",
            Credentials = ResolveCredentials(spec.CredentialRef),
            UseVirtualHostAddressing = spec.VirtualHostAddressing,
        });

        var o = options as RemoteFileSystemOptions ?? new RemoteFileSystemOptions();
        var merged = new RemoteFileSystemOptions
        {
            // 介质调优：只住 options（§4.2——字符串不承载调优）
            PartSize = o.PartSize,
            MultipartThreshold = o.MultipartThreshold,
            MaxParts = o.MaxParts,
            MaxConcurrency = o.MaxConcurrency,
            StagingPageSize = o.StagingPageSize,
            StagingMemoryLimit = o.StagingMemoryLimit,
            ReadCacheBytes = o.ReadCacheBytes,
            PrefetchPages = o.PrefetchPages,
            LeaseTimeout = o.LeaseTimeout,
            HeartbeatInterval = o.HeartbeatInterval,
            OrphanUploadCleanup = o.OrphanUploadCleanup,
            // 位置/挂载：spec 显式胜出 → options 同名值（审计时字符串必须可信）
            KeyPrefix = string.IsNullOrEmpty(spec.KeyPrefix) ? o.KeyPrefix : spec.KeyPrefix,
            Access = spec.Access != AccessMode.ReadWrite ? spec.Access : o.Access,
            Label = spec.Label ?? o.Label,
            QuotaBytes = spec.QuotaBytes != -1 ? spec.QuotaBytes : o.QuotaBytes,
            Exclusive = spec.Exclusive || o.Exclusive,
            SubKind = "s3",              // G4：VolumeInfo.SubKind 观测（协议身份）
        };
        if (verb == TierFsVerb.Open && spec.Label is not null)
            merged = new RemoteFileSystemOptions   // Open + label：断言语义由 RemoteFileSystem.Open 单点执法
            {
                KeyPrefix = merged.KeyPrefix,
                Label = spec.Label,
            };
        else if (verb == TierFsVerb.New && spec.Label is not null)
            merged = new RemoteFileSystemOptions   // New + label：设置（标记对象写入）
            {
                KeyPrefix = merged.KeyPrefix,
                Label = spec.Label,
            };
        if (spec.Spill is not null)
        {
            // spill 仅携带位置——嵌套挂载参数不支持（spill 是中转位置，非嵌套挂载）
            if (spec.Spill.Label is not null || spec.Spill.QuotaBytes != -1
                || spec.Spill.Access != AccessMode.ReadWrite || spec.Spill.Exclusive
                || spec.Spill.Members.Count > 0)
                throw new NotSupportedException("spill 仅携带位置（嵌套 spec 的挂载参数不支持——spill 是中转位置，非嵌套挂载）。");
            merged = spec.Spill.Nature == StorageNature.Local
                ? new RemoteFileSystemOptions
                {
                    KeyPrefix = merged.KeyPrefix,
                    Spill = RemoteSpill.ToDisk(spec.Spill.UncHost is not null
                        ? $@"\\{spec.Spill.UncHost}{spec.Spill.UncPath}"
                        : (spec.Spill.IsCwdRoot ? Environment.CurrentDirectory : spec.Spill.AbsolutePath!)),
                }
                : new RemoteFileSystemOptions { KeyPrefix = merged.KeyPrefix, Spill = RemoteSpill.ToMemory() };
        }

        // 动词路由（P2 收尾）：New = 前缀有内容即抛 AlreadyExists（枚举检查）/ Open = 既有视图 + label 断言
        // / OpenOrCreate = bind-any 纯构造（零探测——label 缺省写入/不符抛）
        return verb switch
        {
            TierFsVerb.New => RemoteFileSystem.New(store, merged, logger),
            TierFsVerb.OpenOrCreate => RemoteFileSystem.OpenOrCreate(store, merged, logger),
            _ => RemoteFileSystem.Open(store, merged, logger),
        };
    }

    /// <summary>env:NAME → 环境变量 NAME（值格式 accessKey:secretKey）——缺失/畸形即抛（fail-fast）。</summary>
    private static StaticCredentials ResolveCredentials(string? credentialRef)   // CA1859：返回具体形（唯一构造产物）
    {
        if (credentialRef is null)
            throw new NotSupportedException(
                "network:///s3 未携带 cred=env:NAME——凭证必须为引用（构造期解析，永不携值）。" +
                "环境变量值格式：accessKey:secretKey。");
        var name = credentialRef["env:".Length..];
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
            throw new NotSupportedException($"凭证环境变量 '{name}' 未设置（cred={credentialRef}）。");
        var sep = value.IndexOf(':');
        if (sep <= 0 || sep == value.Length - 1)
            throw new NotSupportedException($"凭证环境变量 '{name}' 值格式须为 accessKey:secretKey。");
        return new StaticCredentials(value[..sep], value[(sep + 1)..]);
    }

    private static void NotYet(string what, string phase)
        => throw new NotSupportedException(
            $"spec 参数已解析校验但介质能力尚未落地：{what}——{phase}。当前为 P1 工厂骨架：" +
            "参数不静默忽略，落地后自动生效、无需改 spec。");
}

