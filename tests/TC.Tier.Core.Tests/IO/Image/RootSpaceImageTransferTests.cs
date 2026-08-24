using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Mem;

namespace TC.Tier.Core.Tests.IO.Image;

/// <summary>
/// Transfer 快道路由契约测试（设计 §6.2）——P1 三态均不置位 ContiguousCapture（Reserved Mem 是
/// 每文件连续而非整卷连续），路由用测试替身验证；P2 Raw 介质落地即点亮真快道。
/// </summary>
public sealed class RootSpaceImageTransferTests
{
    /// <summary>
    /// 连续卷测试替身：命名空间平面透传 MemoryFileSystem（结构化回退用），
    /// 载体 = 自持 MemoryStream（OpenRawBacking 直达载体——模拟真介质在维护租约内的载体逃生口，
    /// 不经句柄平面，§2.4/§6.2）。人工置位 ContiguousCapture。
    /// </summary>
    private sealed class FakeContiguousVolume : IFileSystem, IContiguousVolume
    {
        private readonly MemoryFileSystem _inner = MemoryFileSystem.New();
        private readonly MemoryStream _carrier = new();

        public FileSystemCapabilities Capabilities =>
            _inner.Capabilities | FileSystemCapabilities.ContiguousCapture;

        public void SeedCarrier(byte[] data)
        {
            _carrier.SetLength(0);
            _carrier.Write(data, 0, data.Length);
        }

        public byte[] CarrierBytes()
        {
            _carrier.Position = 0;
            var b = new byte[_carrier.Length];
            _carrier.ReadExactly(b);
            return b;
        }

        public Stream OpenRawBacking(bool writable)
            => new CarrierStream(_carrier, writable);

        public void OnMirrorCompleted()
        {
            // 替身：载体即权威（CarrierStream Dispose 已写回）——无需重载
        }

        /// <summary>载体视图流——构造时从载体播种；Dispose 时（租约内——先于租约释放）写回载体。</summary>
        private sealed class CarrierStream : MemoryStream
        {
            private readonly MemoryStream _owner;
            private readonly bool _writable;

            public CarrierStream(MemoryStream owner, bool writable)
            {
                _owner = owner;
                _writable = writable;
                var seed = owner.ToArray();
                Write(seed, 0, seed.Length);
                Position = 0;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _writable)
                {
                    var b = ToArray();
                    _owner.SetLength(0);
                    _owner.Write(b, 0, b.Length);
                }
                base.Dispose(disposing);
            }
        }

        public VolumeInfo Volume => _inner.Volume;
        public IFileHandle Open(string path, FileOpenOptions options) => _inner.Open(path, options);
        public void EnsureRoot() => _inner.EnsureRoot();
        public void FlushRoot() => _inner.FlushRoot();
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public void MoveDirectory(string source, string dest) => _inner.MoveDirectory(source, dest);
        public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default)
            => _inner.CreateFile(path, preallocateSize, extra);
        public bool Exists(string path) => _inner.Exists(path);
        public void Delete(string path) => _inner.Delete(path);
        public void Move(string source, string dest, bool overwrite = false) => _inner.Move(source, dest, overwrite);
        public FsEntryInfo Stat(string path) => _inner.Stat(path);
        public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
            => _inner.EnumerateFiles(pattern, recursive);
        public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
            => _inner.EnumerateFiles(path, pattern, recursive);
        public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
            => _inner.EnumerateDirectories(pattern, recursive);
        public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
            => _inner.EnumerateDirectories(path, pattern, recursive);
        public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
            => _inner.EnumerateEntries(pattern, recursive);
        public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
            => _inner.EnumerateEntries(path, pattern, recursive);
        public IDisposable AcquireExclusive(TimeSpan timeout) => _inner.AcquireExclusive(timeout);
        public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
            => _inner.EnterMaintenance(reason, scope, ct);

        public void Dispose() => _inner.Dispose();
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void BothContiguous_RoutesToFastPath_ByteMirror()
    {
        using var src = new FakeContiguousVolume();
        using var dst = new FakeContiguousVolume();
        var payload = new byte[100_000];
        new Random(42).NextBytes(payload);
        src.SeedCarrier(payload);
        dst.SeedCarrier(new byte[payload.Length]);   // 真实卷几何：目标容量定长（D6 预检基线）

        var summary = RootSpaceImage.Transfer(src, dst);

        summary.RawBytes.Should().Be(payload.Length, "快道 = 整卷字节镜像（长度对账）");
        summary.EntryCount.Should().Be(0, "字节镜像产物——条目数无意义（设计 §6.2）");
        dst.CarrierBytes().Should().BeEquivalentTo(payload, "字节级镜像");
    }

    [Fact]
    public void FastPath_TargetSmallerThanSource_RejectedBeforeAnyWrite()
    {
        using var src = new FakeContiguousVolume();
        using var dst = new FakeContiguousVolume();
        var payload = new byte[100_000];
        new Random(42).NextBytes(payload);
        src.SeedCarrier(payload);
        dst.SeedCarrier(new byte[1000]);   // 目标容量 1000 < 源 100000——预检必须先行拒绝

        var act = () => RootSpaceImage.Transfer(src, dst);

        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.DiskFull,
            "整卷覆盖是破坏性操作——容量预检先拒后写（设计 §10/D6）");
        dst.CarrierBytes().Should().OnlyContain(b => b == 0, "预检拒绝——目标未受任何写入");
    }

    [Fact]
    public void SourceOnlyContiguous_FallsBackToStructural()
    {
        using var src = new FakeContiguousVolume();
        using var dst = MemoryFileSystem.New();   // 未置位 → 结构化回退
        src.EnsureRoot();
        dst.EnsureRoot();
        using (var h = src.Open("data", RWOpts()))
            h.Write(0, new byte[1000]);

        var summary = RootSpaceImage.Transfer(src, dst);

        summary.EntryCount.Should().BeGreaterThan(0, "结构化产物（条目数有意义）——非字节镜像");
        dst.Exists("data").Should().BeTrue();
    }

    [Fact]
    public void MemVolumes_NotContiguous_AlwaysStructural()
    {
        using var src = MemoryFileSystem.New();
        using var dst = MemoryFileSystem.New();
        src.EnsureRoot();
        using (var h = src.Open("f", RWOpts()))
            h.Write(0, new byte[64]);

        var summary = RootSpaceImage.Transfer(src, dst);

        src.Capabilities.HasFlag(FileSystemCapabilities.ContiguousCapture).Should()
            .BeFalse("Mem-Reserved 是每文件连续而非整卷连续（设计 §6.1 勘误）");
        summary.EntryCount.Should().Be(1, "结构化路径");
        dst.Exists("f").Should().BeTrue();
    }

    [Fact]
    public void FastPath_LeaseReleasedAfterTransfer()
    {
        using var src = new FakeContiguousVolume();
        using var dst = new FakeContiguousVolume();
        src.SeedCarrier(new byte[32]);
        dst.SeedCarrier(new byte[32]);   // 目标容量 ≥ 源（D6 预检）

        RootSpaceImage.Transfer(src, dst);

        var act = () => src.Open("post-transfer", RWOpts());
        act.Should().NotThrow("转移结束租约 RAII 释放——源恢复可写");
    }
}
