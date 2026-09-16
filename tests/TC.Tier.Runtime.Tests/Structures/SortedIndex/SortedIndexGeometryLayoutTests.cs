using FluentAssertions;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.SortedIndex.Layout;
using Xunit;

namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// 几何块布局契约钉（[BinaryLayout] 迁移的尺寸锚）：三族几何 struct 生成 StructSize 必须
/// 恒等于族契约 32B（SortedIndexConstants/ProbingIndexFormat.GeometrySize）——声明漂移即红。
/// </summary>
public class SortedIndexGeometryLayoutTests
{
    [Fact]
    public void BTreeGeometry_StructSize_MatchesFamilyContract()
        => BTreeIndexGeometryCodec.StructSize.Should().Be(SortedIndexConstants.GeometrySize);

    [Fact]
    public void SkipListGeometry_StructSize_MatchesFamilyContract()
        => SkipListIndexGeometryCodec.StructSize.Should().Be(SortedIndexConstants.GeometrySize);

    [Fact]
    public void HashGeometry_StructSize_MatchesFamilyContract()
        => HashIndexGeometryCodec.StructSize.Should().Be(ProbingIndexFormat.GeometrySize);
}
