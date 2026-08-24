using System.Text;

namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 对象用户元数据——"PUT 时原子快照，非实时可变元数据"（对象间不可变；上限归一 2KB——全厂商最严约束）。
/// <para>★ 超限/非法键<b>直接抛 <see cref="ArgumentException"/>，不静默截断</b>（EngineMeta 类关键元数据
///   被截 = 恢复失败；数据损坏不可静默——评审中 3/M2 定案）。构造时即校验（早失败——不在 Flush 的 PUT 才失败）。</para>
/// <para>★ 键字符集 [A-Za-z0-9_.-]（RFC 7230 token 最严交集；不转义——转义引入往返不对称，静默转义比拒绝更危险）。</para>
/// </summary>
public sealed class ObjectMetadata
{
    /// <summary>用户元数据字节上限（键+值 UTF-8 总和，含 x-amz-meta- 前缀开销——全厂商最严约束归一）。</summary>
    public const int MaxTotalBytes = 2048;

    private const string HeaderPrefixOverhead = "x-amz-meta-";   // 计入真实 HTTP header 尺寸开销

    /// <summary>空元数据单例（不可变——共享安全）。</summary>
    public static ObjectMetadata Empty { get; } = new(null);

    private ObjectMetadata(IReadOnlyDictionary<string, string>? userMetadata)
    {
        UserMetadata = userMetadata ?? new Dictionary<string, string>();
        if (UserMetadata.Count > 0)
            ValidateTotalBytes(UserMetadata);
    }

    /// <summary>构造并校验（非法键/超限抛 <see cref="ArgumentException"/>）。</summary>
    public static ObjectMetadata Create(IReadOnlyDictionary<string, string>? userMetadata)
        => userMetadata is null or { Count: 0 } ? Empty : new ObjectMetadata(userMetadata);

    /// <summary>用户元数据键值（只读视图；键 [A-Za-z0-9_.-]，键+值 UTF-8 总和 ≤ <see cref="MaxTotalBytes"/>）。</summary>
    public IReadOnlyDictionary<string, string> UserMetadata { get; }

    /// <summary>单键写入校验（桥层 xattr 早失败路径复用——WriteExtendedAttribute 即抛，不待 Flush）。</summary>
    public static void ValidateUserMetadataEntry(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("元数据键不能为空。", nameof(key));
        foreach (var c in key)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-')
                continue;
            throw new ArgumentException(
                $"元数据键含非法字符 '{c}'（键仅允许 [A-Za-z0-9_.-]——RFC 7230 token 最严交集，不转义）: {key}", nameof(key));
        }
    }

    /// <summary>总量校验（构造期调用；键+值 UTF-8 + 每键 x-amz-meta- 前缀开销 ≤ <see cref="MaxTotalBytes"/>）。</summary>
    private static void ValidateTotalBytes(IReadOnlyDictionary<string, string> userMetadata)
    {
        long total = 0;
        foreach (var (key, value) in userMetadata)
        {
            ValidateUserMetadataEntry(key, value);
            total += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value)
                     + HeaderPrefixOverhead.Length;
        }
        if (total > MaxTotalBytes)
            throw new ArgumentException(
                $"用户元数据超限（{total} > {MaxTotalBytes} 字节，含 x-amz-meta- 前缀开销）——不静默截断（关键元数据被截 = 恢复失败）。",
                nameof(userMetadata));
    }
}
