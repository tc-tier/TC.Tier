using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

public sealed partial class TierVolumeFs
{
    // ═══════════════ 释放（clean 关闭协议）═══════════════

    /// <summary>
    /// 关闭：提交 + 置 clean + 双侧轮写（§4.1 clean 关闭协议）→ 释放跨进程锁与登记。
    /// 未调 Dispose 的进程退出 = 崩溃语义（dirty 残留 → 下次打开走恢复）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            CleanShutdown();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "clean 关闭协议失败（残留 dirty——下次打开走恢复路径）");
        }
        finally
        {
            ReleaseResources();
        }
    }

    /// <summary>测试后门：模拟崩溃——跳过 clean 关闭协议，仅释放资源与登记（dirty 残留 → 下次打开走恢复）。</summary>
    internal void CrashSimulate()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        StopFlusher();   // flusher 先于载体句柄停止（RM-02——后台线程不碰已释放句柄）
        StopPrefetcher();   // 预读线程同序（性能债 6）
        // ★ 退登记（比较移除——快照挂载与活卷同载体并存，不得误退他实例的键；RM-04 修复同族）
        var pairs = (System.Collections.ICollection)SInstances;
        foreach (var m in _members)
            _ = ((ICollection<KeyValuePair<string, TierVolumeFs>>)pairs).Remove(
                new KeyValuePair<string, TierVolumeFs>(m.Carrier.IdentityKey, this));
        _ = ((ICollection<KeyValuePair<string, TierVolumeFs>>)pairs).Remove(
            new KeyValuePair<string, TierVolumeFs>(_carrier.IdentityKey, this));
        if (_sb is not null)
            _ = ((ICollection<KeyValuePair<string, TierVolumeFs>>)pairs).Remove(
                new KeyValuePair<string, TierVolumeFs>($"uuid:{_sb.Uuid}", this));
        if (_snapshotMount)
            _ = ((ICollection<KeyValuePair<string, TierVolumeFs>>)pairs).Remove(
                new KeyValuePair<string, TierVolumeFs>(SnapshotMountKey(_carrier.IdentityKey, _snapshotName!), this));
        foreach (var m in _members)
        {
            try { m.Handle.Dispose(); } catch { /* 尽力 */ }
            try { m.DioReadHandle?.Dispose(); } catch { /* 尽力 */ }   // RM-28：直达读专用句柄同批释放
            try { m.CrossProcLock?.Dispose(); } catch { /* 尽力 */ }
        }
    }
}
