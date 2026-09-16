namespace TC.Tier.Core.Tests.NativeInterop;

public class WindowsProcessorAffinityTests
{
    private static readonly uint[] HeterogeneousGroups = [2, 4, 1];

    [Fact]
    public void GetNumGroupsProcsPerGroup_WindowsTopology_IsValid()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (groupCount, procsPerGroup) = Kernel32.GetNumGroupsProcsPerGroup();

        groupCount.Should().BeGreaterThan(0);
        procsPerGroup.Should().BeInRange(1, 64);
    }

    [Fact]
    public void MapProcessorRoundRobin_HeterogeneousGroups_UsesEveryProcessor()
    {
        var mapped = Enumerable.Range(0, 8)
            .Select(index => Kernel32.MapProcessorRoundRobin((uint)index, HeterogeneousGroups));

        mapped.Should().Equal(
            ((ushort)0, 0u),
            ((ushort)0, 1u),
            ((ushort)1, 0u),
            ((ushort)1, 1u),
            ((ushort)1, 2u),
            ((ushort)1, 3u),
            ((ushort)2, 0u),
            ((ushort)0, 0u));
    }

    [Fact]
    public void MapProcessorSharded_HeterogeneousGroups_SkipsMissingProcessors()
    {
        var mapped = Enumerable.Range(0, 8)
            .Select(index => Kernel32.MapProcessorSharded((uint)index, HeterogeneousGroups));

        mapped.Should().Equal(
            ((ushort)0, 0u),
            ((ushort)1, 0u),
            ((ushort)2, 0u),
            ((ushort)0, 1u),
            ((ushort)1, 1u),
            ((ushort)1, 2u),
            ((ushort)1, 3u),
            ((ushort)0, 0u));
    }

    [Theory]
    [InlineData(new uint[0])]
    [InlineData(new uint[] { 0 })]
    [InlineData(new uint[] { 65 })]
    public void MapProcessorRoundRobin_InvalidTopology_Throws(uint[] groupProcessorCounts)
    {
        var map = () => Kernel32.MapProcessorRoundRobin(0, groupProcessorCounts);

        map.Should().Throw<ArgumentException>();
    }
}
