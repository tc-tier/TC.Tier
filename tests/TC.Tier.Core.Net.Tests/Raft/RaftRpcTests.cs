using System.Buffers.Binary;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Raft RPC message family contract tests (spec-01..05 message semantics × spec-12 §6/§10 —
/// [WireMessage]-generated wire format locked byte-exact: [Tag][Term 8B prefix][subclass fields],
/// RpcId eliminated (CorrId is transport-carried), NodeId verbatim, AppendEntries entries as a
/// single region blob (v3 — kind as a struct field, store-direct region layout);
/// decode defenses = truncated / unknown tag / Count over bound).
/// </summary>
public class RaftRpcTests
{
    private static NodeId Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[NodeId.Size];
        bytes.Fill(fill);
        return new NodeId(bytes);
    }

    private static byte[] Blob(byte fill, int length)
    {
        var data = new byte[length];
        Array.Fill(data, fill);
        return data;
    }

    /// <summary>v3 条目区构建（测试侧模拟 store 直写产物——RaftEntriesRegion 布局）。</summary>
    private static byte[] BuildRegion(params (long Term, byte Kind, byte[] Content)[] entries)
    {
        var writer = new PooledBufferWriter();
        RaftEntriesRegion.WriteCount(writer, entries.Length);
        foreach (var (term, kind, content) in entries)
            RaftEntriesRegion.WriteEntry(writer, term, kind, content);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void TagConstants_EightMessages_MatchLegacyTypeCodes()
    {
        RaftRpcCodec.TagPreVoteReq.Should().Be(0x01);
        RaftRpcCodec.TagPreVoteResp.Should().Be(0x02);
        RaftRpcCodec.TagRequestVoteReq.Should().Be(0x03);
        RaftRpcCodec.TagRequestVoteResp.Should().Be(0x04);
        RaftRpcCodec.TagAppendEntriesReq.Should().Be(0x05);
        RaftRpcCodec.TagAppendEntriesResp.Should().Be(0x06);
        RaftRpcCodec.TagInstallSnapshotReq.Should().Be(0x07);
        RaftRpcCodec.TagInstallSnapshotResp.Should().Be(0x08);
    }

    [Fact]
    public void Encode_VoteRequests_TagTermPrefixThenCandidateFields()
    {
        var preVote = RaftRpcCodec.Encode(new PreVoteReq
        {
            Term = 7, CandidateId = Id(0xAA), LastLogIndex = 42, LastLogTerm = 6,
        });
        // [Tag 1B][Term 8B LE][CandidateId 16B verbatim][LastLogIndex 8B][LastLogTerm 8B]——RpcId 已取消（CorrId 归传输）
        preVote.Length.Should().Be(1 + 8 + NodeId.Size + 8 + 8, "RpcId removed — CorrId is transport-carried");
        preVote[0].Should().Be(0x01);
        BinaryPrimitives.ReadInt64LittleEndian(preVote.AsSpan(1)).Should().Be(7);
        preVote.Skip(9).Take(NodeId.Size).Should().OnlyContain(b => b == 0xAA);
        BinaryPrimitives.ReadInt64LittleEndian(preVote.AsSpan(1 + 8 + NodeId.Size)).Should().Be(42);
        BinaryPrimitives.ReadInt64LittleEndian(preVote.AsSpan(1 + 8 + NodeId.Size + 8)).Should().Be(6);

        RaftRpcCodec.Encode(new RequestVoteResp { Term = 9, Granted = true })
            .Should().Equal(new byte[] { 0x04, 9, 0, 0, 0, 0, 0, 0, 0, 1 });
    }

    [Fact]
    public void Encode_AppendEntriesReq_EntriesRegionBlob_Verbatim()
    {
        var region = BuildRegion((4, RaftEntryKind.Command, Blob(0x11, 3)), (5, RaftEntryKind.Config, Blob(0x22, 5)));
        var encoded = RaftRpcCodec.Encode(new AppendEntriesReq
        {
            Term = 5, LeaderId = Id(0xBB), PrevLogIndex = 10, PrevLogTerm = 4,
            EntriesRegion = region, LeaderCommit = 8,
        });

        // [Tag][Term 8][LeaderId 16][PrevLogIndex 8][PrevLogTerm 8][LeaderCommit 8][Region Len 4][region]
        // 区内（RaftEntriesRegion）：[Count 4][×N: Term 8][Kind 1][Len 4][content]
        var regionOffset = 1 + 8 + NodeId.Size + 8 + 8 + 8;
        encoded.Length.Should().Be(regionOffset + 4 + region.Length);
        encoded[0].Should().Be(0x05);
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(1 + 8 + NodeId.Size)).Should().Be(10);
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(1 + 8 + NodeId.Size + 8)).Should().Be(4, "PrevLogTerm");
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(1 + 8 + NodeId.Size + 16)).Should().Be(8, "LeaderCommit");
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(regionOffset)).Should().Be(region.Length, "region len");
        var entriesOffset = regionOffset + 4;
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(entriesOffset)).Should().Be(2, "region count");
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(entriesOffset + 4)).Should().Be(4, "first entry term");
        encoded[entriesOffset + 4 + 8].Should().Be(RaftEntryKind.Command, "kind as struct field (envelope dead)");
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(entriesOffset + 4 + 9)).Should().Be(3, "content len");
        encoded[entriesOffset + 4 + 13].Should().Be(0x11, "content bytes verbatim");
    }

    [Fact]
    public void Encode_RespCarriesConflictHintsAndSnapshotIndex()
    {
        var encoded = RaftRpcCodec.Encode(new AppendEntriesResp
        {
            Term = 6, Success = false, MatchIndex = 0, ConflictTerm = 3, ConflictIndex = 12, SnapshotIndex = 9,
        });
        encoded.Length.Should().Be(1 + 8 + 1 + 8 + 8 + 8 + 8);
        encoded[0].Should().Be(0x06);
        encoded[9].Should().Be(0, "success=false");
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(10 + 8)).Should().Be(3, "ConflictTerm");
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(10 + 16)).Should().Be(12, "ConflictIndex");
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(10 + 24)).Should().Be(9, "SnapshotIndex");
    }

    [Fact]
    public void RoundTrip_AllEightMessages_PreservesFields()
    {
        var preVote = RoundTrip(new PreVoteReq { Term = 3, CandidateId = Id(0x31), LastLogIndex = 5, LastLogTerm = 2 });
        preVote.Should().BeOfType<PreVoteReq>().Which.Should().Match<PreVoteReq>(
            m => m.CandidateId == Id(0x31) && m.LastLogIndex == 5 && m.LastLogTerm == 2);

        var preVoteResp = RoundTrip(new PreVoteResp { Term = 3, Granted = true });
        preVoteResp.Should().BeOfType<PreVoteResp>().Which.Granted.Should().BeTrue();

        var vote = RoundTrip(new RequestVoteReq { Term = 4, CandidateId = Id(0x32), LastLogIndex = 9, LastLogTerm = 4 });
        vote.Should().BeOfType<RequestVoteReq>().Which.CandidateId.Should().Be(Id(0x32));

        var voteResp = RoundTrip(new RequestVoteResp { Term = 4, Granted = false });
        voteResp.Should().BeOfType<RequestVoteResp>().Which.Granted.Should().BeFalse();

        var append = RoundTrip(new AppendEntriesReq
        {
            Term = 5, LeaderId = Id(0x33), PrevLogIndex = 7, PrevLogTerm = 5,
            EntriesRegion = BuildRegion((5, RaftEntryKind.Command, Blob(0x44, 2)), (5, RaftEntryKind.Config, Blob(0x55, 4))),
            LeaderCommit = 6,
        }).Should().BeOfType<AppendEntriesReq>().Which;
        RaftEntriesRegion.TryReadCount(append.EntriesRegion, out var count, out var cursor).Should().BeTrue();
        count.Should().Be(2);
        RaftEntriesRegion.TryReadEntry(append.EntriesRegion, ref cursor, out var t1, out var k1, out var c1).Should().BeTrue();
        t1.Should().Be(5);
        k1.Should().Be(RaftEntryKind.Command);
        c1.ToArray().Should().Equal(Blob(0x44, 2));
        RaftEntriesRegion.TryReadEntry(append.EntriesRegion, ref cursor, out var t2, out var k2, out var c2).Should().BeTrue();
        t2.Should().Be(5);
        k2.Should().Be(RaftEntryKind.Config);
        c2.ToArray().Should().Equal(Blob(0x55, 4));
        append.LeaderCommit.Should().Be(6);

        var appendResp = RoundTrip(new AppendEntriesResp
        {
            Term = 5, Success = true, MatchIndex = 9, ConflictTerm = 0, ConflictIndex = 0, SnapshotIndex = 0,
        }).Should().BeOfType<AppendEntriesResp>().Which;
        appendResp.MatchIndex.Should().Be(9);

        var install = RoundTrip(new InstallSnapshotReq { Term = 5, LeaderId = Id(0x34), SnapshotIndex = 20 })
            .Should().BeOfType<InstallSnapshotReq>().Which;
        install.SnapshotIndex.Should().Be(20);
        install.Swarm.Should().BeFalse("stream mode default");
        install.Checksums.Should().BeEmpty();
        install.Holders.Should().BeEmpty();

        var installResp = RoundTrip(new InstallSnapshotResp { Term = 5, Success = true, SnapshotIndex = 20 })
            .Should().BeOfType<InstallSnapshotResp>().Which;
        installResp.Success.Should().BeTrue();
    }

    /// <summary>Swarm handshake coordination (spec-12 §6.1 × T6): manifest + holders round-trip
    /// through the InstallSnapshot handshake face.</summary>
    [Fact]
    public void RoundTrip_InstallSnapshot_SwarmCoordinationFieldsPreserved()
    {
        var manifestId = new Opaque16(Blob(0xAB, Opaque16.Size));
        var install = RoundTrip(new InstallSnapshotReq
        {
            Term = 5, LeaderId = Id(0x34), SnapshotIndex = 20,
            Swarm = true, ManifestId = manifestId, BlockSize = 1024, TotalBytes = 4096,
            Checksums = [0xDEADBEEF, 0x12345678], Holders = [Id(0x41), Id(0x42)],
        }).Should().BeOfType<InstallSnapshotReq>().Which;
        install.Swarm.Should().BeTrue();
        install.ManifestId.Should().Be(manifestId);
        install.BlockSize.Should().Be(1024);
        install.TotalBytes.Should().Be(4096);
        install.Checksums.Should().Equal(0xDEADBEEFu, 0x12345678u);
        install.Holders.Should().Equal(Id(0x41), Id(0x42));
    }

    /// <summary>Swarm wire layout byte-exact: [Swarm 1][ManifestId 16][BlockSize 4][TotalBytes 8]
    /// [Checksums Count 4][4×N][Holders Count 4][16×N] after the legacy prefix.</summary>
    [Fact]
    public void Encode_InstallSnapshot_SwarmFieldsFollowLegacyPrefix()
    {
        var manifestId = new Opaque16(Blob(0xAB, Opaque16.Size));
        var encoded = RaftRpcCodec.Encode(new InstallSnapshotReq
        {
            Term = 5, LeaderId = Id(0x34), SnapshotIndex = 20,
            Swarm = true, ManifestId = manifestId, BlockSize = 1024, TotalBytes = 2048,
            Checksums = [0xDEADBEEF], Holders = [Id(0x41)],
        });
        encoded.Length.Should().Be(1 + 8 + NodeId.Size + 8 + 1 + Opaque16.Size + 4 + 8 + 4 + 4 + 4 + NodeId.Size);
        var offset = 1 + 8 + NodeId.Size + 8;
        encoded[offset].Should().Be(1, "Swarm flag");
        encoded.Skip(offset + 1).Take(Opaque16.Size).Should().OnlyContain(b => b == 0xAB, "ManifestId verbatim");
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset + 1 + Opaque16.Size)).Should().Be(1024, "BlockSize");
        BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(offset + 1 + Opaque16.Size + 4)).Should().Be(2048, "TotalBytes");
        var checksumsOffset = offset + 1 + Opaque16.Size + 4 + 8;
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(checksumsOffset)).Should().Be(1, "Checksums count");
        BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(checksumsOffset + 4)).Should().Be(0xDEADBEEF);
        var holdersOffset = checksumsOffset + 4 + 4;
        BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(holdersOffset)).Should().Be(1, "Holders count");
        encoded.Skip(holdersOffset + 4).Take(NodeId.Size).Should().OnlyContain(b => b == 0x41, "holder NodeId verbatim");
    }

    [Fact]
    public void TryDecode_InstallSnapshot_CoordinationCountsOverBound_ReturnsFalse()
    {
        // [Tag][Term 8][LeaderId 16][SnapshotIndex 8][Swarm 1][ManifestId 16][BlockSize 4][TotalBytes 8]
        // [Checksums Count 4] —— 校验和表超界/负数
        Span<byte> payload = stackalloc byte[1 + 8 + NodeId.Size + 8 + 1 + Opaque16.Size + 4 + 8 + 4];
        payload[0] = RaftRpcCodec.TagInstallSnapshotReq;
        BinaryPrimitives.WriteInt64LittleEndian(payload[1..], 1);
        payload[1 + 8 + NodeId.Size + 8] = 1;   // Swarm = true
        var countOffset = 1 + 8 + NodeId.Size + 8 + 1 + Opaque16.Size + 4 + 8;
        BinaryPrimitives.WriteInt32LittleEndian(payload[countOffset..], RaftRpc.MaxSnapshotBlocks + 1);
        RaftRpcCodec.TryDecode(payload, out _).Should().BeFalse("checksums count over MaxSnapshotBlocks");

        // 1 校验和合法 → 后接 Holders Count 域越界（countOffset + 4 校验和 + 4 Holders Count）
        BinaryPrimitives.WriteInt32LittleEndian(payload[countOffset..], 1);
        var withHolderCount = new byte[payload.Length + 4 + 4];
        payload.CopyTo(withHolderCount);
        BinaryPrimitives.WriteInt32LittleEndian(withHolderCount.AsSpan(countOffset + 8), RaftRpc.MaxSnapshotHolders + 1);
        RaftRpcCodec.TryDecode(withHolderCount, out _).Should().BeFalse("holders count over MaxSnapshotHolders");
    }

    private static RaftRpc RoundTrip(RaftRpc rpc)
    {
        var ok = RaftRpcCodec.TryDecode(RaftRpcCodec.Encode(rpc), out var decoded);
        ok.Should().BeTrue();
        return decoded!;
    }

    [Fact]
    public void TryDecode_EmptyOrUnknownTag_ReturnsFalse()
    {
        RaftRpcCodec.TryDecode([], out _).Should().BeFalse();
        RaftRpcCodec.TryDecode(new byte[] { 0x00 }, out _).Should().BeFalse();
        RaftRpcCodec.TryDecode(new byte[] { 0x09 }, out _).Should().BeFalse("tag 0x09 unassigned");
        RaftRpcCodec.TryDecode(new byte[] { 0xFF }, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse()
    {
        var preVote = RaftRpcCodec.Encode(new PreVoteReq { Term = 1, CandidateId = Id(0x35), LastLogIndex = 2, LastLogTerm = 1 });
        RaftRpcCodec.TryDecode(preVote.AsSpan(..^1), out _).Should().BeFalse("tail field truncated");

        var append = RaftRpcCodec.Encode(new AppendEntriesReq
        {
            Term = 1, LeaderId = Id(0x36), PrevLogIndex = 0, PrevLogTerm = 0,
            EntriesRegion = BuildRegion((1, RaftEntryKind.Command, Blob(0x66, 4))), LeaderCommit = 0,
        });
        RaftRpcCodec.TryDecode(append.AsSpan(..^5), out _).Should().BeFalse("region bytes truncated");
    }

    [Fact]
    public void TryDecode_EntriesRegionLenOverBoundOrNegative_ReturnsFalse()
    {
        // [Tag][Term 8][LeaderId 16][PrevLogIndex 8][PrevLogTerm 8][LeaderCommit 8][Region Len 4]
        Span<byte> payload = stackalloc byte[1 + 8 + NodeId.Size + 8 + 8 + 8 + 4];
        payload[0] = RaftRpcCodec.TagAppendEntriesReq;
        BinaryPrimitives.WriteInt64LittleEndian(payload[1..], 1);
        var lenOffset = 1 + 8 + NodeId.Size + 8 + 8 + 8;
        BinaryPrimitives.WriteInt32LittleEndian(payload[lenOffset..], RaftRpc.MaxEntriesRegionBytes + 1);
        RaftRpcCodec.TryDecode(payload, out _).Should().BeFalse("region len over MaxEntriesRegionBytes");

        BinaryPrimitives.WriteInt32LittleEndian(payload[lenOffset..], -1);
        RaftRpcCodec.TryDecode(payload, out _).Should().BeFalse("negative len");
    }

    [Fact]
    public void TryDecode_TrailingExtensionBytes_Tolerated()
    {
        var resp = RaftRpcCodec.Encode(new RequestVoteResp { Term = 2, Granted = true });
        var extended = resp.Concat(new byte[] { 0xDE, 0xAD }).ToArray();

        RaftRpcCodec.TryDecode(extended, out var decoded).Should().BeTrue(
            "known tag decodes its known prefix — future-added fields are extension bytes");
        decoded.Should().Be(new RequestVoteResp { Term = 2, Granted = true });
    }
}
