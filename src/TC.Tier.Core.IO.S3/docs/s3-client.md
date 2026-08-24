# TC.Tier.Core.IO.S3——S3 兼容对象存储客户端 + 统一远程 IO 使用指南

> **定位**：`IObjectStore` 的默认实现（SigV4 自写）——一个客户端覆盖全部 S3 兼容云（S3 / OSS(S3 兼容端点) / MinIO / R2 / B2 / 腾讯 COS）；经 `RemoteFileSystem` 桥升格为**统一文件 IO 协议**（`IFileSystem`）的第三介质。
> **依赖**：**零外部包**（csproj 唯一引用 = `ProjectReference → TC.Tier.Core`；HTTP/加密/XML 全 BCL）。
> 本文档自包含：对象层（§1–§6）与统一远程 IO 层（§7–§9）的完整用法。

---

## 第一部分：对象层（IObjectStore 直用）

### 1. 快速开始（各云接入矩阵）

```csharp
using TC.Tier.Core.IO.S3;

// MinIO / 自建（path-style——两态皆可，默认）
using var minio = S3ObjectStore.Create(new S3ClientOptions
{
    Endpoint = "http://minio:9000",
    Bucket = "tier-logs",
    Credentials = new StaticCredentials("minioadmin", "minioadmin"),
});

// 腾讯 COS（★ 必须 virtual-host——path-style 会把整段路径当 key；实测 28/28）
using var cos = S3ObjectStore.Create(new S3ClientOptions
{
    Endpoint = "https://cos.ap-chengdu.myqcloud.com",   // 区域域名（桶名含 appid 后缀）
    Bucket = "tc-1253530278",
    Credentials = new StaticCredentials("<SecretId>", "<SecretKey>"),
    UseVirtualHostAddressing = true,
});

// AWS S3 / OSS(S3 兼容端点) / R2 / B2——换 endpoint/credentials 即达
```

| 云 | endpoint | 寻址 | 实测状态 |
|---|---|---|---|
| MinIO（RELEASE.2025-01-20） | `http://host:9000` | 两态皆可 | ✅ 契约套 25/25 |
| 腾讯 COS | `https://cos.<region>.myqcloud.com` | **必须 vhost** | ✅ 契约套 28/28（含会话治理/流式 List/chunked PUT） |
| AWS S3 | `https://s3.<region>.amazonaws.com` | 两态皆可 | SigV4 黄金向量保底；真云终验按需 |
| OSS / R2 / B2 | 各自 S3 兼容端点 | 按厂商文档 | 未逐家实测（SigV4 黄金向量 + 假服务器覆盖协议面） |
| COS 原生 V5 API | — | — | 不支持（S3 兼容端点已实测覆盖，V5 不再实现） |

### 2. S3ClientOptions 全参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `Endpoint` | 必填 | `scheme://host[:port]` |
| `Bucket` | 必填 | path-style 为桶名；vhost 形态下为 `{bucket}-{appid}` 全名（COS） |
| `Region` | `us-east-1` | 签名 scope 用（MinIO 默认即可） |
| `Credentials` | 必填 | `ICredentialProvider`（§3） |
| `Timeout` | 100s | 单请求粒度（含大对象上传——非 HttpClient 全局） |
| `MaxRetries` / `RetryBaseDelay` | 3 / 200ms | 幂等操作指数退避 + 抖动（§6 重试矩阵） |
| `UseVirtualHostAddressing` | `false` | `{bucket}.{host}/{key}` 寻址——实际请求主机 = 桶名前缀域名（COS/R2 必开）。⚠️ URL 主机 = `HostHeader`：与 `SigningHost` 并用时 URL 直接指向 `SigningHost`（绕过 endpoint 直连云端）——"vhost 走反代"形态当前不可表达 |
| `SigningHost` | `null` | 连接 Host 与**签名 Host** 解耦——经自有域名反代到云原生域名时，签云域名（SigV4 所签 = 服务端所见，反代层负责 Host 改写与 SNI）。⚠️ 仅 path-style 组合有效（COS 实测拒绝此形态，见 §5 表） |
| `PooledConnectionLifetime` / `PooledConnectionIdleTimeout` | 10 分钟 / **60s** | 连接池双防线（§6a）——防死连接复用导致的周期性 SSL 抖动 |
| `SupportsConditionalPut` / `SupportsConditionalDelete` | `true` | 能力位声明（老端点按实际关闭→桥层锁降级） |
| `SupportsStrongList` | `true` | 写后立即可见（老 OSS 最终一致→读后短重试） |

### 3. 凭证源（ICredentialProvider）

```csharp
new StaticCredentials(accessKey, secretKey);   // 部署期固定（MinIO/自建典型）
new EnvironmentCredentials();                  // AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY（每次重读——外部 STS 刷新器改环境变量即生效）
// 自定义：实现 ICredentialProvider（配置文件/STS/Vault——每次签名前取当前凭证，过期刷新零改动）
```

会话 token（STS）：`S3Credentials(access, secret, sessionToken)`——签名自动含 `x-amz-security-token` 头。

### 4. 使用面（IObjectStore 全量）

```csharp
// 六件套
await store.PutAsync("seg-001", data, metadata);            // 整对象原子替换（元数据随 PUT 提交）
await store.PutAsync("seg-001", stream, length: -1);        // ★ 未知长度：spool 后 chunked 上传（零整驻内存）
var n = await store.GetAsync("seg-001", offset, buf);       // Range GET；offset≥长度→0（416 归一不抛）
var info = await store.HeadAsync("seg-001");                // 不存在→null
await store.DeleteAsync("seg-001");                          // 幂等
var entries = await store.ListAsync("logs/");                // 前缀枚举（分页内部归一）

// 条件写（fencing 底座——厂商强制力不一，客户端前置校验兜底归一）
await store.PutAsync("lock", token, condition: new PutCondition(IfMatch: null, IfNoneMatch: "*"));
await store.DeleteAsync("lock", new DeleteCondition(etag));  // token 防误删

// multipart（桥层编排底座；三禁令：禁跳 part / Create 不重试 / Complete NoSuchUpload→NotFound）
var session = store.CreateMultipartUpload("big", metadata);
var p1 = await session.UploadPartAsync(1, partData);
var p2 = await session.UploadPartCopyAsync(2, "src", 0, len);   // 服务端零出口流量
await session.CompleteAsync([p1, p2]);

// 会话治理（孤儿清理/运维面）
var sessions = await store.ListMultipartUploadsAsync();
await store.AbortMultipartUploadAsync(key, uploadId);        // 幂等（NoSuchUpload 视为成功）

// 流式 List（大桶——S3 实现真分页流式；其他实现 DIM 包装 ListAsync）
await foreach (var e in store.ListStreamingAsync("logs/")) { ... }

// 同步便捷包装（低频路径——SyncAsyncBridge 有界桥接，非裸 GetResult）
store.Put("k", data);  store.Head("k");  store.List("prefix");
```

**PUT(Stream) 三形态**：

| 流形态 | 路径 |
|---|---|
| 可寻 + 长度已知 | 单遍流式 SHA-256 → 回卷 → 单段签名上传 |
| **不可寻 + 长度已知** | **chunked 流式签名直传**（`STREAMING-AWS4-HMAC-SHA256-PAYLOAD` 链式分帧：seed 签名 → 每 128KiB chunk 链式派生 → 终帧；免整驻免双遍哈希；单次发送不重试） |
| **长度未知（length<0）** | spool 临时文件后 chunked 上传（磁盘中转——零整驻内存） |

### 5. 厂商差异（适配器内吸收——消费者零感知）

| 差异 | 吸收方式 |
|---|---|
| COS path-style 失真（整段路径被当 key） | 文档约束：`UseVirtualHostAddressing=true` |
| COS 经反代 path-style + `SigningHost` 改写签名 Host 仍被拒（`SignatureDoesNotMatch`，2026-08-18 实测） | vhost 要求在**签名规范形态**层面（服务端按 bucket-in-Host 计算规范请求）——反代改写绕不过；COS 只可用原生端点 + vhost（反代需 vhost 域名形态透传，另议） |
| COS/MinIO 条件 PUT 忽略/静默创建 | 客户端前置 Head + 本地校验（接受极小竞态——与条件 DELETE 同款） |
| 条件 DELETE 强制不一（MinIO 失配不拦） | Head 校验 + 无条件删常态化 |
| XML xmlns 使用不一（AWS/MinIO） | 解析一律 LocalName 匹配（命名空间免疫） |
| ListV2 分页参数差异 | continuation-token 循环归一 |
| S3 200 + Error body（multipart complete 延迟失败） | 响应体根元素检测 → 按错误映射抛出 |

### 6a. 连接稳定性（防周期性 SSL 抖动）

已知坑（Aliyun OSS SDK 时代）：服务端 60-90s 主动断开空闲 TCP 连接，而客户端连接池
`PooledConnectionLifetime = 0`（永不回收）→ 池中堆积死连接 → 高并发复用死连接 → SSL 握手失败
周期性抖动。本客户端三层防御：

1. **`PooledConnectionIdleTimeout` 默认 60s**（空闲即关）——客户端在服务端断开**之前**主动回收，
   池中不留死连接；
2. **`PooledConnectionLifetime` 默认 10 分钟**（总寿命到期重建）——兜底长寿命连接的中间设备劣化；
3. .NET 8 `SocketsHttpHandler` 复用失败自动重建连接重发（stale 自愈）。

超时模型：`Timeout` 是**每请求粒度**（全局 HttpClient = Infinite）——大对象上传不会被小请求超时
误杀，反之亦然。两个池参数均可按目标端点调整（例如服务端空闲阈值更短的端点，下调 idle timeout）。

### 6. 错误与重试

错误出口统一 `FileIOException`：

| S3 响应 | `IOError` |
|---|---|
| 404 / NoSuchKey / NoSuchUpload | `NotFound` |
| 412 / PreconditionFailed | `PreconditionFailed` |
| 403 / SignatureDoesNotMatch / InvalidAccessKeyId | `AccessDenied` |
| 507 | `DiskFull` |
| 416 RangeNotSatisfiable | （GetAsync 返回 0——不抛） |
| 501 | `Unsupported` |

重试矩阵：GET/HEAD/PUT/DELETE/List/UploadPart/Complete 幂等可重试（5xx/429/网络抖动指数退避）；
`CreateMultipartUpload` **不重试**（响应丢失重发会双开会话）；chunked 直传单次发送（源不可回卷——重试语义归 spool 路径）。

---

## 第二部分：统一远程 IO 协议（RemoteFileSystem）

对象层之上，`RemoteFileSystem` 把任意 `IObjectStore` 升格为**统一文件 IO 协议**（与磁盘/内存平权的
`IFileSystem` 第三介质）——消费者只认 `IFileSystem`/`IFileHandle`，从不问"数据在哪"。

### 7. 构造与打开

```csharp
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;

using var store = S3ObjectStore.Create(cosOptions);
using var fs = RemoteFileSystem.Create(store, new RemoteFileSystemOptions
{
    KeyPrefix = "engine-a/",          // 多引擎共桶隔离（对象键 = KeyPrefix + path）
    SpillDirectory = "/var/tmp",      // staging 超内存预算的落盘根（null=纯内存超限 DiskFull；无盘形态用 SpillToMemory=true）
});

// 打开语义与磁盘/mem 完全同构（IFileSystem 契约）
using var w = fs.Open("seg-001", new FileOpenOptions
{
    Access = FileOpenAccess.ReadWrite,
    Mode = FileOpenMode.OpenOrCreate,   // ★ 显式给——裸默认是 OpenExisting（与其他介质同规则）
});
```

**RemoteFileSystemOptions 调参表**：

| 参数 | 默认 | 说明 |
|---|---|---|
| `KeyPrefix` | `""` | 命名空间隔离；path 经共享校验规则（分隔符/越根/非法字符全拒）——不可能访问同桶其他前缀 |
| `StagingMemoryLimit` / `StagingPageSize` | 64MB / 64KiB | 写句柄 staging 预算与页粒度（延迟加载/回填的最小单位） |
| `SpillDirectory` / `SpillToMemory` | null / false | 超预算落盘根 / 无盘内存卷变体（互斥；两者皆空超限 = DiskFull） |
| `MultipartThreshold` / `PartSize` / `MaxParts` / `MaxConcurrency` | 8MB / 8MB / 10000 / 4 | Flush 的 multipart 编排 |
| `ReadCacheBytes` / `PrefetchPages` | 4MB / 4 | 读句柄页缓存与预取窗口（`Advise(Sequential)` 放大 4×） |
| `LeaseTimeout` | 60s | fencing 卷锁租约（心跳超时接管窗口） |
| `OrphanUploadCleanup` | null | 非空 = 构造时扫描清理早于阈值的残留 multipart 会话（崩溃碎片回收） |

### 8. 核心语义（与磁盘/mem 的差异——必须知道的三条）

1. **★ Flush 是唯一持久化点**。`Write`/`Append` 只进 staging（页缓存的远端同构物）即返回；
   **任何 Dispose 都不触发上传**——池内句柄 Dispose = 归还（staging 留池续用）、池外 Dispose = 关闭
   （未 Flush 的数据丢弃 = "未 fsync 即丢"）。需要"用完即持久"必须显式：

   ```csharp
   using var h = pool.Acquire("seg-001", opts);   // 或 fs.Open
   h.Append(payload);
   h.Flush();                                     // ← 唯一持久化点（multipart complete——此前崩溃旧对象完好）
   ```

2. **读句柄不追新**：读句柄的 Length 和数据是 **Open 时刻的快照**——其他句柄 Flush 的追加对它不可见，
   需要追新 = 重新 Open。写句柄自身的 read-your-writes 天然成立（读走 staging）。

3. **随机覆写已有大文件 = 逐区间按需拉取**（延迟加载）：纯追加句柄永不拉历史（追加路径零网络）；
   随机覆写会按页粒度 Range GET 拉满所触区间——GB 级文件秒级 + 全额带宽。追加式访问模式是远程介质的
   正确姿势（`RandomWrite` 能力位不置位即为此）。

**常用操作速查**：`Append` 原子预留（同 fs 跨句柄落点不交）/ `PunchHole` 读零语义（无对齐约束——
AllocationUnit=1）/ `SetLength` 截断后扩展读零（不复活旧数据）/ `Lock`·`Map`·`CollapseRange` 抛
Unsupported（能力位表达）/ `Move` = 服务端 Copy+Delete / `Enumerate` = ListObjectsV2 前缀枚举
（恢复扫描零额外 Head）/ xattr = PUT 原子快照（Flush 随对象提交）。

### 9. 增量 Flush 与恢复

- **增量 Flush**：二次 Flush 只上传**脏 part**，未改 part 走服务端自拷贝（零出口流量）——追加负载的
  出口流量 O(增量) 而非 O(总长)（真实 COS 实测耗时比 38%）。
- **洞元数据读加速**：`PunchHole` 后 Flush 会把洞区间编码进对象元数据——后续读句柄命中洞区间本地
  返零、不发 GET（实测 8MB 洞读 38ms）。
- **恢复**：`fs.Enumerate()` 返回 (Name, Size) 融合列表——引擎扫段重建段表与磁盘路径同构；
  崩溃未 Flush 的 staging 丢 = 未 fsync 丢，**恢复协议零修改**。
- **fencing 卷锁**：`fs.AcquireExclusive(timeout)` = lock 对象 + 条件 PUT 抢建 + 心跳超时接管 +
  token 防误删——**尽力型**（仅防意外双开；引擎正确性由段表 lease 单写者协议承担）。

### 10. 测试与性能基线

测试三层门禁（离线黄金向量/假服务器 → MinIO 容器 → 真云环境变量）见 COORDINATION.md §4；
桥级契约测试在 `tests/TC.Tier.Core.Tests/IO/Remote/`（staging 语义/池协议/增量断言/洞读零 GET/孤儿清理等 60+ 例）。

**性能基线（真实 COS @ ap-chengdu，2026-08-18 实测）**：

| 指标 | 数值 |
|---|---|
| Range GET 延迟 | p50 = 48ms / p99 = 87ms（客户端→成都 RTT 主导） |
| 桥级全量 Flush（128MB multipart） | 12.8 MB/s |
| 桥级**增量 Flush**（追加 32MB） | 耗时比 38%（未改 part 服务端自拷贝） |
| 桥级洞区间读（8MB 整读） | 38ms（本地零填充） |

探针：`benchmarks/TC.Tier.S3PerfProbe`（对象层吞吐/延迟/TPS + `bridge` 模式桥级场景）。
