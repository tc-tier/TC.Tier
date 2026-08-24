using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// FileOpenOptions 单元测试——值相等（池前提，建设期第一优先 ④）+ 组合合法性校验（⑬）。
/// </summary>
public sealed class FileOpenOptionsTests
{
    [Fact]
    public void ValueEquality_SameFields_EqualAndSameHashCode()
    {
        var a = new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.Read, Hints = FileOpenHints.NoBuffering, PreallocateSize = 4096,
        };
        var b = new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.Read, Hints = FileOpenHints.NoBuffering, PreallocateSize = 4096,
        };

        b.Should().Be(a);
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void ValueEquality_DifferentPreallocateSize_NotEqual()
    {
        // PreallocateSize 参与值相等（完整打开意图比较）——与池 key（排除预分配）是两套各自场景的相等性
        var a = new FileOpenOptions { Access = AccessMode.Write, PreallocateSize = 0 };
        var b = new FileOpenOptions { Access = AccessMode.Write, PreallocateSize = 4096 };

        b.Should().NotBe(a);
    }

    [Fact]
    public void ValueEquality_DifferentHints_NotEqual()
    {
        var a = new FileOpenOptions { Access = AccessMode.Read, Hints = FileOpenHints.None };
        var b = new FileOpenOptions { Access = AccessMode.Read, Hints = FileOpenHints.SequentialScan };

        b.Should().NotBe(a);
    }

    [Theory]
    [InlineData(FileOpenMode.Append, AccessMode.Read)]        // Append 须写权限
    [InlineData(FileOpenMode.Truncate, AccessMode.Read)]      // 截断须写权限
    [InlineData(FileOpenMode.CreateNew, AccessMode.Read)]     // 新建须写权限
    [InlineData(FileOpenMode.OpenOrCreate, AccessMode.Read)]  // 开或建须写权限
    public void Validate_WriteModeWithReadAccess_Throws(FileOpenMode mode, AccessMode access)
    {
        var options = new FileOpenOptions { Mode = mode, Access = access };
        var act = () => options.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_AppendWithWriteOrReadWrite_Legal()
    {
        var act1 = () => new FileOpenOptions { Mode = FileOpenMode.Append, Access = AccessMode.Write }.Validate();
        var act2 = () => new FileOpenOptions { Mode = FileOpenMode.Append, Access = AccessMode.ReadWrite }.Validate();
        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }

    [Fact]
    public void Validate_NoBufferingPlusWriteThrough_CanStack()
    {
        // DIO + 写透可叠加（正交提示）
        var act = () => new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Hints = FileOpenHints.NoBuffering | FileOpenHints.WriteThrough,
        }.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NegativePreallocateSize_Throws()
    {
        var act = () => new FileOpenOptions { Access = AccessMode.Write, PreallocateSize = -1 }.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_ReadOpenExisting_Legal()
    {
        var act = () => new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Read }.Validate();
        act.Should().NotThrow();
    }
}

/// <summary>
/// PathValidator 单元测试——R5 七条共享规则的拒绝集（㉛：两介质同实现同断言——介质侧参数化测试另有专项）。
/// </summary>
public sealed class PathValidatorTests
{
    private const string Root = "/data/vol";





    // ═══════════ ValidateRelative（根空间层级路径——filesystem-root-space-design §4）═══════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Relative_Rejects_NullOrWhitespace(string? path)
    {
        var act = () => PathValidator.ValidateRelative(path!, Root);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a\\b")]          // 反斜杠（'/' 唯一合法分隔符）
    [InlineData("/a")]            // 首分隔符（绝对化）
    [InlineData("a/")]            // 尾分隔符（空尾组件）
    [InlineData("a//b")]          // 连续分隔符（空组件）
    [InlineData("//")]            // 纯分隔符
    [InlineData("..")]            // 越根组件
    [InlineData(".")]             // 无意义组件
    [InlineData("a/../b")]        // 中段越根
    [InlineData("./a")]           // 前导相对组件
    [InlineData("a/..")]          // 尾部越根
    [InlineData("C:file")]        // 盘符
    [InlineData("C:/a")]          // 盘符绝对路径
    [InlineData("a<b")]           // 保留字符集
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\0b")]          // NUL
    [InlineData("a/b\0c")]        // 深层组件 NUL
    public void Relative_Rejects_IllegalPath(string path)
    {
        var act = () => PathValidator.ValidateRelative(path, Root);
        act.Should().Throw<ArgumentException>($"路径 {path} 应被拒绝");
    }

    [Theory]
    [InlineData("file")]                    // 单组件（旧扁平名——规则子集）
    [InlineData("a/b")]                     // 一层
    [InlineData("struct1/eng0/data.0")]     // 引擎布局形态
    [InlineData("struct1/eng0/compact/tmp.0")]
    [InlineData(".tier-volume-lock")]       // 点前缀单组件（卷锁/sidecar 合法）
    [InlineData("a/.data.0")]              // 深层点前缀（sidecar 形态）
    [InlineData("data.compact.marker")]    // 点分隔多段名
    public void Relative_Accepts_LegalHierarchical(string path)
    {
        var act = () => PathValidator.ValidateRelative(path, Root);
        act.Should().NotThrow($"路径 {path} 应合法");
    }

    [Fact]
    public void Relative_Rejects_ComponentTooLong()
    {
        var path = $"ok/{new string('a', PathValidator.MaxComponentLength + 1)}";
        var act = () => PathValidator.ValidateRelative(path, Root);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Relative_Rejects_CombinedTooLong()
    {
        var longRoot = new string('r', PathValidator.MaxCombinedLength);
        var act = () => PathValidator.ValidateRelative("a/b", longRoot);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("seg-000001.data")]
    [InlineData("meta")]
    [InlineData("A1.B2")]
    [InlineData("a..b")]        // .. 在组件内部（非独立组件）——合法
    [InlineData("sp ace")]      // 空格在中间——合法
    public void Accepts_LegalNames(string path)
    {
        var act = () => PathValidator.ValidateRelative(path, Root);
        act.Should().NotThrow();
    }

    [Fact]
    public void MaxComponentLength_Exactly255_Accepted()
    {
        var path = new string('a', PathValidator.MaxComponentLength);
        var act = () => PathValidator.ValidateRelative(path, Root);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRoot_EmptyOrWhitespace_Throws()
    {
        ((Action)(() => PathValidator.ValidateRoot(""))).Should().Throw<ArgumentException>();
        ((Action)(() => PathValidator.ValidateRoot("  "))).Should().Throw<ArgumentException>();
        ((Action)(() => PathValidator.ValidateRoot("/data/vol"))).Should().NotThrow();
    }
}

/// <summary>
/// IOExceptionMapper 单元测试——Win32 / errno 交集码表逐项断言（现 Storage 逻辑无专属单测，借此补上）。
/// </summary>
public sealed class IOExceptionMapperTests
{
    [Theory]
    [InlineData(2, (int)IOError.NotFound)]              // ENOENT / ERROR_FILE_NOT_FOUND
    [InlineData(13, (int)IOError.AccessDenied)]         // EACCES
    [InlineData(16, (int)IOError.SharingViolation)]     // EBUSY
    [InlineData(28, (int)IOError.DiskFull)]             // ENOSPC
    [InlineData(87, (int)IOError.AlignmentError)]       // ERROR_INVALID_PARAMETER（EINVAL）
    [InlineData(112, (int)IOError.DiskFull)]            // ERROR_DISK_FULL
    [InlineData(183, (int)IOError.AlreadyExists)]       // ERROR_ALREADY_EXISTS
    [InlineData(80, (int)IOError.AlreadyExists)]        // ERROR_FILE_EXISTS
    [InlineData(1117, (int)IOError.IOFailure)]          // ERROR_IO_DEVICE
    [InlineData(9999, (int)IOError.Unknown)]            // 未分类
    public void ClassifyHResult_Win32AndErrnoIntersection(int code, int expected)
    {
        IOExceptionMapper.ClassifyHResult(code).Should().Be((IOError)expected);
    }

    [Fact]
    public void ClassifyHResult_Code5_FacilityDisambiguates_EioVsAccessDenied()
    {
        // facility=0（POSIX errno）→ EIO；facility≠0（Win32）→ ACCESS_DENIED
        IOExceptionMapper.ClassifyHResult(unchecked((int)0x00000005)).Should().Be(IOError.IOFailure);
        IOExceptionMapper.ClassifyHResult(unchecked((int)0x80070005)).Should().Be(IOError.AccessDenied);
    }

    [Fact]
    public void Classify_ByExceptionType()
    {
        IOExceptionMapper.Classify(new OperationCanceledException()).Should().Be(IOError.Cancelled);
        IOExceptionMapper.Classify(new ArgumentException("x")).Should().Be(IOError.AlignmentError);
        IOExceptionMapper.Classify(new UnauthorizedAccessException()).Should().Be(IOError.AccessDenied);
        IOExceptionMapper.Classify(new FileNotFoundException()).Should().Be(IOError.NotFound);
        IOExceptionMapper.Classify(new Exception("boom")).Should().Be(IOError.Unknown);
    }

    [Fact]
    public void Wrap_CarriesErrorPathAndOperation()
    {
        var inner = new IOException("disk on fire", unchecked((int)0x80070070)); // ERROR_DISK_FULL
        var wrapped = inner.Wrap("PunchHole", "f.data");

        wrapped.Should().BeOfType<FileIOException>();
        wrapped.Error.Should().Be(IOError.DiskFull);
        wrapped.Path.Should().Be("f.data");
        wrapped.Operation.Should().Be("PunchHole");
        wrapped.InnerException.Should().Be(inner);
        wrapped.Message.Should().Contain("PunchHole failed");
    }

    [Fact]
    public void FileIOException_ReservedOffsetAndCompletedLength_RoundTrip()
    {
        var ex = new FileIOException(IOError.IOFailure, "Append failed", "f.data", "Append")
        {
            ReservedOffset = 4096,
        };
        ex.ReservedOffset.Should().Be(4096);
        ex.CompletedLength.Should().BeNull();
        ex.ToString().Should().Contain("reservedOffset=4096");

        var ex2 = new FileIOException(IOError.IOFailure, "CopyRange failed", "f.data", "CopyRange")
        {
            CompletedLength = 8192,
        };
        ex2.CompletedLength.Should().Be(8192);
        ex2.ReservedOffset.Should().BeNull();
        ex2.ToString().Should().Contain("completedLength=8192");
    }
}
