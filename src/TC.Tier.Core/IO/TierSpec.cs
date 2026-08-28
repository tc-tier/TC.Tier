using System.Text;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.IO;

/// <summary>
/// spec 解析结果（medium-protocol-and-parity-design §2.1 方案甲）——构造协议的结构化形态。
/// <para>★ scheme 头 = 本性四类（local/memory/virtual/network——封闭）；二级分类 = path 首段
///   （virtual 的 <c>dev</c> 设备载体 / network 的协议名——开放注册键）。</para>
/// <para>★ local 路径域四形态：POSIX 绝对（<c>local:///var/…</c>）/ Windows 盘符（<c>\</c> 与 <c>/</c>
///   都收，解析时归一 <c>/</c>）/ UNC（<c>local://server/share</c>）/ 相对（<c>local:rel</c>——
///   CWD 固化由工厂负责，解析层保持原样）。</para>
/// <para>★ 快捷档：<c>local</c> ≡ <c>local:</c> = CWD 根；<c>memory</c> ≡ <c>memory:</c> = 缺省私有卷；
///   virtual/network 无裸形态（fail-fast——没有默认载体与默认端点）。</para>
/// <para>★ <see cref="ToString"/> 输出规范形——往返稳定：<c>Parse(Parse(s).ToString()).ToString()</c>
///   恒等（设计 §7.3 往返测试的锚点）。</para>
/// </summary>
public sealed record TierSpec
{
    /// <summary>介质本性（scheme 头的解析结果——四类封闭）。</summary>
    public StorageNature Nature { get; init; }

    /// <summary>二级分类（path 首段）：network → 协议名（"s3" 等，开放注册键）；virtual → "dev"（设备载体）或 null（文件载体，缺省）；local/memory → null。</summary>
    public string? SubKind { get; init; }

    // ═══════════════ 位置（按形态互斥）═══════════════

    /// <summary>绝对路径（local POSIX/Windows 盘符、virtual 文件·设备路径——反斜杠已归一为 /）。</summary>
    public string? AbsolutePath { get; init; }

    /// <summary>UNC 主机（local://server/share 形态）。</summary>
    public string? UncHost { get; init; }

    /// <summary>UNC 共享起始路径（含前导 /；含共享名首段）。</summary>
    public string? UncPath { get; init; }

    /// <summary>相对路径（local:rel 形态——CWD 解析与固化由工厂负责）。</summary>
    public string? RelativePath { get; init; }

    /// <summary>快捷 local（裸形态 / local: 空形态）= CWD 为根。</summary>
    public bool IsCwdRoot { get; init; }

    /// <summary>网络端点 host[:port]。</summary>
    public string? Endpoint { get; init; }

    /// <summary>桶名。</summary>
    public string? Bucket { get; init; }

    /// <summary>键前缀（可为空串；多引擎共桶隔离）。</summary>
    public string? KeyPrefix { get; init; }

    // ═══════════════ 挂载属性（设计 §2.5 参数表）═══════════════

    /// <summary>卷标签（≤32 UTF-8 字节；New = 设置 / Open = 校验）。</summary>
    [SpecParam]
    public string? Label { get; init; }

    /// <summary>空间根容量上限（-1 = 无上限，缺省；&gt;0 = 强制硬限——超限 DiskFull）。</summary>
    [SpecParam]
    public long QuotaBytes { get; init; } = -1;

    /// <summary>访问三态（缺省 ReadWrite；空间平面即总上包络）。</summary>
    [SpecParam]
    public AccessMode Access { get; init; } = AccessMode.ReadWrite;

    /// <summary>排他打开（四介质各自最优映射：锁文件/真锁/fencing/内建）。</summary>
    [SpecParam]
    public bool Exclusive { get; init; }

    /// <summary>spill 中转位置（仅 network；嵌套 spec——目标限 local/memory）。</summary>
    [SpecParam(Media = "network")]
    public TierSpec? Spill { get; init; }

    /// <summary>凭证引用（<c>env:NAME</c>——引用永不携值）。</summary>
    [SpecParam(Media = "network")]
    public string? CredentialRef { get; init; }

    /// <summary>区域（仅 network·s3）。</summary>
    [SpecParam(Media = "network")]
    public string? Region { get; init; }

    /// <summary>virtual-host 寻址（仅 network·s3——COS 端点必须）。</summary>
    [SpecParam(Media = "network")]
    public bool VirtualHostAddressing { get; init; }

    /// <summary>端点 TLS（缺省 true = https；false = http——本地 MinIO 类端点。spec 端点不带协议方案，此参数补齐方案位）。</summary>
    [SpecParam(Media = "network")]
    public bool Tls { get; init; } = true;

    /// <summary>多载体成员清单（仅 virtual；成员 0 = 主载体 = <see cref="AbsolutePath"/>，member 依次追加）。</summary>
    [SpecParam(Media = "virtual", Repeatable = true)]
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>解析 spec 字符串（失败抛 <see cref="FormatException"/>——fail-fast，含修正提示）。</summary>
    public static TierSpec Parse(string spec)
    {
        if (string.IsNullOrEmpty(spec)) throw Err("spec 为空。");

        // scheme 头：到首个 ':' 或 '?' 为止（快捷形态允许无 ':'）
        var i = 0;
        while (i < spec.Length && spec[i] != ':' && spec[i] != '?') i++;
        var scheme = spec[..i];
        var hasColon = i < spec.Length && spec[i] == ':';
        var rest = hasColon ? spec[(i + 1)..] : spec[i..];

        return scheme switch
        {
            "local" => ParseLocal(rest),
            "memory" => ParseMemory(rest),
            "virtual" => ParseVirtual(rest),
            "network" => ParseNetwork(rest),
            _ => throw Err($"未知 scheme 头 '{scheme}'——四本性：local / memory / virtual / network。"),
        };
    }

    // ═══════════════ 分介质解析 ═══════════════

    private static TierSpec ParseLocal(string rest)
    {
        var (pathPart, query) = SplitQuery(rest);

        // 快捷 / 空形态：CWD 为根（local 裸形态、local:、local?query）
        if (pathPart.Length == 0)
            return ApplyQuery(new TierSpec { Nature = StorageNature.Local, IsCwdRoot = true }, query);

        if (pathPart.StartsWith("//", StringComparison.Ordinal))
        {
            var body = NormalizeSeparators(pathPart[2..]);
            if (body.Length == 0)
                throw Err("local:// 后为空——绝对路径 / 盘符 / UNC 三选一。");

            // local:///…：前导 / 后若为盘符形态则按 Windows 绝对处理，否则 POSIX 绝对
            if (body[0] == '/')
            {
                // ★ 冗余前导斜杠归一（POSIX 等价语义）：local:////tmp/x ≡ local:///tmp/x——
                //   spill 等嵌套场景拼接绝对目录时天然产生四斜杠，翻译岗收口为单一前导 /。
                //   全斜杠（local:/// 等）仍 fail-fast——绝对路径不可为空（契约不变）。
                int i = 1;
                while (i < body.Length && body[i] == '/') i++;
                if (i >= body.Length)
                    throw Err("local:/// 后为空——绝对路径不可为空。");
                body = "/" + body[i..];
                var afterSlash = body[1..];
                if (afterSlash.Length == 0)
                    throw Err("local:/// 后为空——绝对路径不可为空。");
                return ApplyQuery(new TierSpec
                {
                    Nature = StorageNature.Local,
                    AbsolutePath = IsDrivePath(afterSlash) ? afterSlash : body,
                }, query);
            }
            if (IsDrivePath(body))
                return ApplyQuery(new TierSpec { Nature = StorageNature.Local, AbsolutePath = body }, query);

            // UNC：首段 = 主机，余 = 共享起始路径。双斜杠后单段（非盘符非主机/共享）为消歧保留——报错。
            var slash = body.IndexOf('/');
            if (slash <= 0 || body[(slash + 1)..].Length == 0)
                throw Err(
                    $"local:// 后非盘符亦非合法 UNC（'{pathPart}'）——本地目录用 local:///绝对、local:相对" +
                    " 或 local://server/share；双斜杠后单段为消歧保留形态，直接报错。");
            return ApplyQuery(new TierSpec
            {
                Nature = StorageNature.Local,
                UncHost = body[..slash],
                UncPath = "/" + body[(slash + 1)..],
            }, query);
        }

        // 相对形态（local:rel——CWD 固化由工厂负责）
        return ApplyQuery(new TierSpec
        {
            Nature = StorageNature.Local,
            RelativePath = NormalizeSeparators(pathPart),
        }, query);
    }

    private static TierSpec ParseMemory(string rest)
    {
        var (pathPart, query) = SplitQuery(rest);
        if (pathPart.Length != 0)
            throw Err($"memory 无位置（'{pathPart}' 非法）——内存卷不占路径。");
        return ApplyQuery(new TierSpec { Nature = StorageNature.Memory }, query);
    }

    private static TierSpec ParseVirtual(string rest)
    {
        var (pathPart, query) = SplitQuery(rest);
        if (!pathPart.StartsWith("//", StringComparison.Ordinal))
            throw Err("virtual 必须完整——无裸形态、无相对形态：virtual:///路径（文件载体）或 virtual:///dev/设备（设备载体）。");
        var body = NormalizeSeparators(pathPart[2..]);
        if (body.Length == 0)
            throw Err("virtual:// 后为空——载体路径必填（虚拟卷没有默认载体）。");
        if (body[0] == '/') body = body[1..];

        if (body.StartsWith("dev/", StringComparison.Ordinal))
            return ApplyQuery(new TierSpec
            {
                Nature = StorageNature.Virtual,
                SubKind = "dev",
                AbsolutePath = "/dev/" + body["dev/".Length..],
            }, query);

        // 文件载体（缺省二级）：绝对路径（/ 起）或 Windows 盘符
        return ApplyQuery(new TierSpec
        {
            Nature = StorageNature.Virtual,
            AbsolutePath = IsDrivePath(body) ? body : "/" + body,
        }, query);
    }

    private static TierSpec ParseNetwork(string rest)
    {
        var (pathPart, query) = SplitQuery(rest);
        if (!pathPart.StartsWith("//", StringComparison.Ordinal))
            throw Err("network 必须完整——无裸形态：network:///协议/端点/桶[/前缀]。");
        var body = NormalizeSeparators(pathPart[2..]);
        if (!body.StartsWith('/'))
            throw Err("network 协议首段必填（network:///s3/…）——协议是部署事实，无缺省、不猜测（fail-fast）。");
        body = body[1..];

        var s1 = body.IndexOf('/');
        if (s1 <= 0)
            throw Err("network 缺协议首段或端点——形态：network:///协议/端点/桶[/前缀]。");
        var proto = body[..s1];
        if (!ProtocolNameValid(proto))
            throw Err($"协议名非法 '{proto}'——小写字母/数字/连字符，字母数字开头（注册表键规范）。");

        var address = body[(s1 + 1)..];
        var s2 = address.IndexOf('/');
        if (s2 <= 0)
            throw Err("network 缺桶名——形态：network:///协议/端点/桶[/前缀]。");
        var endpoint = address[..s2];
        var bucketAndPrefix = address[(s2 + 1)..];
        var s3 = bucketAndPrefix.IndexOf('/');
        var bucket = s3 < 0 ? bucketAndPrefix : bucketAndPrefix[..s3];
        var prefix = s3 < 0 ? "" : bucketAndPrefix[(s3 + 1)..];
        if (bucket.Length == 0)
            throw Err("network 桶名不可为空。");

        return ApplyQuery(new TierSpec
        {
            Nature = StorageNature.Network,
            SubKind = proto,
            Endpoint = endpoint,
            Bucket = bucket,
            KeyPrefix = prefix,
        }, query);
    }

    // ═══════════════ 查询参数（设计 §2.5 参数表——封闭键集）═══════════════

    private static TierSpec ApplyQuery(TierSpec spec, string? query)
    {
        if (query is null) return spec;

        string? label = null, cred = null, region = null;
        long? quota = null;
        var accessSet = false;
        var access = AccessMode.ReadWrite;
        var exclusiveSet = false;
        var exclusive = false;
        var vhostSet = false;
        var vhost = false;
        var tlsSet = false;
        var tls = true;
        TierSpec? spill = null;
        List<string>? members = null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw Err($"参数缺 '='：'{pair}'。");
            var key = pair[..eq];
            var value = pair[(eq + 1)..];
            if (value.Length == 0)
                throw Err($"参数值不可为空：'{key}'（省略参数请整体省略）。");

            switch (key)
            {
                case "label":
                    Dup(label is not null, key);
                    if (Encoding.UTF8.GetByteCount(value) > 32)
                        throw Err($"label 超 32 UTF-8 字节（{Encoding.UTF8.GetByteCount(value)}）。");
                    label = value;
                    break;
                case "quota":
                    Dup(quota is not null, key);
                    quota = ParseSize(value);
                    break;
                case "access":
                    Dup(accessSet, key);
                    accessSet = true;
                    access = value switch
                    {
                        "ro" => AccessMode.Read,
                        "wo" => AccessMode.Write,
                        "rw" => AccessMode.ReadWrite,
                        _ => throw Err($"access 值非法 '{value}'——ro / wo / rw。"),
                    };
                    break;
                case "exclusive":
                    Dup(exclusiveSet, key);
                    exclusiveSet = true;
                    exclusive = ParseFlag(value, key);
                    break;
                case "spill":
                    Dup(spill is not null, key);
                    spill = Parse(value);
                    if (spill.Nature is not (StorageNature.Local or StorageNature.Memory))
                        throw Err($"spill 目标限 local/memory（收到 {spill.Nature}）——spill 是本地中转位置。");
                    break;
                case "cred":
                    Dup(cred is not null, key);
                    if (!value.StartsWith("env:", StringComparison.Ordinal) || value.Length == "env:".Length)
                        throw Err($"cred 必须为引用形态 'env:NAME'（收到 '{value}'）——凭证永不携值。");
                    cred = value;
                    break;
                case "region":
                    Dup(region is not null, key);
                    region = value;
                    break;
                case "vhost":
                    Dup(vhostSet, key);
                    vhostSet = true;
                    vhost = ParseFlag(value, key);
                    break;
                case "tls":
                    Dup(tlsSet, key);
                    tlsSet = true;
                    tls = ParseFlag(value, key);
                    break;
                case "member":
                    (members ??= []).Add(NormalizeSeparators(value));
                    break;
                default:
                    throw Err($"未知参数 '{key}'——参数集封闭（§2.5 表）：label/quota/access/exclusive/spill/cred/region/vhost/tls/member。");
            }
        }

        // 参数 × 介质合法性（§2.5）
        if (spec.Nature != StorageNature.Network && (spill is not null || cred is not null || region is not null || vhostSet || tlsSet))
            throw Err("spill/cred/region/vhost/tls 仅 network。");
        if (spec.Nature != StorageNature.Virtual && members is not null)
            throw Err("member（多载体清单）仅 virtual。");

        return spec with
        {
            Label = label,
            QuotaBytes = quota ?? -1,
            Access = access,
            Exclusive = exclusive,
            Spill = spill,
            CredentialRef = cred,
            Region = region,
            VirtualHostAddressing = vhost,
            Tls = tls,
            Members = members ?? [],
        };
    }

    /// <summary>尺寸解析：纯数字 = 字节；数字 + B/K/M/G/T（1024 基）；-1 = 无上限。</summary>
    private static long ParseSize(string value)
    {
        if (value == "-1") return -1;

        var last = value[^1];
        var hasSuffix = char.IsAsciiDigit(last) is false;
        var numberPart = hasSuffix ? value[..^1] : value;
        if (numberPart.Length == 0 || !numberPart.All(char.IsAsciiDigit))
            throw Err($"quota 值非法 '{value}'——纯数字（字节）、数字+B/K/M/G/T 后缀（1024 基）或 -1（无上限）。");

        long magnitude = long.Parse(numberPart);
        if (hasSuffix)
        {
            magnitude = char.ToUpperInvariant(last) switch
            {
                'B' => magnitude,
                'K' => magnitude << 10,
                'M' => magnitude << 20,
                'G' => magnitude << 30,
                'T' => magnitude << 40,
                _ => throw Err($"quota 后缀非法 '{last}'——B / K / M / G / T。"),
            };
        }
        if (magnitude <= 0)
            throw Err($"quota 必须为正或 -1（收到 {magnitude}）——0 不是合法上限。");
        return magnitude;
    }

    private static bool ParseFlag(string value, string key)
        => value switch
        {
            "1" => true,
            "0" => false,
            _ => throw Err($"{key} 值非法 '{value}'——1 / 0。"),
        };

    private static void Dup(bool already, string key)
    {
        if (already) throw Err($"参数 '{key}' 重复——一词一形，禁止双写。");
    }

    private static (string PathPart, string? Query) SplitQuery(string rest)
    {
        var q = rest.IndexOf('?');
        return q < 0 ? (rest, null) : (rest[..q], rest[(q + 1)..]);
    }

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static bool IsDrivePath(string body)
        => body.Length >= 2 && char.IsAsciiLetter(body[0]) && body[1] == ':'
           && (body.Length == 2 || body[2] == '/');

    private static bool ProtocolNameValid(string proto)
        => proto.Length > 0 && char.IsAsciiLetterOrDigit(proto[0])
           && proto.All(c => char.IsAsciiLetterOrDigit(c) && !char.IsUpper(c) || c == '-');

    private static FormatException Err(string message) => new($"spec 解析失败：{message}");

    // ═══════════════ 规范形序列化（往返稳定）═══════════════

    /// <summary>输出规范形 spec 字符串（往返稳定：<c>Parse(Parse(s).ToString()).ToString()</c> 恒等）。</summary>
    /// <returns>规范形 spec 字符串。</returns>
    public override string ToString()
    {
        var sb = new StringBuilder();
        switch (Nature)
        {
            case StorageNature.Local:
                if (IsCwdRoot) sb.Append("local:");
                else if (RelativePath is not null) sb.Append("local:").Append(RelativePath);
                else if (UncHost is not null) sb.Append("local://").Append(UncHost).Append(UncPath);
                else if (AbsolutePath![0] == '/') sb.Append("local://").Append(AbsolutePath);
                else sb.Append("local:///").Append(AbsolutePath);   // Windows 盘符——三斜杠规范形
                break;
            case StorageNature.Memory:
                sb.Append("memory:");
                break;
            case StorageNature.Virtual:
                if (SubKind == "dev") sb.Append("virtual:///").Append(AbsolutePath![1..]);   // 去前导 / → dev/…
                else if (AbsolutePath![0] == '/') sb.Append("virtual://").Append(AbsolutePath);
                else sb.Append("virtual:///").Append(AbsolutePath);
                break;
            case StorageNature.Network:
                sb.Append("network:///").Append(SubKind).Append('/').Append(Endpoint).Append('/').Append(Bucket);
                if (KeyPrefix!.Length > 0) sb.Append('/').Append(KeyPrefix);
                break;
            default:
                throw new InvalidOperationException($"未知的介质本性：{Nature}");
        }

        var parts = new List<string>();
        if (Label is not null) parts.Add($"label={Label}");
        if (QuotaBytes != -1) parts.Add($"quota={QuotaBytes}");
        if (Access != AccessMode.ReadWrite)
            parts.Add($"access={Access switch { AccessMode.Read => "ro", AccessMode.Write => "wo", _ => "rw" }}");
        if (Exclusive) parts.Add("exclusive=1");
        if (Spill is not null) parts.Add($"spill={Spill}");
        if (CredentialRef is not null) parts.Add($"cred={CredentialRef}");
        if (Region is not null) parts.Add($"region={Region}");
        if (VirtualHostAddressing) parts.Add("vhost=1");
        if (!Tls) parts.Add("tls=0");
        foreach (var m in Members) parts.Add($"member={m}");
        if (parts.Count > 0) sb.Append('?').Append(string.Join("&", parts));
        return sb.ToString();
    }

    // Members 是集合——record 默认按引用比较，覆写为序列相等（往返等价判定依赖值相等）
    /// <summary>值相等比较（含 Members 序列相等——record 默认引用比较对集合不适用）。</summary>
    /// <param name="other">另一 spec（null = 不相等）。</param>
    /// <returns>true = 全部字段值相等且 Members 序列相等。</returns>
    public bool Equals(TierSpec? other) =>
        other is not null
        && Nature == other.Nature && SubKind == other.SubKind
        && AbsolutePath == other.AbsolutePath && UncHost == other.UncHost && UncPath == other.UncPath
        && RelativePath == other.RelativePath && IsCwdRoot == other.IsCwdRoot
        && Endpoint == other.Endpoint && Bucket == other.Bucket && KeyPrefix == other.KeyPrefix
        && Label == other.Label && QuotaBytes == other.QuotaBytes && Access == other.Access
        && Exclusive == other.Exclusive && Spill == other.Spill
        && CredentialRef == other.CredentialRef && Region == other.Region
        && VirtualHostAddressing == other.VirtualHostAddressing && Tls == other.Tls
        && Members.SequenceEqual(other.Members);

    /// <summary>哈希码（介质/位置/端点/桶 + 挂载参数 label/quota/access/exclusive 的复合哈希）。</summary>
    /// <returns>复合哈希值。</returns>
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(Nature, SubKind, AbsolutePath, UncHost, RelativePath, IsCwdRoot, Endpoint, Bucket),
        HashCode.Combine(Label, QuotaBytes, Access, Exclusive));
}
