using System.Text;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.Queue;

/// <summary>注册表组条目（组名 + 定格配置——spec §5.2 GroupConfig 创建时定格持久）。</summary>
/// <param name="Name">组名。</param>
/// <param name="VisibilityTimeoutMs">可见性超时（毫秒——在途未 ack 的重投等待）。</param>
/// <param name="MaxRedeliveries">重投上限（达上限死信）。</param>
public readonly record struct GroupRegistryEntry(string Name, long VisibilityTimeoutMs, int MaxRedeliveries);

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct QueueGroupRegistryHeaderLayout
{
    [FieldOffset(0)] internal ulong MagicValue;
    [FieldOffset(8)] internal int Count;
}

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 4)]
internal struct QueueGroupRegistryNameLayout
{
    [FieldOffset(0)] internal int Length;
}

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct QueueGroupRegistryConfigLayout
{
    [FieldOffset(0)] internal long VisibilityTimeoutMs;
    [FieldOffset(8)] internal int MaxRedeliveries;
}

/// <summary>
/// 组注册表 codec（"TQREG01\0"）——队列级组清单（每组状态体在各自的
/// <c>{queue}.group.{name}</c> VersionedMetadata 域，注册表记名字 + 定格配置）。
/// <para>布局：<c>[Magic 8B][Count 4B] × N {[NameLen 4B + UTF8][VisibilityMs 8B][MaxRedeliveries 4B]}</c>。</para>
/// </summary>
public static class QueueGroupRegistry
{
    private const ulong MagicValue = 0x0031304745525154UL;

    /// <summary>布局魔数。</summary>
    public static ReadOnlySpan<byte> Magic => "TQREG01\0"u8;

    /// <summary>编码组清单。</summary>
    /// <param name="entries">组条目列表（组名 + 定格配置）。</param>
    /// <returns>编码后的注册表字节（头 + N 条组条目）。</returns>
    public static byte[] Write(IReadOnlyList<GroupRegistryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int size = QueueGroupRegistryHeaderLayoutCodec.StructSize;
        foreach (var e in entries)
            size += QueueGroupRegistryNameLayoutCodec.StructSize
                + Encoding.UTF8.GetByteCount(e.Name)
                + QueueGroupRegistryConfigLayoutCodec.StructSize;
        var buf = new byte[size];
        var header = new QueueGroupRegistryHeaderLayout { MagicValue = MagicValue, Count = entries.Count };
        QueueGroupRegistryHeaderLayoutCodec.Write(buf, in header);
        int off = QueueGroupRegistryHeaderLayoutCodec.StructSize;
        foreach (var e in entries)
        {
            int len = Encoding.UTF8.GetByteCount(e.Name);
            var nameLayout = new QueueGroupRegistryNameLayout { Length = len };
            QueueGroupRegistryNameLayoutCodec.Write(buf.AsSpan(off), in nameLayout);
            off += QueueGroupRegistryNameLayoutCodec.StructSize;
            Encoding.UTF8.GetBytes(e.Name, buf.AsSpan(off, len));
            off += len;
            var config = new QueueGroupRegistryConfigLayout
            {
                VisibilityTimeoutMs = e.VisibilityTimeoutMs,
                MaxRedeliveries = e.MaxRedeliveries,
            };
            QueueGroupRegistryConfigLayoutCodec.Write(buf.AsSpan(off), in config);
            off += QueueGroupRegistryConfigLayoutCodec.StructSize;
        }
        return buf;
    }

    /// <summary>解码（魔数/几何校验失败 = 空注册表）。</summary>
    /// <param name="src">注册表字节。</param>
    /// <param name="entries">输出：组条目列表（校验失败 = 空列表）。</param>
    /// <returns>true = 解码成功；false = 魔数/几何非法。</returns>
    public static bool TryRead(ReadOnlySpan<byte> src, out IReadOnlyList<GroupRegistryEntry> entries)
    {
        entries = Array.Empty<GroupRegistryEntry>();
        if (src.Length < QueueGroupRegistryHeaderLayoutCodec.StructSize) return false;
        var header = QueueGroupRegistryHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        int count = header.Count;
        if (count < 0) return false;

        var list = new List<GroupRegistryEntry>(count);
        int off = QueueGroupRegistryHeaderLayoutCodec.StructSize;
        for (int i = 0; i < count; i++)
        {
            if (off + QueueGroupRegistryNameLayoutCodec.StructSize > src.Length) return false;
            var nameLayout = QueueGroupRegistryNameLayoutCodec.Read(src.Slice(off));
            int len = nameLayout.Length;
            off += QueueGroupRegistryNameLayoutCodec.StructSize;
            if (len < 0 || off + len + QueueGroupRegistryConfigLayoutCodec.StructSize > src.Length) return false;
            string name = Encoding.UTF8.GetString(src.Slice(off, len));
            off += len;
            var config = QueueGroupRegistryConfigLayoutCodec.Read(src.Slice(off));
            off += QueueGroupRegistryConfigLayoutCodec.StructSize;
            list.Add(new GroupRegistryEntry(name, config.VisibilityTimeoutMs, config.MaxRedeliveries));
        }
        entries = list;
        return true;
    }
}
