# 虚拟文件系统使用指南（RawFileSystem · virtual://——TC.Tier.Core.IO.Raw）

> **状态**：正式文档（自包含——不依赖设计/开发过程文档）
> **读者**：消费 `TC.Tier.Core` IO 层的应用/引擎开发者
> **同级**：[io.md](io.md)（IO 总指南）· [perf/io-performance.md](perf/io-performance.md)（性能）

---

## 0. 一句话

**虚拟文件系统（第四介质 · Raw 格式实现）：一个自描述的单工件（`.raw` 文件或 Linux 块设备），既是活卷又是存档。** 你得到一个 `RawFileSystem` 对象，它实现完整的 `IFileSystem`——命名空间、数据面、FileExtra、崩溃一致性全部内建，跨平台（Linux/macOS/Windows 的文件载体 + Linux 设备载体），零 BCL `File`/`Directory` 依赖。

```csharp
using var fs = RawFileSystem.New(RawCarrier.File("/data/vol.raw"),
    new RawFormatOptions { QuotaBytes = 10L << 30 });           // New 即格式化；QuotaBytes 缺省 -1 = 按需自动扩容

using var h = fs.Open("docs/hello.txt", new FileOpenOptions
{
    Access = FileOpenAccess.ReadWrite,
    Mode = FileOpenMode.OpenOrCreate,
    Sharing = FileSharing.ReadWrite,
});
h.Write(0, "hello, raw medium"u8);
h.Flush();
```

---

## 1. 核心模型（30 秒版）

| 概念 | 事实 |
|---|---|
| **载体** | `.raw` 文件 或 Linux 块设备/裸分区——一份逻辑布局，两种物理形态 |
| **自持一致性** | 崩溃一致性不委托 OS——superblock 双份轮写 + 循环日志（WAL）+ 可达性对账，断电后 `Open` 即恢复 |
| **一卷一实例** | 进程内登记（卷 UUID）+ 跨进程锁（文件载体伴生 `.lock` / 设备 `flock`）——第二个实例打开同卷立即 `SharingViolation` |
| **统一块空间** | 条目/区间/FileExtra/数据同源分配——没有"文件数上限"这种台阶，满了只因空间 |
| **区间三态** | 洞（读零）/ unwritten（预分配，读零）/ written——`Length` 与 `AllocatedSize` 是两个事实 |
| **自管页缓存** | 顶替 OS page cache 的位置：命中 = memcpy；另有直达档（`NoBuffering`）绕过 |
| **实例即介质** | 导出/导入/迁移全部经 `IFileSystem` 接口平面（管线 + dd 快道），无侧门 |

---

## 2. 载体与格式化

### 2.1 两种载体

```csharp
RawCarrier.File("/data/vol.raw")        // 文件载体：容量声明内按需增长，稀疏友好
RawCarrier.Device("/dev/nvme0n1p3")     // 设备载体：固定几何（Linux；O_DIRECT 强制）
```

- 文件载体创建伴生锁文件 `<path>.lock`（跨进程互斥）；
- 设备载体用 `flock` 互斥；容量/扇区经 `BLKGETSIZE64`/`BLKSSZGET` ioctl 探测；
- Windows 物理盘不在支持范围（权限/锁卷三重障碍）——Windows 用文件载体。

### 2.2 格式化

```csharp
public static RawFileSystem New(RawCarrier carrier, RawFormatOptions? options = null, ILogger? logger = null)   // Format 已终态改名 New（零过渡）
```

| 选项 | 默认 | 说明 |
|---|---|---|
| `QuotaBytes` | -1 | 空间根硬上限（一词制）：正数 = 供给（位图按此预留——物理级执法）；**-1 = 按需自动扩容**（文件载体：初始 64MiB、分配接近界即倍增位图重定位，直到磁盘物理满；设备载体 = 设备大小） |
| `Label` | null | 卷标签（≤32B UTF-8——New 写入 superblock；Open 断言不符即抛） |
| `BlockSize` | 4096 | 内部块大小；2 的幂，[512, 1MiB] |
| `JournalReserveBytes` | 8 MiB | 日志物理预留（0 = 不预留——此后该卷永不开日志） |
| `Label` | "" | 卷标签（≤32B UTF-8） |

- **显式语义**：对已格式化载体 `New` 抛 `IOError.AlreadyExists`（防毁卷脚枪；格式化前还会探测别卷成员头）；

**自动扩容（QuotaBytes = -1 的文件载体）**：初始 64MiB 小界；分配触及界（空间耗尽/碎片化两触发点）时
位图重定位 + superblock 原子翻转倍增（64M→128M→…，Open 配额收紧则目标 = min(倍增, 配额界)），直到磁盘
物理满（ENOSPC → DiskFull 语义）。崩溃两侧自洽：翻转前崩溃 = 旧界旧位图完好；翻转后 = 新位图已先落盘。
日志语义不切割（翻转保留日志字段原值；追加式扩容下既有记录块号稳定）。多载体卷（AddCarrier 后）退出自动
扩容——容量管理转显式。
- 格式化完成即 clean、日志就绪（`JournalReserveBytes > 0` 时）。

### 2.3 打开

```csharp
public static RawFileSystem Open(RawCarrier carrier, RawOpenOptions? options = null, ILogger? logger = null)
public static RawFileSystem Open(RawCarrier?[] carriers, RawOpenOptions? options = null, ILogger? logger = null)
```

| 选项 | 默认 | 说明 |
|---|---|---|
| `ReadOnly` | false | 只读打开；写意图操作抛 `IOError.ReadOnlyVolume` |
| `PageCacheBytes` | 64 MiB | 自管页缓存预算；**0 = 禁用读缓存**（纯直达形态——大扫描不占内存） |
| `AllowDegraded` | false | 多载体卷允许成员缺失（null 占位）——只读降级形态 |

打开路径自动完成：唯一性检查 → superblock 采纳（CRC/版本）→ 日志重放（dirty 卷）→ 可达性对账 → 恢复可写。**断电后直接 `Open` 即可，无需修复工具。**

- 版本高于支持上限 → `IOError.Unsupported`（绝不静默猜测）；
- 多载体：成员清单须全量按序（成员 0 = 主载体）；UUID/索引不匹配拒开；缺失成员未 `AllowDegraded` 拒开。

### 2.4 关闭（重要）

```csharp
fs.Dispose();   // clean 关闭：提交 + 置 clean 位 + 轮写 + 释放锁
```

- **正常关闭 = 走 clean 协议**（下次打开跳过恢复遍历，快路径）；
- 进程直接退出/崩溃 = dirty 残留——不丢已提交数据，下次打开走日志重放 + 孤儿回收后**恢复可写**；
- 崩溃语义（fsync 语义窗口）：`Flush()` 前的写允许丢失，`Flush()` 后的完整存活。

---

## 3. 日常使用（IFileSystem 平面）

Raw 实现完整 `IFileSystem`——与 Mem/Disk/Remote 三介质同构，全部 API 见 `src/TC.Tier.Core/IO/IFileSystem.cs` 与 `IFileHandle.cs`。要点：

### 3.1 命名空间

```csharp
fs.CreateDirectory("docs");                     // mkdir -p（幂等 + 耐久）
fs.DeleteDirectory("docs");                     // 仅限空（非空抛 DirectoryNotEmpty）
fs.MoveDirectory("a", "b");                     // 实例内元数据事务原子（不依赖 OS rename）
fs.CreateFile("docs/f.bin", preallocateSize: 1 << 30, extra: someExtra);  // 显式创建 + 预分配 + FileExtra
fs.Delete("docs/f.bin");                        // 耐久删除（打开句柄在档拒删——SharingViolation）
fs.Move("a/x", "b/x", overwrite: false);        // 内建父目录刷盘（DurableRename）
var info = fs.Stat("docs/f.bin");               // 单条目完整信息
foreach (var e in fs.EnumerateEntries("docs", "*", recursive: true)) { }
```

- **FileExtra**：≤1536B 侧车数据（xattr 同构）——`CreateFile(extra:)` 建、`Stat` 读、`IFileHandle.SetFileExtra/ReadFileExtra/WriteFileExtra` 增改。

### 3.2 数据面（IFileHandle）

```csharp
using var h = fs.Open("docs/f.bin", new FileOpenOptions
{
    Access = FileOpenAccess.ReadWrite,
    Mode = FileOpenMode.OpenOrCreate,       // OpenExisting / CreateNew / Truncate / Append
    Sharing = FileSharing.ReadWrite,
    PreallocateSize = 1 << 30,              // open 即幂等预分配（unwritten 区间——读零）
    Hints = FileOpenHints.None,             // 两档模型见 §4
});

h.Write(offset, data);                      // pwrite 语义（越 EOF 零洞扩展）
var got = h.Read(offset, buf);              // pread 语义（EOF 处短读）
var pos = h.Append(chunk);                  // 原子预留追加——多线程同句柄无覆写，返回写入偏移
h.WriteVector(offset, sources);             // writev
h.Flush();                                  // fsync 语义（排干 + 日志提交——持久化屏障）
```

### 3.3 空间管理（稀疏与预分配——区间三态）

```csharp
h.SetLength(100L << 30);                    // 纯逻辑扩展——零物理分配（稀疏）
h.Preallocate();                            // 幂等预分配（重放 open 语义）
h.PunchHole(offset, length);                // 物理回收区间（文件大小不变、区间归零）——按 AllocationUnit 对齐
h.CollapseRange(offset, length);            // 塌缩（后续数据前移，文件缩短）——全平台
h.InsertRange(offset, length);              // 插入零洞（后续数据后移）——全平台

h.Length;            // 逻辑长度（含洞）
h.AllocatedSize;     // 物理占用（PunchHole 后 < Length）
h.EnumerateAllocatedRangesDetailed();       // 区间 + unwritten 状态（物理占用真相）
```

容量判据 = **物理块数**：100G 逻辑文件在 10G 卷上成立，只要 written 数据装得下。

### 3.4 其他能力

```csharp
h.CopyRange(dest, srcOff, dstOff, len);     // 介质内高效拷贝
h.CloneRange(dest);                         // 整文件克隆（回退 CopyRange）
h.Lock(off, len, FileLockMode.Exclusive);   // 进程内逻辑范围锁（单实例下即完备）
h.TryLock(off, len, mode);
h.Unlock(off, len);

using var view = h.Map(offset, length, MapAccess.ReadWrite);  // MMF 直映射（文件载体；
                                                             // 单连续区间文件零拷贝；碎片文件自动物化）
h.Advise(FileAdvise.Sequential);            // 纯流式读档（见 §4.3）
```

### 3.5 卷几何（精确）

```csharp
var v = fs.Volume;    // FreeSpace / TotalSpace 精确（superblock + 位图推导，非估算）
fs.VolumeUuid;        // 卷 UUID（诊断）
```

---

## 4. 两档 IO 模型与调优

### 4.1 缓冲档（默认）与直达档

| 档 | 打开方式 | 路径 | 适用 |
|---|---|---|---|
| **缓冲** | `Hints = None` | 自管页缓存：命中 ~100ns memcpy / miss 载体读 + 自动预读 | 随机读、重读、小写（写回 + 后台 flusher） |
| **直达** | `Hints = NoBuffering` | 绕过自管缓存：载体直读/直写（文件载体走 O_DIRECT 读通道；对齐纪律内建） | 大顺序扫描、备份、透写 |

两档**语义同构**（与 Disk 介质的两档对称）——直达档对齐由实现吸收（弹跳窗/合并读），未对齐缓冲也可用；一致性纪律内建（直达写失效对应缓存页 / 直达读前排干重叠脏页）。

### 4.2 自管页缓存参数

- 预算：`RawOpenOptions.PageCacheBytes`（默认 64 MiB）；
- 预算内行为：LRU 逐出、写回脏页（flusher 阈值 `max(1MB, 预算/8)` 唤醒）、整块 run 写绕（write-around，不污染缓存）；
- **0 预算 = 纯直达形态**：扫描型负载不想吃内存时用。

### 4.3 Advise 与预读

- `Advise(FileAdvise.Sequential)`：**纯流式读档**——一次排干该文件自管脏页后全程载体直读（无页机制开销、无预取交互；OS 缓存自然服务重复扫描）；
- 不带 Advise 的顺序读：自动预读器（连续读启发式，内核 readahead 同构——专用线程 + 有界队列背压）；
- `Advise(WillNeed/DontNeed/Random)`：语义提示（no-op 安全）。

### 4.4 WriteThrough / FlushData

```csharp
// 打开时 Hints = FileOpenHints.WriteThrough：逐写日志提交——O_SYNC 语义，崩溃窗口归零（最重档）
h.Flush();       // 全量：排干 + 日志提交（fsync 形态）
h.FlushData();   // 数据面：排干 + 载体屏障（fdatasync 形态——不含日志记录；FlushDataOnly 能力位）
fs.FlushRoot();  // 目录级收口（净卷 = 纯屏障，纳秒级）
```

崩溃窗口谱系（默认档）：后台 flusher 50ms 轮询 / 75% 日志占用触发检查点 → `FlushData` → `Flush`（单屏障日志提交）→ `WriteThrough`（零窗口）。

---

## 5. 多载体卷（扩容/缩容/降级）

```csharp
// 在线扩容（加载体——纯加法，不改格式）
fs.AddCarrier(RawCarrier.File("/data/vol2.raw"), capacityBytes: 20L << 30);
// 设备成员容量自几何，省略 capacityBytes

// 缩容（减载体）——非空成员自动迁移（btrfs device remove 同构）后摘除
fs.RemoveCarrier(1);    // 主载体（成员 0）不可移除

// 多载体打开（全量清单——路径不入盘上格式，LVM 同构）
using var fs2 = RawFileSystem.Open(new RawCarrier?[]
{
    RawCarrier.File("/data/vol.raw"),      // 成员 0 = 主载体
    RawCarrier.File("/data/vol2.raw"),
});

// 降级打开（成员缺失——只读形态；缺失成员上的数据读诚实拒绝）
using var fs3 = RawFileSystem.Open(new RawCarrier?[]
{
    RawCarrier.File("/data/vol.raw"),
    null,                                   // 占位缺失成员
}, new RawOpenOptions { AllowDegraded = true });
```

- 成员容量 64 块对齐；误并入已格式化载体显式拒绝；
- 缩容含数据迁移时为逐文件逐 extent 块搬迁（在线/崩溃重放同构）；
- `FreeSpace`/`TotalSpace` 全成员求和；
- v2 遗留：>8 成员、条带布局。

---

## 6. 采集/还原/迁移（管线 + dd 快道）

全部经 `RootSpaceImage`（`TC.Tier.Core.IO.Image`）——只认 `IFileSystem` 接口平面，四态（Mem/Disk/Remote/Raw）互转一套代码：

```csharp
// 采集 → TCA1 流（结构化存档：清单 + 数据帧 + CRC）
using (var out_ = File.Create("backup.tca"))
    RootSpaceImage.Capture(fs, out_, new ImageOptions
    {
        Compression = ImageCompression.Zstd,   // None / ZLib / Zstd
        FrameBytes = 1 << 20,
        QuietSource = true,                    // 自动进维护门闩（静默快照）
    });

// 还原（目标必须为空）
using (var in_ = File.OpenRead("backup.tca"))
    RootSpaceImage.Restore(in_, targetFs, new ImageOptions { VerifyChecksums = true });

// 介质间转移——能力位自动路由：
// 源与目标都置位 ContiguousCapture（即 Raw↔Raw）→ dd 字节直拷快道；
// 其余组合 → 结构化管线（TCA1 流中转）
RootSpaceImage.Transfer(sourceFs, targetFs);
```

- TCA1 保真：稀疏（洞不搬零）、unwritten 区间（预分配语义重建）、FileExtra；时间戳仅审计记录不承诺还原；
- 快道前置：容量预检（目标不足抛 `DiskFull` 且零字节受损）、双侧维护租约、镜像后目标重载内存态；
- **`.raw` 文件本身即存档**——`cp`/`dd`/网络推流都是合法迁移（单工件自描述）。

---

## 7. 维护门闩（静默快照前置）

```csharp
using (fs.EnterMaintenance("backup", MaintenanceScope.WriteOperations, ct))
{
    // 此期间：写操作抛 IOError.UnderMaintenance，读放行
    // 在途写已收敛（进入时阻塞等待归零）
}
```

- 责任划分：fs 层门闩挡得住句柄操作，**挡不住你自己的后台任务**——调用方先自行收敛业务侧写入，再 Enter；
- `AllOperations` 档读写全拒（还原目标卷用）；
- 管线 `QuietSource = true` 时自动包夹（见 §6）。

---

## 8. 能力位（Capabilities）

Raw 置位（对照 Disk 全对齐 + 6 增强）：

`Sparse`（内部恒真）· `RandomWrite` · `EmptyDirectories` · `DurableRename` · `AtomicDirectoryMove`（**增强**：实例内元数据事务，不依赖 OS）· `ExclusiveLock`（**增强**：内建）· `RangeLock`（**增强**：进程内逻辑锁）· `RangeShift`（**增强**：全平台）· `ContiguousCapture` · `MaintenanceGate` · `CopyRange` · `VectorIO` · `DirectIO` · `Advise` · `WriteThrough` · `FlushDataOnly` · `Mmap`（文件载体；设备诚实不置位）· `Volume` 几何精确（**增强**）。

消费方**按能力位决策**，不要按介质硬编码——这是四介质平权的机械保证。

---

## 9. 错误码速查

| IOError | 场景 | 处置 |
|---|---|---|
| `SharingViolation` | 第二实例开同卷 / 删除打开中的文件 / 载体被外部持有 | 检查另一实例；先关句柄 |
| `AlreadyExists` | New 到已格式化载体 / CreateFile 已存在 | 显式语义，幂等由调用方组合 |
| `ReadOnlyVolume` | 只读/降级卷上的写意图 | 与权限问题分离——修复 = 全量成员重开 |
| `UnderMaintenance` | 维护租约生效中被 scope 拒的操作 | 等租约解除 |
| `DiskFull` | 物理块耗尽（含 Transfer 预检） | 因空间不因条目——扩容（加载体）或删除 |
| `AlignmentError` | PunchHole/Collapse/Insert 未按 AllocationUnit 对齐 | 对齐到 `Volume.AllocationUnit` |
| `Unsupported` | 版本高于支持上限 / 未置位能力位 | 前向双门——绝不静默 |
| `NotFound` | 多载体成员缺失且未 AllowDegraded | 全量清单或降级打开 |

---

## 10. 纪律与禁区

1. **一卷一实例，无侧门**：一切载体字节访问（含备份导出）必须经实例——直接 `dd` 一个**活卷**属于违规（锁是 advisory 拦不住外部写者；设备载体 O_DIRECT 下有一致性风险）；
2. **句柄必须 Dispose**：`IFileHandle` 持有共享登记；泄漏句柄会挡住 Delete/维护收敛；
3. **不要绕过 `RawFileSystem` 类型表面**：盘上格式细节全部内嵌私有——partial 拆分是内部组织，不是扩展点；
4. **写放大自知**：碎片文件 `Map` 会物化（全文件重写）；`CollapseRange/InsertRange` 是 memmove 语义；
5. **容量声明定死**：单载体不改格式不扩容——跨上限一律 `AddCarrier`（纯加法）。

---

## 11. 常见配方

**配方 A：追加日志型工作负载（引擎段文件同构）**

```csharp
using var h = fs.Open("wal/seg-001", new FileOpenOptions
{
    Access = FileOpenAccess.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
    Sharing = FileSharing.ReadWrite, PreallocateSize = 512L << 20,   // 预分配消分配税
});
// 追加走 Append（原子预留）；落盘节奏按需：FlushData（批）/ Flush（提交点）/ WriteThrough（逐写）
```

**配方 B：大文件扫描分析（只读吞吐优先）**

```csharp
using var h = fs.Open("datasets/big", new FileOpenOptions
{
    Access = FileOpenAccess.Read, Mode = FileOpenMode.OpenExisting,
    Hints = FileOpenHints.NoBuffering,          // 直达档——O_DIRECT 读通道，扫描不冲刷内存
});
h.Advise(FileAdvise.Sequential);                 // 纯流式：无页机制、无预取交互
```

**配方 C：备份到 S3 再恢复**

```csharp
var remote = new RemoteFileSystem(/* s3 options */);
RootSpaceImage.Transfer(rawFs, remote);          // 结构化管线（TCA1 + Zstd 压缩）
// 灾备：Transfer(remote, RawFileSystem.New(RawCarrier.File("/restore.raw"), ...))
```

**配方 D：在线扩容不打断服务**

```csharp
fs.AddCarrier(RawCarrier.Device("/dev/sdb1"));   // 新块立即可用；成员表事务原子持久
```

---

## 12. 测试与验证

- 契约测试族 `tests/TC.Tier.Core.Tests/IO/Raw/`（1:1 跟源文件走）；
- 性能基准 `benchmarks/TC.Tier.Core.Benchmarks/IO/RawIoBenchmarks.cs`（`dotnet run -c Release -- --filter *RawIo*`）；
- 探针 `prototypes/DiskVsRawProbe/`（快速肉眼对照）；
- 数字与验收线见 [perf/io-performance.md](perf/io-performance.md)。
