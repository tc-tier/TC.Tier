using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using TC.Tier.CodeGen;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Federation;

/// <summary>联邦协议域（二期-F6——DDR-F6，核心区 0x00-0x4F 内分配）。</summary>
public static class FederationProtocol
{
    /// <summary>联邦拉取/批次承载协议域（核心注册口）。</summary>
    public const byte ProtocolId = 0x06;
}

/// <summary>联邦拉取请求头（24B：[ClusterTag 4B][GroupId 8B][Watermark 8B][MaxEntries 4B]——
/// [BinaryLayout] 声明式生成；GroupId = <see cref="RaftGroupId"/>.Value 摊平）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct FederationPullRequest
{
    /// <summary>对端声明的源集群标签。</summary>
    [FieldOffset(0)] public uint ClusterTag;

    /// <summary>目标组号（<see cref="RaftGroupId"/>.Value）。</summary>
    [FieldOffset(4)] public ulong GroupId;

    /// <summary>对端已消费水位。</summary>
    [FieldOffset(12)] public long Watermark;

    /// <summary>单次条数上限。</summary>
    [FieldOffset(20)] public int MaxEntries;
}

/// <summary>联邦批次应答头（9B：[Granted 1B][ClusterTag 4B][Count 4B]）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 9)]
public struct FederationBatchHead
{
    /// <summary>0 = 拒链，1 = 批次有效。</summary>
    [FieldOffset(0)] public byte Granted;

    /// <summary>源集群标签。</summary>
    [FieldOffset(1)] public uint ClusterTag;

    /// <summary>条目数。</summary>
    [FieldOffset(5)] public int Count;
}

/// <summary>联邦条目定长头（21B：[Index 8B][Term 8B][Kind 1B][ContentLength 4B]——payload 尾随变长）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 21)]
public struct FederationEntryHead
{
    /// <summary>源日志 index。</summary>
    [FieldOffset(0)] public long Index;

    /// <summary>源 term。</summary>
    [FieldOffset(8)] public long Term;

    /// <summary>条目种类。</summary>
    [FieldOffset(16)] public byte Kind;

    /// <summary>payload 长度。</summary>
    [FieldOffset(17)] public int ContentLength;
}

/// <summary>
/// 联邦线信封（DDR-F6 定案）：跨集群独立 raft 域间的已提交条目流。
/// <list type="bullet">
/// <item>拉取请求 = <c>[ClusterTag 4B][GroupId 8B][Watermark 8B][MaxEntries 4B]</c>（24B）；</item>
/// <item>批次应答 = <c>[Granted 1B][ClusterTag 4B][Count 4B] + 条目×N</c>，
///   条目 = <c>[Index 8B][Term 8B][Kind 1B][len 4B][payload]</c>。</item>
/// </list>
/// <para>★ Granted=0 = 拒链（对端 ClusterTag 不匹配——FederationWire.ClusterTagMatches
/// 门）； Granted=1 = 批次有效（Count 可为 0——无增量）。</para>
/// </summary>
public static class FederationWire
{
    /// <summary>拉取请求定长（ClusterTag 4 + GroupId 8 + Watermark 8 + Max 4）。</summary>
    public const int PullRequestSize = 24;

    /// <summary>批次应答定长头（Granted 1 + ClusterTag 4 + Count 4）。</summary>
    public const int BatchHeaderSize = 9;

    /// <summary>单条目定长头（Index 8 + Term 8 + Kind 1 + len 4）。</summary>
    public const int EntryHeaderSize = 21;

    /// <summary>集群归属核对（跨集群链路门——复用握手同款比较，spec-12 §3.3）。</summary>
    /// <param name="local">本端集群标签。</param>
    /// <param name="remote">对端声明的集群标签。</param>
    /// <returns>true = 标签一致（放行）；false = 错配（拒链——Granted=0 回显本端标签供诊断）。</returns>
    public static bool ClusterTagMatches(uint local, uint remote) => local == remote;

    /// <summary>编码拉取请求。</summary>
    /// <param name="clusterTag">目标源集群标签（源侧据此核对，错配即拒链）。</param>
    /// <param name="group">联邦承载的组 ID。</param>
    /// <param name="watermark">本地已消费的源日志高水位（增量自水位 + 1 起拉；0 = 从头）。</param>
    /// <param name="maxEntries">单次拉取条数上限（≥ 1——源侧对 ≤ 0 视为畸形拒链）。</param>
    /// <returns>24B 拉取请求帧（<c>[ClusterTag 4B][GroupId 8B][Watermark 8B][MaxEntries 4B]</c>——小端）。</returns>
    public static byte[] EncodePull(uint clusterTag, RaftGroupId group, long watermark, int maxEntries)
    {
        var buf = new byte[PullRequestSize];
        FederationPullRequestCodec.Write(buf, new FederationPullRequest
        {
            ClusterTag = clusterTag,
            GroupId = group.Value,
            Watermark = watermark,
            MaxEntries = maxEntries,
        });
        return buf;
    }

    /// <summary>解码拉取请求（缺长返回 false）。</summary>
    /// <param name="wire">入站帧（≥ <see cref="PullRequestSize"/> 字节）。</param>
    /// <param name="clusterTag">输出：对端声明的源集群标签。</param>
    /// <param name="group">输出：目标组 ID。</param>
    /// <param name="watermark">输出：对端已消费水位。</param>
    /// <param name="maxEntries">输出：对端单次条数上限。</param>
    /// <returns>true = 解码成功（各输出有效）；false = 帧长不足（输出全部归零/default）。</returns>
    public static bool TryDecodePull(ReadOnlySpan<byte> wire, out uint clusterTag, out RaftGroupId group,
        out long watermark, out int maxEntries)
    {
        if (wire.Length < PullRequestSize)
        {
            clusterTag = 0; group = default; watermark = 0; maxEntries = 0;
            return false;
        }
        var head = FederationPullRequestCodec.Read(wire);
        clusterTag = head.ClusterTag;
        group = new RaftGroupId(head.GroupId);
        watermark = head.Watermark;
        maxEntries = head.MaxEntries;
        return true;
    }

    /// <summary>编码拒绝应答（Granted=0 + 本端 ClusterTag——对端可诊断错配）。</summary>
    /// <param name="localClusterTag">本端集群标签（回显供对端诊断错配）。</param>
    /// <returns>9B 拒绝应答帧（Granted=0、条目数 0）。</returns>
    public static byte[] EncodeReject(uint localClusterTag)
    {
        var buf = new byte[BatchHeaderSize];
        FederationBatchHeadCodec.Write(buf, new FederationBatchHead { Granted = 0, ClusterTag = localClusterTag });
        return buf;
    }

    /// <summary>编码批次应答（条目为源已提交事实——逐条整段拷贝进单缓冲）。</summary>
    /// <param name="clusterTag">源集群标签（批次声明方）。</param>
    /// <param name="entries">已提交条目列表（可为空——无增量）。</param>
    /// <returns>批次应答帧（Granted=1：9B 头 + 逐条 <c>[Index 8B][Term 8B][Kind 1B][len 4B][payload]</c>——小端）。</returns>
    public static byte[] EncodeBatch(uint clusterTag,
        IReadOnlyList<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries)
    {
        var size = BatchHeaderSize;
        foreach (var e in entries) size += EntryHeaderSize + e.Content.Length;
        var buf = new byte[size];
        FederationBatchHeadCodec.Write(buf, new FederationBatchHead { Granted = 1, ClusterTag = clusterTag, Count = entries.Count });
        var p = BatchHeaderSize;
        foreach (var e in entries)
        {
            FederationEntryHeadCodec.Write(buf.AsSpan(p, EntryHeaderSize), new FederationEntryHead
            {
                Index = e.Index,
                Term = e.Term,
                Kind = e.Kind,
                ContentLength = e.Content.Length,
            });
            e.Content.Span.CopyTo(buf.AsSpan(p + EntryHeaderSize));
            p += EntryHeaderSize + e.Content.Length;
        }
        return buf;
    }

    /// <summary>解码批次应答（截断/条目失配 = false——静默容错会吞数据）。</summary>
    /// <param name="wire">入站应答帧。</param>
    /// <param name="granted">输出：true = 批次有效；false = 拒链。</param>
    /// <param name="clusterTag">输出：源集群标签。</param>
    /// <param name="entries">输出：条目列表（拒链/空批次/解码失败 = 空表）。</param>
    /// <returns>true = 帧结构完整（继续判定 granted——拒链/空批次亦 true）；false = 截断/条目长度失配（输出归零/default）。</returns>
    public static bool TryDecodeBatch(ReadOnlySpan<byte> wire, out bool granted, out uint clusterTag,
        out List<(long Index, long Term, byte Kind, byte[] Content)> entries)
    {
        granted = false; clusterTag = 0; entries = [];
        if (wire.Length < BatchHeaderSize) return false;
        var head = FederationBatchHeadCodec.Read(wire);
        granted = head.Granted != 0;
        clusterTag = head.ClusterTag;
        var count = head.Count;
        if (!granted || count == 0) return true;   // 拒链 / 空批次（无增量）
        entries = new List<(long, long, byte, byte[])>(count);
        var p = BatchHeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (wire.Length < p + EntryHeaderSize) return false;
            var entry = FederationEntryHeadCodec.Read(wire.Slice(p, EntryHeaderSize));
            if (entry.ContentLength < 0 || wire.Length < p + EntryHeaderSize + entry.ContentLength) return false;
            entries.Add((entry.Index, entry.Term, entry.Kind,
                wire.Slice(p + EntryHeaderSize, entry.ContentLength).ToArray()));
            p += EntryHeaderSize + entry.ContentLength;
        }
        return true;
    }
}

/// <summary>联邦去重水位存储（对端 (ClusterTag, GroupId) 域的已消费源 index——持久化面）。</summary>
public interface IFederationWatermarkStore
{
    /// <summary>读已消费的源日志高水位（0 = 从头拉取）。</summary>
    long Get(uint clusterTag, RaftGroupId group);

    /// <summary>推进水位（单调不回退——重放确认后调用；持久化先于确认返回）。</summary>
    void Set(uint clusterTag, RaftGroupId group, long index);
}

/// <summary>内存水位存储（测试/嵌入式形态）。</summary>
public sealed class InMemoryFederationWatermarkStore : IFederationWatermarkStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(uint, RaftGroupId), long> _watermarks = [];

    /// <inheritdoc/>
    /// <param name="clusterTag">源集群标签（水位域键之一）。</param>
    /// <param name="group">目标组 ID（水位域键之一）。</param>
    /// <returns>该 (标签, 组) 域已消费的源日志高水位（去重依据）；0 = 从未消费（从头拉取）。</returns>
    public long Get(uint clusterTag, RaftGroupId group)
    {
        lock (_lock) return _watermarks.TryGetValue((clusterTag, group), out var w) ? w : 0;
    }

    /// <inheritdoc/>
    /// <param name="clusterTag">源集群标签（水位域键之一）。</param>
    /// <param name="group">目标组 ID（水位域键之一）。</param>
    /// <param name="index">新已消费水位（源日志 index——单调不回退，≤ 现值忽略）。</param>
    public void Set(uint clusterTag, RaftGroupId group, long index)
    {
        lock (_lock)
        {
            if (_watermarks.TryGetValue((clusterTag, group), out var cur) && index <= cur) return;   // 单调
            _watermarks[(clusterTag, group)] = index;
        }
    }
}

/// <summary>文件水位存储（生产形态——tmp+原子替换；断链/重启恢复的去重依据）。</summary>
public sealed class FileFederationWatermarkStore : IFederationWatermarkStore
{
    private readonly IFileSystem _fs;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<(uint, RaftGroupId), long> _watermarks = [];

    /// <param name="fs">承载文件系统——★ 零 BCL IO 纪律（设计 §11）：一切文件访问经 IFileSystem。</param>
    /// <param name="path">持久化文件相对路径（文本行 <c>tag,groupId,index</c>——量级 = 链路数）。</param>
    public FileFederationWatermarkStore(IFileSystem fs, string path)
    {
        _fs = fs;
        _path = path;
        if (_fs.Exists(path))
        {
            using var h = _fs.Open(path, new FileOpenOptions { Mode = FileOpenMode.OpenExisting });
            var bytes = new byte[h.Length];
            var n = h.Read(0, bytes);
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, n)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',');
                if (parts.Length == 3
                    && uint.TryParse(parts[0], out var tag)
                    && ulong.TryParse(parts[1], out var gid)
                    && long.TryParse(parts[2], out var idx))
                    _watermarks[(tag, new RaftGroupId(gid))] = idx;
            }
        }
    }

    /// <inheritdoc/>
    /// <param name="clusterTag">源集群标签（水位域键之一）。</param>
    /// <param name="group">目标组 ID（水位域键之一）。</param>
    /// <returns>该 (标签, 组) 域已消费的源日志高水位（去重依据）；0 = 从未消费（从头拉取）。</returns>
    public long Get(uint clusterTag, RaftGroupId group)
    {
        lock (_lock) return _watermarks.TryGetValue((clusterTag, group), out var w) ? w : 0;
    }

    /// <inheritdoc/>
    /// <param name="clusterTag">源集群标签（水位域键之一）。</param>
    /// <param name="group">目标组 ID（水位域键之一）。</param>
    /// <param name="index">新已消费水位（源日志 index——单调不回退，≤ 现值忽略）。</param>
    public void Set(uint clusterTag, RaftGroupId group, long index)
    {
        lock (_lock)
        {
            if (_watermarks.TryGetValue((clusterTag, group), out var cur) && index <= cur) return;
            _watermarks[(clusterTag, group)] = index;
            var tmp = _path + ".tmp";
            var payload = Encoding.UTF8.GetBytes(
                string.Join('\n', _watermarks.Select(kv => $"{kv.Key.Item1},{kv.Key.Item2.Value},{kv.Value}")) + "\n");
            using (var h = _fs.Open(tmp, new FileOpenOptions { Mode = FileOpenMode.Truncate }))
            {
                h.Write(0, payload);
                h.Flush();
            }
            _fs.Move(tmp, _path, overwrite: true);   // 原子替换——断电不半写
        }
    }
}
