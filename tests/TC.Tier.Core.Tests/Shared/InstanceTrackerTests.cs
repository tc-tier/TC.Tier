using TC.Tier.Core.Shared;
using Xunit;

namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// InstanceTracker 契约测试——实例跟踪/泄漏检测设施（它自己是"检测器"，更必须有测试：检测器坏了泄漏就隐形了）。
/// 契约：Register 分配 Id + 按名可查；Unregister 移除（未注册返回 false）；GetAlive 按类型子串过滤；
/// 弱跟踪（实例死后条目自动消失，ConditionalWeakTable 语义）。
/// </summary>
public class InstanceTrackerTests
{
    [Fact]
    public void Register_GetAlive_Contains_WithTypeAndId()
    {
        var obj = new object();
        var id = InstanceTracker.Register(obj, "TestType-A");

        id.Should().NotBe(Guid.Empty, "注册应分配非空 Id");
        var alive = InstanceTracker.GetAlive("TestType-A");
        alive.Any(i => i.Id == id && i.TypeName == "TestType-A").Should().BeTrue("按类型名可查到刚注册实例");
    }

    [Fact]
    public void Unregister_RemovesFromAlive()
    {
        var obj = new object();
        var id = InstanceTracker.Register(obj, "TestType-Unreg");
        InstanceTracker.GetAlive("TestType-Unreg").Any(i => i.Id == id).Should().BeTrue();

        InstanceTracker.Unregister(obj).Should().BeTrue("注销已注册实例返回 true");
        InstanceTracker.GetAlive("TestType-Unreg").Any(i => i.Id == id).Should().BeFalse();
        InstanceTracker.Unregister(obj).Should().BeFalse("重复注销返回 false");
        InstanceTracker.Unregister(new object()).Should().BeFalse("注销未注册实例返回 false");
    }

    [Fact]
    public void GetAlive_TypeFilter_IsSubstringMatch()
    {
        var a = new object();
        var b = new object();
        InstanceTracker.Register(a, "Leak.Probe.Alpha");
        InstanceTracker.Register(b, "Leak.Probe.Beta");

        InstanceTracker.GetAlive("Leak.Probe").Should().HaveCountGreaterThanOrEqualTo(2, "子串过滤应命中 Alpha 和 Beta");
        InstanceTracker.GetAlive("Leak.Probe.Alpha").Should().ContainSingle("精确子串只命中 Alpha");
    }

    [Fact]
    public void WeakTracking_InstanceCollected_EntryDisappears()
    {
        var id = RegisterDeadInstance("TestType-Weak");   // 实例死于辅助方法返回——调用方零强引用
        InstanceTracker.GetAlive("TestType-Weak").Any(i => i.Id == id).Should().BeTrue("条目应随实例存活（GetOrCreateValue 副作用下先验证在册）");

        // 强制 GC——ConditionalWeakTable 条目应随之消失（轮询，GC 异步）
        Assert.True(SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !InstanceTracker.GetAlive("TestType-Weak").Any(i => i.Id == id);
        }, 5000), "实例被 GC 后跟踪条目应消失（弱跟踪语义）");
    }

    /// <summary>弱测试标准模式：NoInlining 辅助方法内创建+注册，返回 Id 后实例在调用方零强引用——
    /// 防 Debug 构建 JIT 把匿名临时对象活性延长到测试方法尾（隐形扎根导致弱语义测试假红）。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Guid RegisterDeadInstance(string typeName)
    {
        var obj = new object();
        return InstanceTracker.Register(obj, typeName);   // obj 死于方法返回
    }
}
