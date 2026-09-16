using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W4 契约测试——IKvFunctions 完整模型 + RMW 三流（tierkv-design.md §1/§3.3，FASTER IFunctions
/// 语义逐条对齐）：Initial（无值造值）/Copy（不可变折叠——旧记录保留=版本历史）/InPlace（追加前
/// 最后折叠语义位，false 回落 Copy——FASTER mutable→immutable 派生序）+ 读钩子档位派发
/// （Serializable→SingleReader/其余→ConcurrentReader）+ 写钩子校验面 + 完成回调状态回传。
/// </summary>
/// <remarks>Counter 形态：TValue=long 累加器，TInput=long 增量，TOutput=折叠产物，TContext=string?。</remarks>
public class KvFunctionsTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-w4-" + suffix);

    private static async Task<TierKvOfLongLong> NewKvAsync(TestVolume vol)
        => await TierKvOfLongLong.CreateAsync(vol.Fs, Opts(Guid.NewGuid().ToString("N")[..8]));

    /// <summary>录制型 Counter Functions——全钩子留痕（派发序/状态/结果位可编程）。</summary>
    private sealed class CounterFunctions : KvFunctionsBase<long, long, long, long, string?>
    {
        public readonly List<string> Log = new();
        public bool InitialResult = true;
        public bool InPlaceResult = true;
        public bool CopyResult = true;
        public bool SingleWriterResult = true;
        public bool ConcurrentWriterResult = true;

        public override bool InitialUpdater(ref long key, ref long input, ref long value, ref long output, ref string? context)
        {
            Log.Add("Initial");
            if (!InitialResult) return false;
            value = input;
            output = value;
            return true;
        }

        public override bool InPlaceUpdater(ref long key, ref long input, ref long value, ref long output, ref string? context)
        {
            Log.Add("InPlace");
            if (!InPlaceResult) return false;
            value += input;
            output = value;
            return true;
        }

        public override bool CopyUpdater(ref long key, ref long input, ref long oldValue, ref long newValue, ref long output, ref string? context)
        {
            Log.Add("Copy");
            if (!CopyResult) return false;
            newValue = oldValue + input;
            output = newValue;
            return true;
        }

        public override void ConcurrentReader(ref long key, ref long input, ref long value, ref long output, ref string? context)
        {
            Log.Add("ConcurrentReader");
            output = value;
        }

        public override void SingleReader(ref long key, ref long input, ref long value, ref long output, ref string? context)
        {
            Log.Add("SingleReader");
            output = value;
        }

        public override bool SingleWriter(ref long key, ref long value)
        {
            Log.Add("SingleWriter");
            return SingleWriterResult;
        }

        public override bool ConcurrentWriter(ref long key, ref long value)
        {
            Log.Add("ConcurrentWriter");
            return ConcurrentWriterResult;
        }

        public override void RmwCompletionCallback(ref long output, KvStatus status, string? context)
            => Log.Add($"RmwCb:{status}:{output}");

        public override void ReadCompletionCallback(ref long key, ref long input, ref long output, KvStatus status, string? context)
            => Log.Add($"ReadCb:{status}:{output}");

        public override void UpsertCompletionCallback(ref long key, ref long value, KvStatus status, string? context)
            => Log.Add($"UpsertCb:{status}");

        public override void DeleteCompletionCallback(ref long key, KvStatus status, string? context)
            => Log.Add($"DeleteCb:{status}");
    }

    // ═══ RMW 三流 ═══

    [Fact]
    public async Task Rmw_InitialFlow_CreatesValue_Miss()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        var f = new CounterFunctions();

        var status = await kv.RmwAsync(1, input: 42L, f, context: "ctx");

        status.Should().Be(KvStatus.Ok, "Initial 流造值成功");
        f.Log.Should().Equal("Initial", "RmwCb:Ok:42");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(42L, "造值经 Format→追加→换绑可读回");
    }

    [Fact]
    public async Task Rmw_CopyFlow_FoldsExistingValue_PreservesOldRecord()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        await kv.PutFormattedAsync(1, 100L);
        var oldAddr = kv.FindAddress(1);

        var f = new CounterFunctions { InPlaceResult = false };   // 强制走 Copy 流
        var status = await kv.RmwAsync(1, input: 5L, f, context: "ctx");

        status.Should().Be(KvStatus.Ok);
        f.Log.Should().Contain("Copy");
        var copyLog = f.Log.Single(e => e == "Copy");
        copyLog.Should().NotBeNull();
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(105L, "Copy 流折叠 oldValue+input");

        // 版本历史保留（append-only 多出能力）：旧记录仍在 Ring 真源可解析
        kv.FindAddress(1).Should().NotBe(oldAddr, "换绑指向新记录");
        kv.Ring.TryGetKey(oldAddr, out var oldKey).Should().BeTrue("旧记录保留——真源不可变");
        oldKey.Should().Be(1);
    }

    [Fact]
    public async Task Rmw_InPlaceFirst_FallbackToCopy_MatchesFasterDerivation()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        await kv.PutFormattedAsync(1, 100L);

        // 命中 → InPlace 先征询（追加前最后折叠）；接受 → Copy 不征询
        var f1 = new CounterFunctions();
        await kv.RmwAsync(1, input: 1L, f1, context: "ctx");
        f1.Log.Should().Equal("InPlace", "RmwCb:Ok:101");

        // InPlace false → 回落 Copy（FASTER mutable→immutable 派生序）
        var f2 = new CounterFunctions { InPlaceResult = false };
        await kv.RmwAsync(1, input: 1L, f2, context: "ctx");
        f2.Log.Should().Equal("InPlace", "Copy", "RmwCb:Ok:102");
    }

    [Fact]
    public async Task Rmw_InitialFalse_ErrorAndZeroWrite()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        var f = new CounterFunctions { InitialResult = false };

        var status = await kv.RmwAsync(1, input: 42L, f, context: "ctx");

        status.Should().Be(KvStatus.Error, "Initial 折叠失败——Error 收口");
        f.Log.Should().Contain("RmwCb:Error:0");
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("零写入");
    }

    [Fact]
    public async Task Rmw_InPlaceAndCopyBothFalse_ExistingValueUnchanged()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        await kv.PutFormattedAsync(1, 100L);

        var f = new CounterFunctions { InPlaceResult = false, CopyResult = false };
        var status = await kv.RmwAsync(1, input: 5L, f, context: "ctx");

        status.Should().Be(KvStatus.Error);
        f.Log.Should().Contain("RmwCb:Error:0");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(100L, "折叠失败——原值不动");
    }

    // ═══ 读钩子（档位派发）═══

    [Fact]
    public async Task Read_ConcurrentReader_OnKvDirect_NotFoundSkipsReader()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        await kv.PutFormattedAsync(1, 7L);
        var f = new CounterFunctions();

        var (hit, output) = await kv.ReadAsync(1, input: 0L, f, context: "ctx");
        hit.Should().Be(KvStatus.Ok);
        output.Should().Be(7L, "ConcurrentReader 翻译 value→output");
        f.Log.Should().Equal("ConcurrentReader", "ReadCb:Ok:7");

        // 未命中：读钩子不征询，直接 NotFound 收口
        var f2 = new CounterFunctions();
        var (miss, output2) = await kv.ReadAsync(99, input: 0L, f2, context: "ctx");
        miss.Should().Be(KvStatus.NotFound);
        f2.Log.Should().NotContain("ConcurrentReader");
        f2.Log.Should().Contain("ReadCb:NotFound:0");
        output2.Should().Be(0L);
    }

    [Fact]
    public async Task Read_SessionDispatch_SingleReaderForSerializable()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        await kv.PutFormattedAsync(1, 7L);

        using var serial = kv.CreateSession(KvSessionConditions.Serializable);
        var fSerial = new CounterFunctions();
        (await serial.ReadAsync(1, input: 0L, fSerial, context: "ctx")).Output.Should().Be(7L);
        fSerial.Log.Should().Contain("SingleReader");
        fSerial.Log.Should().NotContain("ConcurrentReader", "Serializable 档派发单读者钩子");

        using var plain = kv.CreateSession(KvSessionConditions.None);
        var fPlain = new CounterFunctions();
        (await plain.ReadAsync(1, input: 0L, fPlain, context: "ctx")).Output.Should().Be(7L);
        fPlain.Log.Should().Contain("ConcurrentReader");
        fPlain.Log.Should().NotContain("SingleReader");
    }

    [Fact]
    public async Task Read_SessionWriteSetHit_TranslatesLocalValue()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        using var s1 = kv.CreateSession();   // RMW
        await s1.PutFormattedAsync(1, 100L);
        using var s2 = kv.CreateSession();
        await s2.PutFormattedAsync(1, 200L);   // 他人覆盖

        var f = new CounterFunctions();
        var (status, output) = await s1.ReadAsync(1, input: 0L, f, context: "ctx");
        status.Should().Be(KvStatus.Ok);
        output.Should().Be(100L, "写集命中——读己之写（翻译走本地值）");
    }

    // ═══ 写钩子（校验面）═══

    [Fact]
    public async Task Upsert_SingleWriterRejects_ErrorAndZeroWrite()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        var f = new CounterFunctions { SingleWriterResult = false };

        var status = await kv.UpsertAsync(1, value: 42L, input: 0L, f, context: "ctx");
        status.Should().Be(KvStatus.Error, "SingleWriter 拒绝——Error 收口");
        f.Log.Should().Equal("SingleWriter", "UpsertCb:Error");
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("零写入");

        var f2 = new CounterFunctions();
        (await kv.UpsertAsync(1, value: 42L, input: 0L, f2, context: "ctx")).Should().Be(KvStatus.Ok);
        f2.Log.Should().Equal("SingleWriter", "UpsertCb:Ok");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(42L);
    }

    [Fact]
    public async Task SessionUpsert_ConcurrentWriterRejects_ErrorAndZeroWrite()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        using var s = kv.CreateSession();
        var f = new CounterFunctions { ConcurrentWriterResult = false };

        (await s.UpsertAsync(1, value: 42L, input: 0L, f, context: "ctx")).Should().Be(KvStatus.Error);
        (await s.TryGetFormattedAsync(1)).Found.Should().BeFalse("零写入（写集零污染）");

        var f2 = new CounterFunctions();
        (await s.UpsertAsync(1, value: 42L, input: 0L, f2, context: "ctx")).Should().Be(KvStatus.Ok);
        (await s.TryGetFormattedAsync(1)).Value.Should().Be(42L);
    }

    // ═══ 会话 RMW（写集集成）═══

    [Fact]
    public async Task SessionRmw_WriteSetRefreshed_SelfSeesFoldedValue()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        using var s1 = kv.CreateSession();
        using var s2 = kv.CreateSession();

        await s1.PutFormattedAsync(1, 100L);
        (await s1.RmwAsync(1, input: 5L, new CounterFunctions(), context: "ctx")).Should().Be(KvStatus.Ok);

        // 他人并发覆盖后，s1 仍自见折叠结果（写集）
        await s2.PutFormattedAsync(1, 999L);
        (await s1.TryGetFormattedAsync(1)).Value.Should().Be(105L, "RMW 写集刷新——本会话自见折叠值");
        (await s2.TryGetFormattedAsync(1)).Value.Should().Be(999L);

        // 会话 RMW 走三流：初始未命中建值
        var f = new CounterFunctions();
        (await s1.RmwAsync(77, input: 7L, f, context: "ctx")).Should().Be(KvStatus.Ok);
        f.Log.Should().Equal("Initial", "RmwCb:Ok:7");
    }

    // ═══ Functions 驱动 Delete ═══

    [Fact]
    public async Task FunctionsDelete_StatusCallback_NotFoundAndOk()
    {
        using var vol = new TestVolume();
        await using var kv = await NewKvAsync(vol);
        await kv.PutFormattedAsync(1, 1L);
        var f = new CounterFunctions();

        (await kv.DeleteAsync(1, input: 0L, f, context: "ctx")).Should().Be(KvStatus.Ok);
        (await kv.DeleteAsync(1, input: 0L, f, context: "ctx")).Should().Be(KvStatus.NotFound, "二次删除未命中");
        f.Log.Should().Equal("DeleteCb:Ok", "DeleteCb:NotFound");
    }
}
