using System.Text;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// IObjectStore 契约族（B3.0②）——类型级校验行为单测：元数据键字符集/2KB 超限不截断、
/// 对象键共享校验规则（§9.5 契约冻结项）、条件类型值语义。
/// 实现级语义（六件套/条件写矩阵）归 ObjectStoreContractTests（B3.1 参数化平权套）。
/// </summary>
public class ObjectStoreContractTypesTests
{
    // ═══════════════ ObjectMetadata：键字符集（早失败——写入时即抛，不静默转义）═══════════════

    [Theory]
    [InlineData("engine-meta")]
    [InlineData("Engine_Meta.2")]
    [InlineData("a")]
    public void Create_LegalKeys_Accepted(string key)
    {
        var act = () => ObjectMetadata.Create(new Dictionary<string, string> { [key] = "v" });
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("meta key")]        // 空格——非 token 字符集
    [InlineData("meta:key")]        // 冒号（PathValidator 非法集同源）
    [InlineData("元数据")]           // 非 ASCII
    [InlineData("meta/key")]
    [InlineData("meta*")]
    [InlineData("")]                // 空键
    public void Create_IllegalKeys_ThrowsArgument(string key)
    {
        var act = () => ObjectMetadata.Create(new Dictionary<string, string> { [key] = "v" });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_SingleIllegalKey_FailsWholeMetadata()
    {
        var dict = new Dictionary<string, string> { ["good-key"] = "v", ["bad key"] = "v" };
        var act = () => ObjectMetadata.Create(dict);
        act.Should().Throw<ArgumentException>();
    }

    // ═══════════════ ObjectMetadata：2KB 上限（不静默截断——关键元数据被截 = 恢复失败）═══════════════

    [Fact]
    public void Create_OverLimit_Throws_NotTruncated()
    {
        // 单键单值 ~2000 字节（含 x-amz-meta- 前缀开销 11B ≤ 2048）→ 合法
        var within = new Dictionary<string, string> { ["k"] = new string('a', 2048 - 11 - 1) };
        ObjectMetadata.Create(within).Should().NotBeNull();

        var over = new Dictionary<string, string> { ["k"] = new string('a', 2048 - 11 - 1 + 1) };
        var act = () => ObjectMetadata.Create(over);
        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("2048");
    }

    [Fact]
    public void Create_ManySmallKeys_AggregatesOverLimit_Throws()
    {
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < 100; i++)
            dict[$"key-{i:D3}"] = new string('x', 40);   // 100 × (7+40+11) ≈ 5800B
        var act = () => ObjectMetadata.Create(dict);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_IsSharedImmutableSingleton()
    {
        ObjectMetadata.Empty.Should().BeSameAs(ObjectMetadata.Create(null));
        ObjectMetadata.Empty.UserMetadata.Count.Should().Be(0);
        ObjectMetadata.Create(new Dictionary<string, string>()).Should().BeSameAs(ObjectMetadata.Empty);
    }

    // ═══════════════ ObjectKeyValidator（§9.5 契约冻结：UTF-8 / ≤1024 字节 / 禁控制字符）═══════════════

    [Fact]
    public void Key_NullOrEmpty_Throws()
    {
        ((Action)(() => ObjectKeyValidator.Validate(null!))).Should().Throw<ArgumentException>();
        ((Action)(() => ObjectKeyValidator.Validate(""))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Key_ControlChars_Throws()
    {
        ((Action)(() => ObjectKeyValidator.Validate("a\0b"))).Should().Throw<ArgumentException>();
        ((Action)(() => ObjectKeyValidator.Validate("a\r\nb"))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Key_Over1024Bytes_Throws()
    {
        var key = new string('k', ObjectKeyValidator.MaxKeyBytes + 1);   // ASCII 1B/字符
        ((Action)(() => ObjectKeyValidator.Validate(key))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Key_Utf8ByteCountIsTheLimit_NotCharCount()
    {
        // 中文 3B/字符：342 字符 = 1026 字节 > 1024（字符数 < 1024 仍拒——字节口径）
        var key = new string('键', ObjectKeyValidator.MaxKeyBytes / 3 + 1);
        key.Length.Should().BeLessThan(ObjectKeyValidator.MaxKeyBytes);
        Encoding.UTF8.GetByteCount(key).Should().BeGreaterThan(ObjectKeyValidator.MaxKeyBytes);
        ((Action)(() => ObjectKeyValidator.Validate(key))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Key_AtExactLimit_Accepted()
    {
        var key = new string('k', ObjectKeyValidator.MaxKeyBytes);
        ((Action)(() => ObjectKeyValidator.Validate(key))).Should().NotThrow();
    }

    // ═══════════════ 条件类型（值语义——fencing 原语的取值面）═══════════════

    [Fact]
    public void Conditions_AreValueRecords()
    {
        var a = new PutCondition(IfMatch: "\"etag1\"", IfNoneMatch: null);
        var b = new PutCondition(IfMatch: "\"etag1\"", IfNoneMatch: null);
        a.Should().Be(b);

        var d1 = new DeleteCondition(IfMatch: "t");
        var d2 = new DeleteCondition(IfMatch: "t2");
        d1.Should().NotBe(d2);
    }

    // ═══════════════ 能力位枚举（值冻结——新增从 1<<8 起，不动既有位）═══════════════

    [Fact]
    public void Capabilities_BitValues_Frozen()
    {
        ((int)ObjectStoreCapabilities.ConditionalPut).Should().Be(1 << 0);
        ((int)ObjectStoreCapabilities.ServerSideCopy).Should().Be(1 << 1);
        ((int)ObjectStoreCapabilities.StrongList).Should().Be(1 << 2);
        ((int)ObjectStoreCapabilities.Multipart).Should().Be(1 << 3);
        ((int)ObjectStoreCapabilities.RangeGet).Should().Be(1 << 4);
        ((int)ObjectStoreCapabilities.Appendable).Should().Be(1 << 5);
        ((int)ObjectStoreCapabilities.ObjectLock).Should().Be(1 << 6);
        ((int)ObjectStoreCapabilities.ConditionalDelete).Should().Be(1 << 7);
    }
}
