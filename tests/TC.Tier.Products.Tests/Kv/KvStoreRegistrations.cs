using TC.Tier.CodeGen;

// ★ [KvStore] 端到端契约注册（tierkv-design.md §4 W1 阶段2）——测试程序集一行声明，生成器在
//   本程序集编译期产出封闭形态（TierKvOfLongLong 等）：编译通过本身即生成器契约
//   （RingClosedSpecializationTests 同款验收）；功能面见 TierKvClosedFormTests。
//   五条标注覆盖全部发射分支：unmanaged 缺省 / byte / byte[] / 自定义 formatter（显式胜出）/
//   IndexKind 编译期缺省（BTree——独立 (Key,Value) 对避免同对去重吞行）。
[assembly: KvStore(typeof(long), typeof(long))]
[assembly: KvStore(typeof(long), typeof(byte))]
[assembly: KvStore(typeof(long), typeof(byte[]))]
[assembly: KvStore(typeof(long), typeof(double), IndexKind = (int)TC.Tier.Products.Kv.KvIndexKind.BTree)]
[assembly: KvStore(typeof(TC.Tier.Products.Tests.Kv.TestKey), typeof(TC.Tier.Products.Tests.Kv.TestPayload),
    Formatter = typeof(TC.Tier.Products.Tests.Kv.TestPayloadFormatter))]

namespace TC.Tier.Products.Tests.Kv;

/// <summary>双字段结构体 key（unmanaged + record struct 判等闭环——D8 结构体 key 表达）。</summary>
public readonly record struct TestKey(long Id, int Tag);

/// <summary>string 承载值类型（managed——内建覆盖面之外，必须显式 formatter——TCSG022 守卫的端到端正例）。</summary>
public readonly record struct TestPayload(string Text);

/// <summary>TestPayload 的 UTF-8 formatter（[KvStore] Formatter= 显式声明——生成器校验接口实现）。</summary>
public sealed class TestPayloadFormatter : IValueFormatter<TestPayload>
{
    public int GetSize(TestPayload value) => System.Text.Encoding.UTF8.GetByteCount(value.Text);
    public void Format(TestPayload value, System.Span<byte> destination)
        => System.Text.Encoding.UTF8.GetBytes(value.Text).CopyTo(destination);
    public TestPayload Parse(System.ReadOnlySpan<byte> source)
        => new TestPayload(System.Text.Encoding.UTF8.GetString(source));
}
