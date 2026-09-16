using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// 条目种类常量契约（wire/storage 稳定值——值漂移 = 存量日志/快照区全损，锁死即契约）。
/// </summary>
public class RaftEntryKindTests
{
    [Fact]
    public void 常量值_稳定锁死()
    {
        ((int)RaftEntryKind.Command).Should().Be(0, "Command = 0（wire/storage 稳定值——漂移 = 存量条目全损）");
        ((int)RaftEntryKind.Config).Should().Be(1, "Config = 1（wire/storage 稳定值）");
    }

    [Fact]
    public void 常量值_两两相异()
    {
        RaftEntryKind.Command.Should().NotBe(RaftEntryKind.Config);
    }
}
