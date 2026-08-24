# Core/IO——文件 IO 原语使用指南

> **定位**：`TC.Tier.Core.IO` 是文件 IO 的原语层（家族 A·物理层）——**两平面 × 四介质 × 一个注入点**：
>
> | 平面 | 契约 | 职责 |
> |---|---|---|
> | 命名空间平面 | `IFileSystem` | 目录族 / 文件创建 / 枚举 / FileExtra / 卷锁——一个实例 = **整个存储系统的根空间**（全相对路径层级命名空间） |
> | 数据平面 | `IFileHandle` | pread/pwrite / 追加 / 空间管理 / 映射 / 锁 / FileExtra 四成员 |
>
> - **四介质**：`DiskFileSystem`（磁盘）/ `MemoryFileSystem`（内存）/ `RemoteFileSystem`（远程对象存储）/
>   **`RawFileSystem`（虚拟文件系统——`.raw` 文件 / Linux 块设备，自持一致性 + 自管页缓存；本地持久化推荐位。
>   深指南 [`raw-medium.md`](raw-medium.md)，性能 [`perf/io-performance.md`](perf/io-performance.md) §8）**
>   ——同一契约平权实现；
> - **一个注入点**：**网络文件系统已完结**——`IObjectStore`（对象层契约，[`IO/Remote/`](../IO/Remote/)）是唯一扩展面：**外部厂商实现 `IObjectStore` 即接入完整远程栈**（staging/Flush 编排/fencing/恢复全在桥内）；仓库提供**完整 S3 参考实现**（`TC.Tier.Core.IO.S3`：SigV4 自写、零外部包，覆盖 S3/OSS/MinIO/R2/B2/COS-S3兼容端）。
>
> **外部通常经 `IStorageEngine`（Contracts）间接用**——直接消费本层需要理解本文的能力位与陷阱。
> **性能**：[perf/io-performance.md](perf/io-performance.md)（Linux 基线 + Raw 六维 + 远程实测档案）。

**目录**：§1 快速开始 → §2 介质构造 → §3 命名空间（目录/创建/枚举/FileExtra）→ §4 句柄（读写/FileExtra/映射/锁）→ §5 句柄池 → §6 网络文件系统（RemoteFileSystem 完结故事 + IObjectStore 扩展）→ §7 能力位与回退（含逐能力详解）→ §8 常见陷阱 → §9 差异声明 → §10 采集/还原/迁移管线（导出/导入/启动）。虚拟文件系统深指南独立成篇：[`raw-medium.md`](raw-medium.md)。

---

## ⚠️ 置顶一（远程）：持久化唯一入口是 `Flush`

`RemoteFileSystem` 写句柄是 **staging 写回层**：`Write/Append` 只进 staging 即返回；**任何 Dispose 都不上传**
（池内 = 归还；池外 = 关闭且未 Flush 的 staging **丢弃**——语义同构"未 fsync 即丢"）。用完即持久必须显式 `h.Flush()`。
池的三出口（`Release(close:true)`/`RemoveAll`/`pool.Dispose`）同样不 flush——**`RemoveAll` 不得 flush**（引擎删段后
flush 会复活已删对象）。

## ⚠️ 置顶二（mem/raw）：Dispose 方向差异（最反直觉）

| 介质 | fs.Dispose 语义 | 已开句柄 |
|------|----------------|---------|
| 磁盘 / 远程 | "离开目录"——仅释放 fs 自持资源 | **继续有效**（OS 句柄/staging 归消费者） |
| **内存** | **"拔盘"**——销毁卷（数据内存可能复用，必须失效） | **抛 `ObjectDisposedException`** |
| **Raw** | **"关卷"**——提交 + 置 clean + superblock 轮写（下次打开跳过恢复快路径） | **抛 `ObjectDisposedException`** |

可移植纪律：三种行为都不依赖——按"先映射、再句柄、最后 fs"顺序释放。Raw 侧进程直接退出/崩溃 =
dirty 残留（已提交数据不丢，下次打开日志重放 + 孤儿回收后恢复可写）。

---

## 1. 快速开始（30 秒上手）

```csharp
// ── 1. 构造介质（spec 一行切介质——§2 协议形态）──
using var fs     = TierFs.New("local:///" + rootDir);                // 本地文件系统（New = 建空镜像）
using var mem    = TierFs.Open("memory:");                           // 内存文件系统私有卷（测试隔离）
using var remote = TierFs.Open("network:///s3/minio:9000/tier-logs?tls=0&cred=env:MINIO_KEY");  // 网络（§6）

// ── 2. 建结构 + 提前创建（建段协议：创建成本移出运行时热路径）──
fs.EnsureRoot();                                        // 根存在保证（幂等）
fs.CreateDirectory("struct1/eng0/compact");             // mkdir -p 幂等 + 耐久
fs.CreateFile("struct1/eng0/data.0", preallocateSize: 1 << 30,
              extra: header);                           // 真预留(毫秒级) + FileExtra 创建即写；已存在抛 AlreadyExists

// ── 3. 运行时打开读写（纯打开——错误面收窄为 NotFound/SharingViolation）──
using (var h = fs.Open("struct1/eng0/data.0", new FileOpenOptions
       { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
{
    h.Write(4096, record);                              // pwrite：不读不推进游标；越过 EOF 零洞扩展
    var landing = h.Append(record);                     // 多写者原子追加（返回落点）
    h.SetFileExtra(meta);                               // FileExtra ≤1.5K（§4.5）
    h.Flush();                                          // ★ 网络文件系统 = 唯一持久化点（置顶一）
}

// ── 4. 枚举 / 恢复扫描 ──
var segs = fs.EnumerateFiles("struct1/eng0", "data.*");         // 模式匹配（引擎段扫描形态）
var all  = fs.EnumerateEntries("struct1", "*", recursive: true); // 混合（文件+目录）递归——恢复扫盘
var st   = fs.Stat("struct1/eng0/data.0");                       // 完整信息（含 FileExtra）
```

---

## 2. 介质构造（协议形态——spec / 动词 / options 三件套）

**四介质同一套语言**：spec 字符串定位身份与挂载参数（`TierFs` 工厂——可序列化、可配置驱动），
类型化动词 `New`/`Open`/`OpenOrCreate` 落位（bind-any 场景用 OpenOrCreate），options 子类只装调优旋钮
（PartSize/PageSize/MetadataMode…——字符串只带身份与部署事实，调优永不进字符串）。

```csharp
// 工厂（spec 驱动——全部挂载参数可写进字符串：label/quota/access/exclusive/spill/cred/…）
using var db  = TierFs.New ("local:///var/lib/tier?quota=100G&label=prod");
using var mem = TierFs.Open("memory:?label=test-a");
using var vol = TierFs.New ("virtual:///data/vol.raw?label=wal-a");        // 无 quota = 按需自动扩容
using var s3  = TierFs.Open("network:///s3/cos.example.com/bucket/pfx?vhost=1&cred=env:TIER_S3");

// 工厂 × options 合流（spec 定身份 + options 补调优；优先级 = spec 显式胜出 → options → 类型缺省）
using var db2 = TierFs.New("local:///var/lib/tier", new DiskFileSystemOptions { MetadataMode = DiskMetadataMode.Sidecar });

// DSL（代码侧零字符串——L2 编译期安全层；ToString = spec）
using var m2 = TierFs.Open(Specs.Memory().Label("test-a").Quota(1.Giga()).ToString());

// 类型层直构（工厂的底座——三段式签名 (位置[, options][, 日志])）
using var d3 = DiskFileSystem.New("/var/lib/tier", new DiskFileSystemOptions { QuotaBytes = 100L << 30 });
using var v3 = RawFileSystem.New(RawCarrier.File("/data/vol.raw"), new RawFormatOptions { BlockSize = 4096 });
using var r3 = RemoteFileSystem.New(objectStore, new RemoteFileSystemOptions { Spill = RemoteSpill.ToDisk("/var/tmp") });
```

**动词**：`New` = 创建空镜像（已存在且非空抛 AlreadyExists——防误覆盖）；`Open` = 打开既有（不存在抛
NotFound）；`OpenOrCreate` = 懒初始化糖（bind-any 终态——两态显式表达）。四介质全部同契约。

### 2.1 spec 语法（一分钟）

```
local:///var/...        本地文件系统（绝对）      local:data/tier   相对（构造时对 CWD 固化）
local:///C:/data        Windows 盘符             local://host/share  UNC
local | local:          快捷：CWD 为根           memory:            内存文件系统（私有卷）
virtual:///data/v.raw   虚拟文件系统·文件载体     virtual:///dev/nvme0n1  设备载体（path 首段制）
network:///s3/host[:port]/bucket/prefix           网络文件系统（协议首段必填——s3 随 IO.S3 程序集自动注册）
?label=..&quota=100G&access=ro|wo|rw&exclusive=1&member=/v2.raw&vhost=1&tls=0&cred=env:NAME&spill=local:///var/tmp
```

quota 一词制 = 空间根硬上限（-1/缺省 = 无上限；虚拟文件系统文件载体 = **按需自动扩容**——初始 64MiB、
分配接近即倍增，直到磁盘物理满）。`cred` 永远是引用（`env:NAME`）不携值。完整参数表见
[概念平权矩阵](medium-parity-matrix.md) 与 TierSpecTests。

### 2.2 介质定位（全部生产级——选介质 = 选运行形态，不是选完成度）

| 介质 | spec 头 | 生产特长 | 关键支撑 |
|------|---------|----------|----------|
| **内存文件系统** | `memory:` | **高性能运行时**——直址零拷贝、纳秒级元数据 | Reserved 直址；持久化 = 运行时导出镜像（§4.4） |
| **本地文件系统** | `local://` | **可视化目录 / 性能稳定**——宿主工具直接查看 | 目录树形态；fsync 持久化 |
| **网络文件系统** | `network://` | **本地不落地 / 容量无界 / 共享** | staging 写回层；Flush = PUT |
| **虚拟文件系统** | `virtual://` | **功能最全 / 稳定性最高 / 快速迁移** | 自持崩溃一致性；能力位覆盖最全；dd 快道 |

"易失"是内存文件系统的**机制属性不是概念缺席**——持久化经导出镜像全额成立（§4.4），单元测试/CI 无盘只是
它的场景之一。测试隔离用 `TierFs.Open("memory:")` 私有卷（全局盘 `MemoryFileSystem.Default` 路径空间
共享——§8.9）。

### 2.3 虚拟文件系统：`RawFileSystem`（`.raw` 文件 / Linux 块设备）

```csharp
// 自描述单工件：既是活卷又是存档——崩溃一致性内建（断电后 Open 即恢复，无需修复工具）
using var fs  = RawFileSystem.New(RawCarrier.File("/data/vol.raw"),
    new RawFormatOptions { QuotaBytes = 10L << 30 });             // New 即格式化（已格式化载体抛 AlreadyExists）
using var fs2 = RawFileSystem.Open(RawCarrier.File("/data/vol.raw")); // 打开（dirty 卷自动恢复可写）
```

⚠️ **虚拟文件系统差异必读**（完整清单与陷阱见 [`raw-medium.md`](raw-medium.md)）：一卷一实例（进程内 UUID 登记 +
跨进程锁——第二实例 `SharingViolation`）；fs.Dispose = 提交 + 置 clean（**mem 之外唯一"关卷有语义"的介质**）；
自管页缓存（两档模型/预算 0 = 纯直达）；多载体卷（在线扩容/迁移缩容/降级运行）；quota=-1 自动扩容
（§2.1）；采集/还原/dd 快道经 `RootSpaceImage`（TCA1 流 + ContiguousCapture 路由）。

### 2.4 生命周期

构造 → 用 → 释放；**顺序铁律：pool.Dispose（若有）→ 句柄 → 映射 → fs**。fs 可长期持有（引擎级单例）；
`Volume`/`Capabilities` 构造时一次探测缓存（生命周期内不变）。

---

## 3. 命名空间平面（IFileSystem——根空间）

### 3.1 路径契约

层级相对路径（`PathValidator.ValidateRelative`，三介质同一实现）：`'/'` 唯一合法分隔符（`\` 拒）；
空组件（`/a`、`a/`、`a//b`）/ `.` `..` 组件（任何位置越根）/ 盘符 / `\0` 与 Windows 保留集 `<>:"|?*` /
单组件 >255 / 组合超长 → `ArgumentException`；比较一律 **Ordinal**（不得依赖大小写区分路径）。

### 3.2 目录族

```csharp
fs.CreateDirectory("s1/eng0/compact");            // mkdir -p 幂等 + 耐久（新建目录与父目录 fsync）
fs.DirectoryExists("s1/eng0");                    // Remote：前缀有内容（EmptyDirectories 不置位）
fs.DeleteDirectory("s1/eng0/compact");            // POSIX rmdir：仅限空（非空抛 IOError.DirectoryNotEmpty）
                                                  //   ★ 不提供递归删——显式组合 Enumerate+Delete+DeleteDirectory
fs.MoveDirectory("s1/eng0", "s2/eng0");           // 整树移动；不提供 overwrite；原子性看 AtomicDirectoryMove 位
```

### 3.3 文件操作与创建解耦

```csharp
bool exists = fs.Exists("s1/eng0/data.0");
fs.Delete("s1/eng0/data.0");                      // 耐久删除（+ FileExtra/sidecar 绑定清除）
fs.Move("a.compact", "a.data", overwrite: true);  // ★ overwrite 必须显式；false 且目标存在 → AlreadyExists
fs.CreateFile("s1/eng0/data.1", preallocateSize: 1 << 30, extra: payload);
                                                  // 提前创建（与 Open 解耦）：真预留 + FileExtra + 目录项 fsync
                                                  // 已存在抛 AlreadyExists（幂等 = 消费者 Exists 前置组合）
fs.EnsureRoot();  fs.FlushRoot();                 // 根存在保证 / 父目录 fsync（崩溃恢复用）
```

### 3.4 枚举族（三族同形态：单参=根+pattern；双参=path+pattern；recursive 缺省 false）

```csharp
var segs  = fs.EnumerateFiles("s1/eng0", "data.*");            // 引擎段扫描形态
var dirs  = fs.EnumerateDirectories("s1");                     // 一层子目录
var whole = fs.EnumerateEntries("s1", "*", recursive: true);   // 混合递归（恢复扫盘；Remote 单次往返）
```

- **模式匹配**：`*` / `?`（BCL MatchType.Simple，Ordinal）；匹配目标 = 条目**最终组件名**；缺省 `"*"` 全匹配。
- **隐藏类**：任一组件以 `.` 开头 → 枚举不可见（`.tier-volume-lock`/sidecar `.{name}`/引擎 `.{DeviceName}`
  同类；隐藏子树整支）；**豁免 = pattern 首字符 `.`**（`".*"` 显式查看）；直接访问不受影响（可见性语义，
  非访问控制）。
- **一致性契约**（POSIX readdir）：不保证原子快照、不保证顺序；递归 Name = 相对所枚举目录的多组件路径。
- **Stat**：单条目完整信息（Type/Length/时间戳/FileExtra）——O(1)；缺条目抛 NotFound。

### 3.5 卷锁

```csharp
using (var lease = fs.AcquireExclusive(TimeSpan.FromSeconds(10))) { }   // 卷级互斥（RAII + 异常安全）
```

阻塞获取（超时 `SharingViolation`）；非重入；mem = 进程内真锁（自旋互斥——行为保真）；磁盘 lock file 崩溃自愈；
远程 = 尽力型 fencing（§6.5）。

### 3.6 故障注入（测试）

```csharp
using var fi = new FaultInjectingFileSystem(fs, seed: 42);
fi.AddRule("victim-*", "Write", IOError.DiskFull, failAtCallIndex: 3);  // 确定性 / 概率注入
fi.AddRule("*", "Flush", IOError.IOFailure, probability: 0.1);
```

---

## 4. 数据平面（IFileHandle）

### 4.1 打开语义四要素

```csharp
using var h = fs.Open("log-0001.data", new FileOpenOptions
{
    Access   = FileOpenAccess.ReadWrite,   // Read / Write / ReadWrite
    Mode     = FileOpenMode.OpenOrCreate,  // OpenExisting / OpenOrCreate / CreateNew / Truncate / Append（游标初始 EOF）
    Sharing  = FileSharing.ReadWrite,      // advisory（§8.2）：None / Read / Write / ReadWrite / Delete
    Hints    = FileOpenHints.None,         // NoBuffering(DIO) / WriteThrough / SequentialScan / RandomAccess
    PreallocateSize = 64L << 20,           // >0 → open 即幂等预分配
});
```

| 需求 | Access | Mode | Sharing | Hints |
|------|--------|------|---------|-------|
| 日志段写句柄 | ReadWrite | OpenOrCreate | ReadWrite | 按 DIO 探测加 NoBuffering |
| 跨段读（页缓存友好） | Read | OpenExisting | ReadWrite | — |
| DIO 读（扫描器） | Read | OpenExisting | ReadWrite | NoBuffering |
| 一次性临时段 | ReadWrite | CreateNew | None | — |
| 检查点槽文件 | ReadWrite | OpenOrCreate | None | WriteThrough（可选） |

- 组合合法性构造时校验（写模式配 `Access=Read` → `ArgumentException`）；`(Access,Mode,Sharing)` 与 BCL 三要素一一对应。
- **DIO 语义链**（`Hints.NoBuffering`）：句柄 `UnbufferedSupport`/`RequiredAlignment` = 三重对齐单一事实源
  （Win=max(扇区,内存页)/Linux=逻辑块/mem=1；缓冲句柄恒 1）——对齐 buffer 必须走 `AlignedMemoryManager`/`PinnedBufferPool`（§8.1）。

### 4.2 位置读写与追加

```csharp
h.Write(4096, record);                     // pwrite：不读不推进游标；越过 EOF 零洞扩展
int n = h.Read(4096, dst);                 // 返回实际读数（EOF 处可能 < dst.Length）
await h.WriteAsync(4096, mem, ct);         // 异步族语义同同步
var landing = h.Append(record);            // ★ 文件级原子预留（同 fs 任意句柄并发追加——落点两两不交、返回落点）
h.Seek(0, SeekOrigin.Begin);               // 句柄级书签（与 Append 并发 = 调用方错误）
```

三个位置概念各管各的：`Write/Read(offset)` 无状态（协调分配）/ `Position/Seek` 句柄级会话书签 /
`Append` 文件级原子预留。**`FileOpenMode.Append` 只初始化游标于 EOF——不是强制追加**（≠ BCL FileMode.Append
/ O_APPEND）；追加式文件只经 Append 增长（显式 Write 越过预留末端与 Append 混用 = 调用方纪律错误，§8.4）。
Append 失败预留不回滚（异常带 `ReservedOffset`——失败区间成读零洞，§8.5）。

### 4.3 空间管理

```csharp
h.Preallocate();                    // 幂等预分配（open 已自动执行；恢复场景显式重放）
h.SetLength(1 << 20);               // 截断 / 零填充扩展（追加预留权威复位）
h.PunchHole(0, 64 << 10);           // ★ 按 Volume.AllocationUnit 对齐（两介质同校验）；区间读零长度不变
var phys = h.AllocatedSize;         // 物理占用（Sparse 介质打洞后 < Length）
foreach (var r in h.EnumerateAllocatedRanges()) { }   // 块粒度区间
h.CollapseRange(0, 64 << 10);       // 区间移除/插入（RangeShift 位；不支持抛 Unsupported）
```

### 4.4 持久化谱系（持久化 = 把数据从"会丢的层"推进到"不会丢的层"）

```csharp
h.Flush();                          // fsync/FlushFileBuffers/F_FULLFSYNC；网络文件系统 = PUT（唯一持久化点）
h.FlushData();                      // fdatasync 语义（仅 Linux 与 Flush 可区分；否则 ≡ Flush 不抛）
h.Advise(FileAdvise.Sequential);    // 访问提示（不支持平台 no-op）
```

- **原地持久化**：本地/虚拟/网络文件系统经 `Flush`（fsync / 日志提交 / multipart PUT）。
- **内存文件系统无原地持久化层**（介质内没有比进程内存更持久的层——机制豁免），**持久化点 = 运行时导出**
  （`RootSpaceImage.Capture/Transfer`，§10——一致性时点 = 维护门闩静默快照，与 fsync 的崩溃一致点同构）。
  "易失" ≠ "非生产"：内存态运行 + 导出成档 = 与"运行 + fsync"同一概念的两个投影。

group commit 模式：写 → 攒批 → `Flush()` 一次（`FlushData` 在 Linux 省元数据刷盘）。

### 4.5 FileExtra 平面（文件附加数据——统一唯一概念）

一个文件 = 一份**不透明附加数据 ≤1536 字节**（`IFileSystem.MaxFileExtraBytes`）。无命名键、无双 API、预算
闭环（全量/偏移写同一封顶）。fs 级（`CreateFile(extra:)`/`Stat`）与句柄级同平面互见；心智模型 =
≤1.5K 小文件的 pread/pwrite 投影：

```csharp
var blob = h.FileExtra;                        // ① 全量读（空 = 无——ReadOnlyMemory 非 null）
int n = h.ReadFileExtra(offset, dst);          // ② 偏移读（pread 契约：尾段返实际；offset≥长 → 0）
h.WriteFileExtra(offset, patch);               // ③ 精准字节写（原位补丁 / 越尾零扩展 / 越限即抛）
h.SetFileExtra(meta);                          // ④ 完全覆盖（可增减长；空 = 清除；超限即抛）
```

- **介质语义**：Disk = `DiskMetadataMode` 路由（`Fallback` 缺省：xattr/ADS 优先失败回退 sidecar `.{name}`
  伴生文件；`ExtendedAttr` 仅 xattr 惰性探测 fail-fast；`Sidecar` 单通道——sidecar 强一致写
  tmp+WriteThrough+Flush(true)+MoveFileDurably 原子换名）；Mem = 槽 blob 锁内原子；Remote = 对象用户元数据
  （写句柄入 staging 随 Flush/PUT 原子提交，读 staging 优先——**fs 级可见须 Flush 后**）。
- **1536 数学**：S3 用户元数据 HTTP 头 2048 字节总预算 − 键前缀，base64 3:4 折算的可证明安全值——三介质同限。
- **预算闭环**：长度增长点仅 ③ 越尾扩展与 ④——两处单点强制，无旁路。
- **Delete 必清除 / Move 必保留**（Disk xattr 随宿主 + sidecar 绑定 + 孤儿清理；Remote 服务端 COPY 指令保留）。
- 泛型按名 xattr API（`Read/WriteExtendedAttribute`）**已删除**——结构化需求自己在 blob 内编码（自己的格式自己的事）。

### 4.6 映射 / 4.7 范围锁 / 4.8 向量与拷贝

```csharp
using var section = h.Map(0, 4096, MapAccess.ReadWrite);   // ★ 必须 Dispose（独立引用——泄漏）；View 越界=段错误
h.Lock(0, 4096, FileLockMode.Exclusive);                    // 阻塞；advisory 契约（§8.12）
if (h2.TryLock(0, 4096, FileLockMode.Shared)) { }           // 非阻塞（失败返 false）；Unlock 区间精确配对
h.WriteVector(0, new[] { head, body, tail });               // readv/writev 或逐片回退
long copied = src.CopyRange(dst, 0, 0, 1 << 20);            // 部分失败不回滚（CompletedLength 携带，§8.15）
src.CloneRange(dst);                                        // 整文件克隆（不支持回退 CopyRange 全量）
```

mem 差异：Reserved=直址实时可见；Sparse=物化副本（**Flush/Dispose 才写回**，§8.6）；ReadOnly Reserved
映射不可强制只读（已知平权偏差，§9）。磁盘映射走手工 CreateFileMappingW/mmap（非 BCL MMF，§8.17）。

### 4.9 Dispose 协议

异步必须收敛（await/取消）后再 Dispose（进程级稳定性契约，§8.16——实现侧 in-flight 计数告警兜底）；
重复 Dispose 幂等；池内句柄例外（Dispose=归还，§5）；mem 槽复用/拔盘后操作抛 `FileIOException(NotFound)`
（代际防护，§8.18）。

---

## 5. 句柄池（FileHandlePool——键控共享缓存）

| 场景 | 选择 | 理由 |
|------|------|------|
| 长生命周期反复访问同一批文件（引擎段、索引页） | **池** | 同 key 命中同实例——省 open/探测开销 |
| 一次性临时文件（Compact 临时段） | 裸 `fs.Open` + using | 用完即关 |
| 多线程共享写句柄 | **池** | Acquire/Release 使用权计数 |
| 需要"现在就关" | 裸 Open 或 `Release(h, close: true)` | 池默认归还不关 |

```csharp
using var pool = new FileHandlePool(fs);                  // 默认无界；可选 (fs, maxCapacity: 256)
using (var w = pool.Acquire("seg-0001.data", new FileOpenOptions
       { Access = FileOpenAccess.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
         Sharing = FileSharing.ReadWrite, PreallocateSize = segmentGrowthLimit }))
{                                                          // key = (Path, Access, Mode, Sharing, Hints)
    w.Append(record);                                      //   ——★ PreallocateSize 不进 key（完整意图创建时生效）
}                                                          // using 尾 = 归还使用权（★ 底层不关）
if (pool.TryAcquire("seg-0001.data", readOpts, out var h)) { }   // 命中获取（不创建）
```

- **真关闭只有三条出口**：`Release(h, close: true)` / `RemoveAll(pred)` / `pool.Dispose()`（须先于 fs.Dispose）
  ——外部任何 Dispose 都不可能关闭池内资源（僵尸句柄结构性不存在）。
- **归还后不得再触碰该引用**（使用权已交还；顾问式簿记无互斥——继续使用是协议违反）。
- **协议绊线**：使用权下溢（多还）**Debug 构建立即抛** + 借还历史示波器；强制关闭在用/忘还/LRU 跳过在用 → 告警。
- 无 maxCapacity = 纯缓存模型——长期运行消费者必须周期 `RemoveAll` 回收（禁无界增长）。
- 删段顺序：`pool.RemoveAll(...)` → `fs.Delete(...)`（Windows 句柄打开时删除被拒——硬要求；其他平台资源卫生）。
- Compact Promote：`RemoveAll(p => p.Contains(".compact"))` → `fs.Move(tmp, target, overwrite: true)`。

---

## 6. 网络文件系统（RemoteFileSystem——完结故事 + IObjectStore 扩展点）

> **状态：网络文件系统已全部完成**。消费者视角它与磁盘/mem 平权（同一 IFileSystem/IFileHandle），差异在下表
> 显式声明。**接入新的对象存储厂商 = 实现 `IObjectStore`（[`IO/Remote/`](../IO/Remote/)）一个接口**——
> staging/Flush 编排/multipart 回填/fencing/恢复协议全部在桥内，零改动。仓库提供完整 S3 参考实现
> （`TC.Tier.Core.IO.S3`：SigV4 自写零外部包）与内存替身（`MemoryObjectStore`——测试无网形态）。

### 6.1 构造与扩展模型

```csharp
// S3 兼容云（完整参考实现——换 endpoint 即达 S3/OSS/MinIO/R2/B2）
using var store = S3ObjectStore.Create(new S3ClientOptions
{
    Endpoint = "http://minio:9000", Bucket = "tier-logs",
    Credentials = new StaticCredentials("minioadmin", "minioadmin"),
});
using var fs = RemoteFileSystem.Create(store, new RemoteFileSystemOptions
{
    KeyPrefix = "engine-a/",          // 多引擎共桶隔离（对象键 = KeyPrefix + 相对路径）
    SpillDirectory = "/var/tmp",      // staging 超内存预算的落盘根（null = 纯内存超限 DiskFull）
});

// 腾讯 COS（S3 兼容端点——★ 必须 virtual-host：path-style 会把整段路径当 key，§6.6）
using var cosStore = S3ObjectStore.Create(new S3ClientOptions
{
    Endpoint = "https://cos.ap-chengdu.myqcloud.com", Bucket = "tc-1253530278",
    Credentials = new StaticCredentials("<SecretId>", "<SecretKey>"),
    UseVirtualHostAddressing = true,
});

// 自建/其他厂商：实现 IObjectStore 注入（能力位 ObjectStoreCapabilities 声明差异——桥据此降级）
// 测试无网形态：new MemoryObjectStore()
```

依赖方向：`消费者 → TC.Tier.Core.IO.S3 → Core`（生产程序集零外部包）。完整客户端指南（全参数/凭证源/
厂商矩阵/错误重试）：[TC.Tier.Core.IO.S3/docs/s3-client.md](../../TC.Tier.Core.IO.S3/docs/s3-client.md)。

### 6.2 ★ 差异表（远程 vs 磁盘/mem——违反平权契约项的显式声明）

| 场景 | 磁盘/mem | Remote | 消费者义务 |
|------|----------|--------|-----------|
| **持久化时点** | Write 即落 | **Flush = 唯一持久化点**（Dispose 全形态不上传） | 用完即持久必须显式 Flush（置顶一） |
| 已开读句柄 × 写 Flush | 立即可见 | **读旧数据**（句柄级缓存：页 + 长度双来源） | 追新 = 重新 Open |
| `Length`（读句柄） | 实时 | Open 时快照 | 同上 |
| 随机覆写已有大文件 | 微秒级 | **逐区间拉取**（GB 级 = 秒级 + 全额带宽） | `RandomWrite` 不置位——改追加式或重建 |
| `Move` × 在途写句柄 | inode 语义 | staging 仍写旧键 | Move 前 Flush/关闭 |
| `MoveDirectory` | 原子（AtomicDirectoryMove ✓） | 回退逐对象 Copy+Delete（**非原子**） | 按枚举幂等重放 |
| 空目录 | ✓ | **不存在**（目录因内容而存在——CreateDirectory no-op） | EmptyDirectories 不置位 |
| `PunchHole` 存储回收 | 真回收（Sparse） | **零成本优化不存在**（占用恒 = Length） | 打洞只为读零语义 |
| `AllocatedSize` | 物理真值 | ≡ Length（对象不透明） | 勿按 API 推断成本 |
| 卷锁 | OS 原生 | 尽力型 fencing（心跳真空窗） | 正确性由段表 lease 承担 |
| **Map（物化映射）** | 直址/MMF | **区间全量下载**（GB 级 = 秒级 + 下行流量计费；Flush/Dispose 无条件写回 staging） | 小对象/批量加工/整段扫描——大对象随机小改经 Map 是最差姿势 |
| FileExtra 可见性 | 即时 | 写句柄 staging（Flush 后 fs 级可见） | — |
| 键大小写 | Ordinal | Ordinal（NTFS 差异同 mem 陷阱） | — |

### 6.3 staging 写路径

- Open 仅 Head 记长度（**零下载**）；staging = 页式稀疏覆盖层；首次读写未物化区间按需 Range GET 补页
  （StagingPageSize 64KiB）；**纯追加句柄永不加载历史**（追加路径零网络）。
- read-your-writes 天然成立（读走 staging）；句柄级缓存互不共享（单写者协议下无消费者）。
- staging 内存预算（64MB 缺省）超限 → LRU 页 spill 到 SpillDirectory；未配置 → DiskFull（嵌入式无盘形态）。
- AppendCursor 文件级（跨句柄原子推进 + 只升不降）；`SetLength` 权威复位 / 截断后扩展读零（不复活旧数据）。

### 6.4 Flush 编排（multipart + 未触区间回填）

```
Flush:
  1. 未触区间回填（正确性命脉）：旧对象从未加载/写入的 part 优先 UploadPartCopy（服务端零出口流量）；
     边界错位页 Range GET 补集后从 staging 上传。
  2. 禁止跳 part：对象 = parts 顺序拼接的连续流——跳中段 = 对象缩短 + 偏移整体错位（全零 part 照常上传）。
  3. complete = 原子替换旧版本；崩溃在 complete 前 → 旧对象完全不受影响（"未 fsync 则丢"逐字对齐）。
```

调参（`RemoteFileSystemOptions`）：PartSize 8MB ∈[5MB,5GB] / MultipartThreshold 8MB / MaxConcurrency 4 /
StagingPageSize 64KiB / StagingMemoryLimit 64MB / ReadCacheBytes 4MB / PrefetchPages 4 / LeaseTimeout 60s。
group commit 间隔 ↑ → PUT 次数 ↓（每 PUT 百 ms 级——调参收益远大于磁盘）。

### 6.5 fencing 卷锁（AcquireExclusive）

lock 对象（`.tier-volume-lock`——点前缀属**隐藏类**：默认枚举不可见，`".*"` 豁免可见）+ 条件 PUT 抢建
（If-None-Match:*）/ 心跳超时 CAS 接管（If-Match）/ token 校验条件删除（不误删他人锁）。需对象层
`ConditionalPut` 位（S3 2023+/MinIO ✓；老端点抛 Unsupported）。**尽力型三不**：不等即死释放（崩溃后真空 =
心跳超时窗）、不抗时钟漂移、不拦绕过客户端——正确性永远由段表 lease 单写者协议承担（锁只是运营护栏）。

### 6.6 厂商矩阵（差异吸收在适配器内，不上抛消费者）

| 差异点 | 吸收策略（已实现） |
|--------|-------------------|
| XML xmlns 使用不一 | S3Xml LocalName 匹配（命名空间免疫） |
| PUT If-Match × 缺失对象（AWS 404 / MinIO 静默创建） | 客户端前置 Head 归一 NotFound |
| 条件 DELETE 强制不一 | Head 校验 + 无条件删常态化 |
| ListObjectsV2 分页 | continuation-token 循环归一 |
| ★ COS 寻址（实测） | **必须 virtual-host**——path-style 整段路径被当 key；条件 PUT 头被忽略 → 前置校验兜底 |
| 单 part ≥5MiB（非末位） | 桥编排保证 |
| COS 原生 V5 签名 | 不支持——独立实现另议 |

验证分层：黄金向量（AWS 官方）→ 假 S3 服务器（进程内真 HTTP/SigV4/XML）→ MinIO 容器终验
（`scripts/run-minio-tests.sh`）→ 真实云（**COS 28/28 全功能面绿 + 压测基线，2026-08-18**）。

### 6.7 压测基线（实测档案）

- **桥层**（MemoryObjectStore 底座，隔离网络）：Flush 吞吐 870 MB/s 级、PartSize 曲线平坦（8/16/32MB 近似）、
  覆写 2/8 段 Flush 124ms（回填主导：6 part 服务端拷贝 + 2 part 上传）、spill 无悬崖（256MiB 负载/32MiB 预算
  ≈330 MB/s 线性）。
- **真实 COS**（ap-chengdu，Windows 直连；探针 `benchmarks/TC.Tier.S3PerfProbe`）：对象层 PUT 13.2 / GET 15.0
  MB/s（本机带宽饱和）；Range GET p50=48ms；桥级（128MB+32MB）全量 Flush 12.8 MB/s、**增量 Flush 耗时比 38%**
  （未改 part 服务端自拷贝——出口流量 O(增量) 实证）；洞区间 8MB 整读 38ms 全零（tier-holes 读加速真实云生效）。
  流量口径：上行免费、下行计费。

### 6.8 恢复路径（与磁盘同构）

`EnumerateFiles/Entries` = ListObjectsV2 前缀枚举（FsEntry 融合——恢复扫描零额外 Head）；层级路径直接映射
对象键前缀；崩溃未 Flush 的 staging 丢 = 未 fsync 丢——**恢复协议零修改**。规模边界：全量加载适合段目录千级键；
十万级大桶走 `ListStreamingAsync` 流式（对象层已备）。

---

## 7. 能力位矩阵与回退表

| 能力位 | Disk(Win) | Disk(Linux) | Mem(Sparse) | Mem(Reserved) | Remote | **Raw** | 说明 |
|--------|-----------|-------------|-------------|---------------|--------|---------|------|
| Sparse | ✓ | ✓ | ✓（真释放页） | ✗（记账不还物理） | ✗（memset 模拟） | **✓（区间模型固有——恒真）** | PunchHole 物理回收 |
| EmptyDirectories | ✓ | ✓ | ✓（显式目录集合） | ✓ | **✗（目录因内容而存在）** | ✓ | 空目录真实存在 |
| AtomicDirectoryMove | ✓（同卷 rename） | ✓ | ✓（锁内批量 re-key） | ✓ | **✗（逐对象 Copy+Delete）** | **✓（实例内元数据事务）** | MoveDirectory 原子性 |
| DurableRename | ✓ | ✓ | ✓（锁内原子） | ✓ | ✓（服务端 Copy+Delete） | ✓（元数据事务 + Flush） | Move 内建目录刷盘 |
| DirectIO | NTFS/ReFS 非压缩 ✓ | ✓ | **✓（磁盘模拟）** | **✓（磁盘模拟）** | ✗ | **✓（两档模型：NoBuffering=绕自管缓存；载体 DIO 纪律内建）** | mem 模拟 = NoBuffering → Supported + 512 扇区三重对齐强制（行为保真——测试期抓对齐 bug，防切 Disk 生产爆炸） |
| WriteThrough | ✓ | ✓ | ✗ | ✗ | ✗ | **✓（逐写日志提交——崩溃窗口归零）** | |
| FlushDataOnly | ✗ | ✓（真 fdatasync） | ✗ | ✗ | ✗ | **✓（排干+载体屏 ≠ Flush 含日志提交）** | 未置位 FlushData ≡ Flush 不抛 |
| CopyRange | ✗(回退循环) | ✓ | ✗(回退) | ✗(回退) | ✓（服务端零流量） | **✓（介质内 memcpy）** | 加速位——API 永远可用 |
| VectorIO | ✗(逐段回退) | ✓ | ✗(逐段) | ✗(逐段) | ✗(逐段) | ✓ | readv/writev |
| RangeShift | ✗ | ✓ | ✓ | ✓ | ✗（抛 Unsupported） | **✓（全平台——memmove + 区间重建）** | 无回退族 |
| Advise | ✗(no-op) | ✓ | ✗ | ✗ | ✓（桥级预取模拟） | **✓（Sequential=真流式读档）** | |
| ExclusiveLock | ✓ | ✓ | **✓（进程内真锁）** | **✓（同左）** | ✓（尽力型 fencing） | **✓（内建——实例打开即排他）** | mem = 自旋互斥 + 超时（与 Disk 卷锁行为保真） |
| RangeLock | ✓(LockFileEx) | ✓(OFD) | ✓（进程内区间表） | ✓ | **✓（G8：进程内 advisory 区间表——仅约束同进程同实例）** | **✓（进程内逻辑锁——单实例下完备）** | advisory 契约 |
| Mmap | ✓(手工 MMF) | ✓(mmap) | ✓（物化+写回） | ✓（直址） | **✓（G11 物化形态：Read=Range GET 快照 / ReadWrite=staging 视图写回）** | ✓（文件载体；设备诚实不置位） | 映射无只写；Remote 物化成本悬崖见 §6.2 |
| RandomWrite | ✓ | ✓ | ✓ | ✓ | **✗（延迟加载悬崖）** | ✓ | 消费者据此决策访问模式 |
| **ContiguousCapture** | ✗ | ✗ | ✗ | ✗ | ✗ | **✓（单一连续后端——dd 快道）** | Raw 独有：整卷字节镜像 |
| **MaintenanceGate** | ✗ | ✗ | ✓ | ✓ | ✓ | **✓（维护门闩——静默快照前置）** | Mem/Remote/Raw 统一（Disk 经 AcquireExclusive 组合） |
| **Volume 几何** | 探测；FreeSpace 常不可知 | 同左 | 配额 | 配额 | 头信息 | **✓（精确——superblock+位图推导）** | |

**回退表**：`PunchHole`×Sparse=memset 清零+记账；`FlushData`×未置位=≡Flush 不抛；`CopyRange/VectorIO`×未置位
=用户态回退（结果一致）；`MoveDirectory`×Remote=逐对象 Copy+Delete（非原子，部分失败有残留——按枚举幂等重放）；
`DeleteDirectory`×Remote=空/不存在两态合一；`Advise`=no-op；`CollapseRange/InsertRange`（全介质）、
`Lock/Map`×虚拟文件系统设备载体=抛 `Unsupported`（无回退族）。**FileExtra 平面无条件可用**——无能力位、无回退族。

**契约**：能力位未置位的操作有文档化回退或抛 Unsupported；消费者用能力位**主动避免依赖回退**，不要事后猜。

### 7.1 逐能力详解（✓ 背后的真实语义——按组）

#### A. 生命周期与一致性

| 维度 | Disk | Mem | Raw | Remote |
|------|------|-----|-----|--------|
| 崩溃一致性 | OS 委托（fsync 语义） | 不适用（易失） | **自持**：superblock 双份轮写 + WAL 日志重放 + 可达性对账——断电后 `Open` 即恢复可写，无需修复工具 | 服务端委托（对象版本原子性——multipart complete 前 = 旧版完整） |
| `fs.Dispose()` 语义 | "离开目录"（句柄继续有效） | **"拔盘"**（句柄抛 ObjectDisposedException） | **"关卷"**（提交 + 置 clean + 轮写；句柄同样失效） | "离开目录"（未 Flush staging 丢弃） |
| 实例唯一性 | 无（多实例可开同根，靠应用协调） | 进程内对象 | **内建**：卷 UUID 登记 + 跨进程锁（文件锁 / 设备 flock）——第二实例 `SharingViolation` | 无（共享对象存储——fencing 尽力） |
| `Flush()` = 持久化点 | fsync（~110μs） | no-op | 排干 + **单屏障日志提交**（~564μs ≈ 硬件地板） | **唯一持久化点**（multipart PUT，组提交） |

#### B. 数据面

| 维度 | Disk | Mem | Raw | Remote |
|------|------|-----|-----|--------|
| Write 后数据在哪 | OS page cache（内核写回） | 进程内存 | Raw 载体（自管页缓存 + 后台 flusher） | staging 内存层（Flush 才上传） |
| `Length`（读句柄） | 实时 | 实时 | 实时（内存元数据） | **Open 时快照**（追新 = 重新 Open） |
| 随机覆写大文件 | ✓ 微秒级 | ✓ 直达 | ✓ | **✗** 逐区间拉取（`RandomWrite` 不置位） |
| `PunchHole` 物理 | 真回收（Sparse fs） | Sparse 真释放页 / Reserved 记账 | 区间释放 + 可选载体打洞 | 无存储意义（占用恒 = Length） |
| `CopyRange` 底座 | Linux copy_file_range / Win 回退 | 用户态 memcpy | 介质内 memcpy | 服务端零流量拷贝 |
| `Mmap` 底座 | 手工 mmap（非 BCL MMF） | Reserved 直址实时可见 / Sparse 物化副本（Flush 写回） | 文件载体 MMF（碎片文件自动物化） | ✗ |
| DIO 对齐基 | 设备几何（探测） | **512 模拟几何**（磁盘行为保真） | 载体 DIO 纪律（弹跳窗内建） | ✗ |

#### C. 元数据面

| 维度 | Disk | Mem | Raw | Remote |
|------|------|-----|-----|--------|
| Open/Stat 成本 | syscall（~7μs） | 内存直达（~50-120ns） | 内存直达（~50-225ns） | 网络往返（本机 ~0.5ms / 真云几十 ms） |
| FileExtra 存储 | xattr/ADS → sidecar 回退 | 槽字段（锁内原子） | 独立块（≤1536B） | 对象用户元数据（**Flush 后 fs 级可见**） |
| 枚举 | readdir 循环 | 内存 | 内存（结果排序） | ListObjectsV2 单往返（FsEntry 融合零额外 Head） |
| 卷几何 | 探测；FreeSpace 常 -1 | 配额推导（精确） | **精确**（superblock+位图） | 头信息/未知 |

#### D. 并发与锁（实测扩展性见 [perf/io-performance.md](perf/io-performance.md) §10）

| 维度 | Disk | Mem | Raw | Remote |
|------|------|-----|-----|--------|
| `Append` 原子性 | **全介质同契约**：文件级原子预留（同 fs 任意句柄并发追加落点不交） ||||
| 写并发模型 | 内核 per-inode 锁（多文件真隔离） | Sparse RW Gate（读共享写独占）/ Reserved epoch 锁外读写双高 | 全局元数据锁（**单写者模型**——并发写用单句柄 Append 或上层聚合） | staging 内存层 |
| 读并发 | pread 无锁 | Reserved 锁外（6×扩展）/ Sparse 读共享 | 锁外快照（CoW 区间 + epoch 延迟回收，4×扩展） | 句柄级 LRU 缓存 |
| RangeLock | LockFileEx / F_OFD_SETLK | 进程内区间表 | 进程内逻辑锁（单实例下完备） | ✗ |
| 卷锁 | lock file（崩溃自愈） | 进程内真锁（行为保真） | **内建**（打开即排他） | fencing 尽力型 |

#### E. 迁移与采集（详见 §10）

| 维度 | Disk | Mem | Raw | Remote |
|------|------|-----|-----|--------|
| TCA1 结构化采集/还原 | ✓（4×4 矩阵全格成立） | ✓ | ✓ | ✓ |
| 字节直拷快道（dd） | ✗ | ✗ | **✓**（ContiguousCapture——逐载体连续） | ✗ |
| 存档即活卷 | ✗（目录树非单工件） | ✗ | **✓**（`.raw` = 介质即格式——可只读挂载检视/抽取） | ✗ |
| 维护门闩（静默快照前置） | ✗（用 AcquireExclusive 组合） | ✓ | ✓ | ✓ |

---

## 8. 常见陷阱（按踩坑概率排序）

| # | 陷阱 | 要点 |
|---|------|------|
| 8.1 | DIO 三重对齐 | offset/length/buffer 按 `h.RequiredAlignment`（Win buffer 按内存页）；对齐 buffer 必须走池（`AlignedMemoryManager`/`PinnedBufferPool`）；`RequiredAlignment` 管读写、`AllocationUnit` 管空间操作——两基准并存不混用 |
| 8.2 | FileSharing 是 advisory | 仅约束同进程同 fs 实例；跨进程用卷锁；外部原生 IO 不受保护 |
| 8.3 | 删除语义平台差异 | Win 无 `Sharing.Delete` 拒删被开文件；POSIX/mem 延迟释放（旧句柄读旧数据）——删前先回收句柄 |
| 8.4 | `Append` ≠ 强制追加 | Mode.Append 只初始化游标；多写者追加 = `h.Append()`（文件级原子预留——同 fs 任意句柄落点不交） |
| 8.5 | Append 失败预留不回滚 | 回退会吞噬他人预留；异常带 `ReservedOffset`（失败区间 = 读零洞） |
| 8.6 | mem Sparse 映射写时差 | 视图写 Flush/Dispose 才写回；实时可见用 Reserved 或 Write 路径 |
| 8.7 | `IMappedSection` 必须 Dispose | 独立 fd/引用——不 Dispose = 泄漏；View 越界 = 段错误（unsafe 语义） |
| 8.8 | 池归还协议 | Dispose=归还不是关闭；真关闭仅三出口；归还后不得触碰引用；Acquire/Release 恰好配对（下溢 Debug 即抛） |
| 8.9 | memfs `Default` 全局共享 | 测试隔离一律 `Create()` 私有卷（配 Capacity 顺带配额断言） |
| 8.10 | memfs 分配模式选型 | Sparse=按页占用/物化映射/per-file RW Gate（读共享写独占）+ **免清零租借**（整页覆写零清零税）+ **预分配物理化**（`PreallocateSize` = 物理预留纯 memcpy 热道——Disk fallocate/Raw unwritten 同语义；`SetLength` 才是逻辑扩展）；Reserved=创建即占/直址零拷贝/无锁数据面（epoch 读者+freeze 屏障，读写双高）——高并发写密集同文件选 Reserved |
| 8.11 | memfs 模拟边界 | 不模拟：跨进程可见性/ACL/符号链接/大小写不敏感（Ordinal 对齐 Linux）；**已支持：目录树/文件时间戳**（目录时间不追踪，Stat 诚实 MinValue/null） |
| 8.12 | 范围锁 advisory + OFD | 禁依赖 Win mandatory 强化；本层 Linux 用 F_OFD_SETLK（避开进程级 fcntl）；同句柄重锁不保证幂等——调用方去重 |
| 8.13 | mmap × DIO 混用 | 一致性边界由内核决定；视图生命周期与 Move/Delete 互斥由调用方协调 |
| 8.14 | 卷锁成对释放 + 非重入 | lease RAII；fs.Dispose 时持有 = 违约（强制释放 + 告警）；lock file 崩溃自愈 |
| 8.15 | CopyRange 部分失败不回滚 | 目标留半截；已完成长度经 `CompletedLength` 携带——调用方自管残段 |
| 8.16 | Dispose × 在途异步 | 不等待不取消——先收敛（await/取消）再 Dispose；实现侧 in-flight 计数告警 + 5s 超时强关兜底 |
| 8.17 | 磁盘映射/锁走手工 P/Invoke | 非 BCL MMF（OVERLAPPED 复刻句柄 IOCP 不兼容）/ 专用非 OVERLAPPED 锁句柄（裸 LockFileEx 会崩）——已根治，消费者无感知 |
| 8.18 | mem 代际失效 | 槽复用/拔盘/强制转移后操作抛 `FileIOException(NotFound)`（ABA 防护——清晰异常非静默串数据） |

## 9. 差异声明：mem vs 磁盘可见性时点（映射场景）

| 场景 | 磁盘 | mem(Sparse) | mem(Reserved) |
|------|------|-------------|---------------|
| `Map(RW)` 写 → Read 可见 | 立即（视图即页缓存） | Flush/Dispose 时 | 立即（直址） |
| `Map(RO)` 写保护 | ✓ OS 级 | ✓（副本隔离） | **✗ 静默写透**（`Memory<byte>` 无法只读化——已知平权偏差；硬只读用 Sparse 卷或磁盘） |
| 映射期间 `PunchHole` | 视图读零 | 副本同步清零 | 物理清零自然读零 |
| 映射期间 `Move` | POSIX inode：视图跟数据走 | 副本隔离天然有效 | 槽跟数据走：旧视图钉旧 buffer |
| 映射期间 `Delete`+重建同名 | 视图指旧 inode | 副本快照 | 旧视图钉旧数据——读旧写旧不串新 |
| `EnumerateAllocatedRanges` 粒度 | fs 块粒度 | 页粒度（PageSize） | 记账区间（字节精确） |

区间报告契约：统一**块粒度对齐 `AllocationUnit`**（跨介质统一断言的契约选择——ext4 实际可字节精确，收紧为统一）。

---

## 10. 采集/还原/迁移管线（RootSpaceImage——导出/导入/启动）

> 一套代码通吃四介质（只认 `IFileSystem` 接口平面）：**4 源 × 4 目标 = 16 格全部成立**。
> 产物形态两种：**TCA1 结构化流**（清单 + 数据帧 + CRC——跨介质内容镜像）与 **Raw 字节直拷快道**（整卷 dd）。
> 网络传送层 [`TC.Tier.Core.IO.Net`](../../TC.Tier.Core.IO.Net/)（`NetworkImageTransfer.Send/ReceiveTo`）同格式推流。

### 10.1 导出（Capture——任何介质 → TCA1 存档）

```csharp
using (var out_ = File.Create("backup.tca"))
{
    var summary = RootSpaceImage.Capture(sourceFs, out_, new ImageOptions
    {
        Compression = ImageCompression.Zstd,   // None（配快道零拷贝）/ ZLib / Zstd
        QuietSource = true,                    // ★ 自动进维护门闩——静默快照（源置位 MaintenanceGate 时）
    });
    // summary: EntryCount / FrameCount / RawBytes（审计）
}
```

**保真清单**：目录树全结构、文件内容（逐帧 CRC）、**稀疏洞边界**（不搬零字节）、**unwritten 预分配**
（`PreallocateSize` 语义重建——物理预留 + 读零 + 写转换）、**FileExtra**（≤1536B）。时间戳仅记录于清单
供审计（`IFileSystem` 无时间戳写入平面——诚实表达）。

### 10.2 导入/启动（Restore——存档 → 任何空根空间）

```csharp
// 目标必须为空（v1 无合并语义——非空抛 AlreadyExists；根目录不存在自动 EnsureRoot）
using (var in_ = File.OpenRead("backup.tca"))
    RootSpaceImage.Restore(in_, targetFs, new ImageOptions { VerifyChecksums = true });
```

"启动"的四种形态（同一份存档，按需选择落点）：

| 启动形态 | 落点 | 一句话 |
|---------|------|--------|
| 测试环境复活 | 新 `MemoryFileSystem` | CI/调试零盘启动——内容/Extra/稀疏全保真 |
| 落盘部署 | `DiskFileSystem` 目录 | 解档为宿主可直接查看的目录树 |
| **制成存档卷** | `RawFileSystem` | 还原进 `.raw` = 单工件（**存档即活卷**） |
| 云归档 | `RemoteFileSystem` | 存档上云（TCA1 + 压缩） |

### 10.3 存档即活卷（Raw 独有——不解包使用）

```csharp
// 制档：X → .raw（经 Restore 或直接 Transfer）
using (var in_ = File.OpenRead("backup.tca"))
    RootSpaceImage.Restore(in_, RawFileSystem.Format(RawCarrier.File("archive.raw"),
        new RawFormatOptions { CapacityBytes = 10L << 30 }));

// 只读挂载检视/抽取（不打扰原档）
using (var ro = RawFileSystem.Open(RawCarrier.File("archive.raw"), new RawOpenOptions { ReadOnly = true }))
    foreach (var e in ro.EnumerateEntries("*", recursive: true)) { /* 检视 */ }

// 需要运行时：直接读写打开（同一份 .raw）
using var live = RawFileSystem.Open(RawCarrier.File("archive.raw"));
```

### 10.4 介质间转移（Transfer——能力位自动路由）

```csharp
var summary = RootSpaceImage.Transfer(sourceFs, targetFs, options);
// 源与目标都置位 ContiguousCapture（= Raw↔Raw，含 .raw 文件 ↔ 块设备互拷）→ dd 字节直拷快道
//   （双侧维护租约 + 容量预检 D6：目标不足抛 DiskFull 且零字节受损 + 镜像后目标重载内存态）
// 其余组合 → TCA1 结构化管线（流经宿主临时文件中转，自清理）
```

### 10.5 实测全链路（2026-08-19 验证）

Mem 活卷（目录树 + 稀疏 100MB 文件 + FileExtra + 预分配段）→ TCA1 导出 → 还原 **Mem / Disk / Raw 三路
全通过**（内容、Extra、稀疏长度逐项保真）；Raw→Raw Transfer 走字节直拷快道；`.raw` 存档只读挂载检视通过。
复现探针形态见上列代码。

### 10.6 注意事项

1. **还原目标必须为空**——显式失败优于静默覆盖（v1 无合并语义）；
2. 远程目标的空目录/unwritten 语义按能力位诚实降级（`EmptyDirectories`/预分配退化洞——读零保持）；
3. 快道（字节直拷）产物 = **整卷镜像**（介质身份随载体），结构化产物 = **内容镜像**（可跨介质）——用途不同按需选；
4. 压缩会杀死文件→文件零拷贝（必须过用户态）——"压不压缩"是吞吐开关；
5. 采集前静默责任：`QuietSource=true` 只挡 fs 层写操作——**消费者自己的后台任务先自行收敛再采集**（§8 维护门闩责任划分）。
