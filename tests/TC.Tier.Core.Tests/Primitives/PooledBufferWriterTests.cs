using System.Buffers;
using FluentAssertions;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// PooledBufferWriter 契约测试——租借写器三面：写入语义（GetSpan/Advance 窗口契约）、
/// 增长语义（内容保持 + 背板换租）、生命周期（Reset 复用 / Dispose 归还幂等）。
/// </summary>
public class PooledBufferWriterTests
{
    [Fact]
    public void 写入_窗口契约_Advance累计()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 64);
        writer.WrittenCount.Should().Be(0);

        var span = writer.GetSpan(4);
        span.Length.Should().BeGreaterThanOrEqualTo(4);
        span[0] = 0x01;
        span[1] = 0x02;
        writer.Advance(2);

        span = writer.GetSpan(0);   // 零提示也合法——续写同一窗口
        span[0] = 0x03;
        writer.Advance(1);

        writer.WrittenCount.Should().Be(3);
        writer.WrittenSpan.ToArray().Should().Equal(new byte[] { 0x01, 0x02, 0x03 });
    }

    [Fact]
    public void 写入_Advance越界_抛()
    {
        // ★ 池租借可能给大于请求的背板（桶对齐）——越界断言用远超容量的 Advance
        using var writer = new PooledBufferWriter(initialCapacity: 8);
        var act = () => writer.Advance(1000);
        act.Should().Throw<ArgumentOutOfRangeException>("Advance 窗口不得超背板容量");
    }

    [Fact]
    public void 增长_内容保持_容量倍增()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 8);
        var first = writer.GetSpan(8);
        first.Length.Should().BeGreaterThanOrEqualTo(8);
        for (var i = 0; i < 8; i++) first[i] = (byte)i;
        writer.Advance(8);

        // 跨板写入——增长必须保留已写内容（WrittenSpan 前缀视图语义）
        var second = writer.GetSpan(100);
        second.Length.Should().BeGreaterThanOrEqualTo(100);
        for (var i = 0; i < 100; i++) second[i] = (byte)(i + 1);
        writer.Advance(100);

        writer.WrittenCount.Should().Be(108);
        var all = writer.WrittenSpan.ToArray();
        all.Length.Should().Be(108);
        for (var i = 0; i < 8; i++) all[i].Should().Be((byte)i, "旧板内容随增长保持");
        for (var i = 0; i < 100; i++) all[8 + i].Should().Be((byte)(i + 1));
    }

    [Fact]
    public void 增长_负SizeHint_抛()
    {
        using var writer = new PooledBufferWriter();
        Action act = () => writer.GetSpan(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 生命周期_Reset保留背板_稳态复用()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 32);
        writer.GetSpan(32);
        writer.Advance(32);

        writer.Reset();
        writer.WrittenCount.Should().Be(0);

        var span = writer.GetSpan(32);
        span.Fill(0xAA);
        writer.Advance(16);
        writer.WrittenSpan.ToArray().Should().OnlyContain(b => b == 0xAA);
    }

    [Fact]
    public void 生命周期_Dispose幂等_后不可写()
    {
        var writer = new PooledBufferWriter(initialCapacity: 32);
        writer.GetSpan(32);
        writer.Advance(8);
        writer.Dispose();
        writer.Dispose();   // 幂等
        writer.WrittenCount.Should().Be(0);

        var act = () => writer.Advance(1);
        act.Should().Throw<ArgumentOutOfRangeException>("归还后背板空——Advance 越界兜底");
    }

    /// <summary>守恒断言（压测退出判例——租借面必须借还平衡）：自定义池统计 Rent/Return
    /// 次数与字节数，Dispose/增长换租后全部归位。</summary>
    [Fact]
    public void 生命周期_自定义池_借还平衡()
    {
        var pool = new CountingPool();
        var writer = new PooledBufferWriter(pool, initialCapacity: 16);
        writer.GetSpan(16);
        writer.Advance(16);
        writer.GetSpan(4096);   // 触发换租——旧板归还
        writer.Advance(4096);
        pool.RentCount.Should().Be(2, "初租 + 换租一次");
        pool.ReturnCount.Should().Be(1, "旧板已归还");
        writer.Dispose();
        pool.ReturnCount.Should().Be(2, "终板归还");
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        private readonly ArrayPool<byte> _inner = ArrayPool<byte>.Shared;

        public int RentCount;
        public int ReturnCount;

        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref RentCount);
            return _inner.Rent(minimumLength);
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Interlocked.Increment(ref ReturnCount);
            _inner.Return(array, clearArray);
        }
    }
}
