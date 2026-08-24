namespace TC.Tier.Runtime.Storage;

/// <summary>
/// <see cref="StorageEngine"/> epoch 保护 partial——LightEpoch mutator 协作 drain 接入。
/// <para>★ LightEpoch 是 FASTER 风格的纳秒级 epoch 保护（spec 15 §0.2 确认接近理论极限，保留）。</para>
/// <para>★ 核心作用：读路径（SequentialReader.DirtyRead）持 epoch 期间，PunchHole / compact promotion 等物理销毁
///   操作延迟到 reader 退出 epoch 后执行（mutator 线程持 epoch 协作 drain）。</para>
/// <para>★ 性能：ProtectAndDrain = 1 次 ThreadStatic 读 + 2 次裸指针写（cache-line 对齐，纳秒级），不降热路径吞吐。</para>
/// <para>★ 底层并发架构：三种锁（SpinRWLock 段锁/ExtentLock 区间锁/ReadyLock）+ 区间所有权（ExtentTable lease）+
///   ABA（_tail version+1）+ LightEpoch（内存安全回收）。区间所有权是核心性能关键，
///   epoch 补全"物理销毁延迟"这一层，保证全线并发下的数据一致性。</para>
/// <para>★ ★ 本类已拆除专用 epoch worker（<c>new Thread</c> + <c>drained.Wait</c>）——改 FASTER 原生「mutator 线程
///   持 epoch 协作 drain」模型（<see cref="LightEpoch.DrainThen"/>）：无 worker、无 sync Wait、无死锁。
///   对齐 COORDINATION.md §5/§7 反模式 1（禁 new Thread）/2（禁阻塞 IO 进 drain）/4（禁 drain 排序销毁）。
///   （：IEngineEpoch 包装接口消灭——drain 协议下沉 Core 原语，子系统直接注入 LightEpoch。）</para>
/// </summary>
internal sealed partial class StorageEngine
{


    /// <summary>epoch 保护是否已启动（恢复完成后置 true；mutator 协作模型下仅诊断标志，无线程需启停）。</summary>
    private bool _epochStarted;

    /// <summary>
    /// 启动 epoch 保护——设备 Initialize（恢复）后调用。
    /// <para>★ LightEpoch 表在构造时即就绪；本方法仅置标志（mutator 协作 drain 无需专用线程）。</para>
    /// </summary>
    private void StartEpochProtection()
    {
        _epochStarted = true;
    }

    /// <summary>
    /// 停止 epoch 保护——设备 Dispose 前调用（保留为 no-op 占位，对齐 Dispose 编排顺序）。
    /// <para>★ 原 dedicated worker 已拆除（<see cref="LightEpoch.DrainThen"/> 协作模型）——无线程需 Join、无 CTS 需 Cancel；
    ///   epoch 资源由 <see cref="LifecycleBase{THints}.Resources"/> 统一释放。</para>
    /// </summary>
    private void StopEpochProtection()
    {
    }
}
