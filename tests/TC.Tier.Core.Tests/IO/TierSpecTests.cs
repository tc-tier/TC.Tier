using TC.Tier.Core.IO;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// TierSpec 单元测试（medium-protocol-and-parity-design §2.1/§7.3）——表驱动：
/// 方案甲语法（四本性头 + 二级首段）/ local 路径域四形态 / 快捷档 / 参数表（封闭键集 + ×介质合法性）/
/// 规范形往返稳定。
/// </summary>
public sealed class TierSpecTests
{
    // ═══════════════ local 路径域 ═══════════════

    [Fact]
    public void Local_PosixAbsolute()
    {
        var s = TierSpec.Parse("local:///var/lib/tier");
        Assert.Equal(StorageNature.Local, s.Nature);
        Assert.Equal("/var/lib/tier", s.AbsolutePath);
        Assert.False(s.IsCwdRoot);
        Assert.Null(s.UncHost);
        Assert.Null(s.RelativePath);
    }

    [Fact]
    public void Local_WindowsDrive_BackslashNormalized()
    {
        var s = TierSpec.Parse("local:///C:\\data\\tier");
        Assert.Equal("C:/data/tier", s.AbsolutePath);
    }

    [Fact]
    public void Local_WindowsDrive_TwoAndThreeSlashEquivalent()
    {
        Assert.Equal(TierSpec.Parse("local:///C:/data"), TierSpec.Parse("local://C:/data"));
    }

    [Fact]
    public void Local_Unc()
    {
        var s = TierSpec.Parse("local://fileserver/share/tier");
        Assert.Equal(StorageNature.Local, s.Nature);
        Assert.Equal("fileserver", s.UncHost);
        Assert.Equal("/share/tier", s.UncPath);
        Assert.Null(s.AbsolutePath);
    }

    [Fact]
    public void Local_Relative()
    {
        var s = TierSpec.Parse("local:data/tier");
        Assert.Equal("data/tier", s.RelativePath);
        Assert.False(s.IsCwdRoot);
    }

    [Fact]
    public void Local_ShortcutForms_AllCwdRoot()
    {
        Assert.True(TierSpec.Parse("local").IsCwdRoot);
        Assert.True(TierSpec.Parse("local:").IsCwdRoot);
        Assert.True(TierSpec.Parse("local?label=x").IsCwdRoot);
        Assert.True(TierSpec.Parse("local:?label=x").IsCwdRoot);
        Assert.Equal("x", TierSpec.Parse("local?label=x").Label);
    }

    // ═══════════════ memory / virtual / network ═══════════════

    [Fact]
    public void Memory_BareAndColon_Equivalent()
    {
        var bare = TierSpec.Parse("memory");
        var colon = TierSpec.Parse("memory:");
        Assert.Equal(StorageNature.Memory, bare.Nature);
        Assert.Equal(bare, colon);
    }

    [Fact]
    public void Memory_WithParams()
    {
        var s = TierSpec.Parse("memory?quota=1G&label=test-a");
        Assert.Equal(1L << 30, s.QuotaBytes);
        Assert.Equal("test-a", s.Label);
    }

    [Fact]
    public void Virtual_FileCarrier_DefaultSubKindNull()
    {
        var s = TierSpec.Parse("virtual:///data/vol.tier?label=wal");
        Assert.Equal(StorageNature.Virtual, s.Nature);
        Assert.Null(s.SubKind);
        Assert.Equal("/data/vol.tier", s.AbsolutePath);
        Assert.Equal("wal", s.Label);
    }

    [Fact]
    public void Virtual_DeviceCarrier_FirstSegment()
    {
        var s = TierSpec.Parse("virtual:///dev/nvme0n1?label=wal");
        Assert.Equal("dev", s.SubKind);
        Assert.Equal("/dev/nvme0n1", s.AbsolutePath);
    }

    [Fact]
    public void Virtual_WindowsFileCarrier()
    {
        var s = TierSpec.Parse("virtual:///D:/vols/archive.tier");
        Assert.Null(s.SubKind);
        Assert.Equal("D:/vols/archive.tier", s.AbsolutePath);
    }

    [Fact]
    public void Virtual_MultiCarrier_MemberList()
    {
        var s = TierSpec.Parse("virtual:///data/vol.tier?member=/data/v2.tier&member=/data/v3.tier");
        Assert.Equal(["/data/v2.tier", "/data/v3.tier"], s.Members);
    }

    [Fact]
    public void Network_FullForm()
    {
        var s = TierSpec.Parse("network:///s3/cos.example.com/tc-bucket/engine-a");
        Assert.Equal(StorageNature.Network, s.Nature);
        Assert.Equal("s3", s.SubKind);
        Assert.Equal("cos.example.com", s.Endpoint);
        Assert.Equal("tc-bucket", s.Bucket);
        Assert.Equal("engine-a", s.KeyPrefix);
    }

    [Fact]
    public void Network_EndpointWithPort()
    {
        var s = TierSpec.Parse("network:///s3/minio:9000/tier-logs/engine-a");
        Assert.Equal("minio:9000", s.Endpoint);
        Assert.Equal("tier-logs", s.Bucket);
        Assert.Equal("engine-a", s.KeyPrefix);
    }

    [Fact]
    public void Network_EmptyPrefix()
    {
        var s = TierSpec.Parse("network:///s3/host/bucket");
        Assert.Equal("", s.KeyPrefix);
    }

    // ═══════════════ 参数表（§2.5）═══════════════

    [Theory]
    [InlineData("100G", 100L << 30)]
    [InlineData("512M", 512L << 20)]
    [InlineData("16K", 16L << 10)]
    [InlineData("1T", 1L << 40)]
    [InlineData("1024", 1024L)]
    [InlineData("-1", -1L)]
    public void Quota_SizeForms(string value, long expected)
    {
        var s = TierSpec.Parse($"memory:?quota={value}");
        Assert.Equal(expected, s.QuotaBytes);
    }

    [Theory]
    [InlineData("ro", AccessMode.Read)]
    [InlineData("wo", AccessMode.Write)]
    [InlineData("rw", AccessMode.ReadWrite)]
    public void Access_ThreeStates(string value, AccessMode expected)
    {
        var s = TierSpec.Parse($"local:///x?access={value}");
        Assert.Equal(expected, s.Access);
    }

    [Fact]
    public void Spill_NestedSpec_LocalAndMemory()
    {
        var s = TierSpec.Parse("network:///s3/h/b/p?spill=local:///var/tmp");
        Assert.NotNull(s.Spill);
        Assert.Equal(StorageNature.Local, s.Spill.Nature);
        Assert.Equal("/var/tmp", s.Spill.AbsolutePath);

        var m = TierSpec.Parse("network:///s3/h/b/p?spill=memory:");
        Assert.Equal(StorageNature.Memory, m.Spill!.Nature);
    }

    [Fact]
    public void CredentialRef_MustBeEnvReference()
    {
        var s = TierSpec.Parse("network:///s3/h/b/p?cred=env:TIER_S3");
        Assert.Equal("env:TIER_S3", s.CredentialRef);
    }

    [Fact]
    public void FullParamSurface()
    {
        var s = TierSpec.Parse(
            "network:///s3/cos.example.com/tc-bucket/engine-a?vhost=1&label=prod&quota=100G&access=ro" +
            "&exclusive=1&spill=local:///var/tmp&cred=env:TIER_S3&region=cn-chengdu");
        Assert.True(s.VirtualHostAddressing);
        Assert.Equal("prod", s.Label);
        Assert.Equal(100L << 30, s.QuotaBytes);
        Assert.Equal(AccessMode.Read, s.Access);
        Assert.True(s.Exclusive);
        Assert.Equal(StorageNature.Local, s.Spill!.Nature);
        Assert.Equal("env:TIER_S3", s.CredentialRef);
        Assert.Equal("cn-chengdu", s.Region);
    }

    // ═══════════════ 规范形与往返 ═══════════════

    [Fact]
    public void CanonicalForms()
    {
        Assert.Equal("local:///var/lib/tier", TierSpec.Parse("local:///var/lib/tier").ToString());
        Assert.Equal("local:///C:/data", TierSpec.Parse("local://C:\\data").ToString());
        Assert.Equal("local://fileserver/share/tier", TierSpec.Parse("local://fileserver/share/tier").ToString());
        Assert.Equal("local:data/tier", TierSpec.Parse("local:data\\tier").ToString());
        Assert.Equal("local:", TierSpec.Parse("local").ToString());
        Assert.Equal("memory:", TierSpec.Parse("memory").ToString());
        Assert.Equal("virtual:///data/vol.tier", TierSpec.Parse("virtual:///data/vol.tier").ToString());
        Assert.Equal("virtual:///dev/nvme0n1", TierSpec.Parse("virtual:///dev/nvme0n1").ToString());
        Assert.Equal("network:///s3/h/b/p", TierSpec.Parse("network:///s3/h/b/p").ToString());
    }

    public static TheoryData<string> ValidSpecs => new()
    {
        "local:///var/lib/tier?label=prod-logs&quota=100G&access=ro&exclusive=1",
        "local:///C:\\data\\tier",
        "local://C:/data",
        "local://fileserver/share/tier",
        "local:data/tier",
        "local",
        "local:?label=x",
        "memory:",
        "memory?quota=1G&label=test-a",
        "virtual:///data/vol.tier?label=wal",
        "virtual:///dev/nvme0n1?label=wal",
        "virtual:///D:/vols/archive.tier",
        "virtual:///data/vol.tier?member=/data/v2.tier&member=/data/v3.tier",
        "network:///s3/cos.example.com/tc-bucket/engine-a",
        "network:///s3/minio:9000/tier-logs/engine-a",
        "network:///s3/host/bucket",
        "network:///s3/h/b/p?spill=local:///var/tmp",
        "network:///s3/h/b/p?spill=memory:&vhost=1&cred=env:TIER_S3&region=r1",
        "network:///s3/cos.example.com/tc-bucket/engine-a?vhost=1&label=prod&quota=100G&access=ro" +
            "&exclusive=1&spill=local:///var/tmp&cred=env:TIER_S3&region=cn-chengdu",
    };

    [Theory]
    [MemberData(nameof(ValidSpecs))]
    public void RoundTrip_CanonicalFormStable(string spec)
    {
        var once = TierSpec.Parse(spec);
        var canonical = once.ToString();
        var twice = TierSpec.Parse(canonical);
        Assert.Equal(canonical, twice.ToString());
        Assert.Equal(once, twice);
    }

    // ═══════════════ 非法形态（fail-fast 消歧）═══════════════

    [Fact]
    public void Local_TwoSegmentAfterSlashes_IsUnc()
    {
        // 双段 = 合法 UNC（host + 共享段）——与 fileserver/share 同构；误写相对路径会在构造时因主机不存在响亮失败
        var s = TierSpec.Parse("local://data/tier");
        Assert.Equal("data", s.UncHost);
        Assert.Equal("/tier", s.UncPath);
    }

    [Theory]
    [InlineData("disk:///x")]                                   // 未知 scheme（旧实现名——顶层只收四本性）
    [InlineData("")]                                            // 空
    [InlineData("local://data")]                                // 双斜杠后单段：既非盘符亦非完整 UNC——消歧保留形态
    [InlineData("local:///")]                                   // 三斜杠后为空
    [InlineData("local://server/")]                             // UNC 缺共享段
    [InlineData("network://s3/host/bucket")]                    // 缺协议首段（未三斜杠）
    [InlineData("network:///host/bucket")]                      // 缺协议（双斜杠直连端点）
    [InlineData("network:///s3/host")]                          // 缺桶
    [InlineData("network:///S3/host/bucket")]                   // 协议名大写非法
    [InlineData("network")]                                     // network 无裸形态
    [InlineData("virtual")]                                     // virtual 无裸形态
    [InlineData("virtual://")]                                  // 载体路径必填
    [InlineData("memory:rel")]                                  // memory 不占路径
    [InlineData("local:///var?foo=1")]                          // 未知参数（封闭键集）
    [InlineData("local:///var?access=rx")]                      // access 非法值
    [InlineData("memory:?quota=0")]                             // 0 不是合法上限
    [InlineData("memory:?quota=-5")]                            // 负值非法（-1 除外）
    [InlineData("memory:?quota=1X")]                            // 未知后缀
    [InlineData("network:///s3/h/b/p?cred=secret")]             // 凭证携值——必须 env: 引用
    [InlineData("network:///s3/h/b/p?cred=env:")]               // 引用名为空
    [InlineData("local:///x?label=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33 字节超限
    [InlineData("network:///s3/h/b/p?spill=virtual:///x")]      // spill 目标限 local/memory
    [InlineData("local:///x?member=/m")]                        // member 仅 virtual
    [InlineData("memory:?cred=env:K")]                          // cred 仅 network
    [InlineData("local:///x?label=a&label=b")]                  // 参数重复——一词一形
    [InlineData("memory:?quota=-1&quota=100G")]                 // 显式 -1 后再写——重复检测
    [InlineData("local:///var?label")]                          // 缺 '='
    [InlineData("local:///var?label=")]                         // 值不可为空
    public void Invalid_FailFast(string spec)
    {
        Assert.Throws<FormatException>(() => TierSpec.Parse(spec));
    }
}
