# TC.Tier.Core.IO.S3 协调文档

> 本文件是 **S3 客户端层的"正确拼装"指南**：组件职责全景、正确用法、反模式。
> 它不重复每个类的 XML 注释，而是回答"**遇到 X 该用哪个积木、怎么用、什么绝对不要做**"。
>
> **层的范围**（五层架构的第三层·远程对象介质）：`IObjectStore` 的默认实现——SigV4 自写的
> S3 兼容客户端。**零外部包**（唯一引用 = `TC.Tier.Core`）；契约类型（`IObjectStore` /
> `ObjectStoreCapabilities` / 条件结构体等）在 Core/IO 定义，本层只做实现。
>
> 配合阅读：
> - [`docs/network-file-system-s3.md`](docs/network-file-system-s3.md) —— 消费者使用指南（两部分自包含：对象层 §1-6 快速开始/全参数/凭证源/厂商矩阵/错误重试；**统一远程 IO 协议 §7-9**——RemoteFileSystem 构造/调参/核心语义/增量 Flush/恢复）
> - [`../TC.Tier.Core/docs/io.md`](../TC.Tier.Core/docs/io.md) —— `RemoteFileSystem` 桥（把本层升格为 `IFileSystem` 第三介质；差异表/池化 Flush 置顶警告）
> - [`docs/sync-async-bridge.md`](../TC.Tier.Core/docs/sync-async-bridge.md) —— 同步包装的桥接底座（`ObjectStoreExtensions` 内部使用）

---

## 0. 一句话总纲

**一个客户端覆盖全部 S3 兼容云——换 endpoint/credentials 即达；厂商差异全部吸收在适配器内，永不上抛消费者。**

---

## 1. 组件全景

| 积木 | 文件 | 职责 | 何时用 / 红线 |
|------|------|------|---------------|
| `S3ObjectStore` | `S3ObjectStore.cs` | `IObjectStore` 实现：六件套 / 条件写（客户端校验兜底）/ multipart（含会话治理）/ CopyRange 编排 / 流式 List / chunked 直传 / 重试矩阵 | **唯一公开工厂** `S3ObjectStore.Create(options[, http])`；线程安全（HttpClient 并发共用） |
| `S3ClientOptions` | `S3ClientOptions.cs` | 全部配置：endpoint/bucket/region/凭证/超时/重试/寻址模式（vhost）/SigningHost/能力位声明 | 构造期校验；改配置=换实例（record 语义） |
| `SigV4` | `SigV4.cs` | 签名核心：RFC3986 编码器 / canonical request / HMAC 链 / chunked 流式签名（链式 string-to-sign） | 🔒 internal——正确性由 AWS 官方黄金向量逐字节保底 |
| `ChunkedSignedStream` | `ChunkedSignedStream.cs` | `STREAMING-AWS4-HMAC-SHA256-PAYLOAD` 分帧流（128KiB chunk 链签 + 终帧；`EncodedLength` 精确预计算设 Content-Length） | 🔒 internal；不可寻流直传的载体 |
| `S3Xml` | `S3Xml.cs` | XML 解析/构造（ListObjectsV2 / multipart / 错误体）——**命名空间免疫**（LocalName 匹配） | 🔒 internal；畸形响应容错（分页继续、条目跳过） |
| `ICredentialProvider` / `S3Credentials` / `StaticCredentials` / `EnvironmentCredentials` | 同名文件（一类型一文件） | 凭证三源：静态 / 环境变量（每次重读——外部 STS 刷新生效）/ 自定义 | 每次签名前取当前凭证；STS 会话 token 自动入签 |

**目录形态**：全部平铺本目录（9 文件）——层小不值得子目录；契约类型（`IObjectStore` 族，已按一类型族一文件拆分）在 [`../TC.Tier.Core/IO/`](../TC.Tier.Core/IO/)。

## 2. 正确用法要点

1. **生命周期**：`S3ObjectStore` 持有 `HttpClient`（连接池 10min 复用）——**长生命周期单例**，不要每请求建；`Dispose` 释放连接池。
2. **寻址模式**：默认 path-style（`{endpoint}/{bucket}/{key}`）；**COS 必须** `UseVirtualHostAddressing=true`（path-style 下 COS 把整段路径当 key——对象操作自洽失真、桶级/copy 穿帮，实测结论）。
3. **条件写语义**：本层做客户端前置 Head + 本地校验兜底（COS/MinIO 服务端强制力不足）——**接受极小竞态**，fencing 类强一致需求须在更高层（token/心跳校验）兜底。
4. **multipart 三禁令**：禁跳 part（对象=parts 顺序拼接，跳中段=错位）；`CreateMultipartUpload` 不重试（双开会话）；`Complete` 遇 NoSuchUpload→`NotFound`（调用方视为已 complete 回读校验）。
5. **流式 PUT**：不可寻流自动走 chunked 链签（单次发送）；未知长度（-1）自动 spool——**单次消费**，不要复用已失败的流重试。
6. **超时**：`Timeout` 是单请求粒度（默认 100s，大对象上传够用）——不要设成 HttpClient 全局。
7. **连接池双防线不可关闭**：`PooledConnectionIdleTimeout`（默认 60s）+ `PooledConnectionLifetime`
   （默认 10min）——防服务端断空闲连接后的死连接复用（Aliyun OSS 60-90s 断开、老 SDK 周期性 SSL
   抖动根因）。特殊端点可调，禁止调大 idle timeout 超过目标端点的服务端空闲阈值。

## 3. 反模式

### ❌ 反模式 1：每请求 new S3ObjectStore
连接池形同虚设、TLS 握手开销全额支付。**单例 + Dispose 收尾**。

### ❌ 反模式 2：对 COS 用 path-style
对象操作"看起来正常"（自洽失真），桶级 List/会话枚举/Copy 一用就 NoSuchKey——**最阴的坑**，vhost 必开。

### ❌ 反模式 3：拿条件写当强一致
本层条件写有极小竞态窗口（客户端校验兜底的固有代价）。fencing 抢占的正确性兜底在锁协议层（token 校验释放 / 心跳超时接管二次校验），不在 HTTP 条件头。

### ❌ 反模式 4：CreateMultipartUpload 失败后盲目重试
POST ?uploads 响应丢失时重发 = **双开会话**（孤儿碎片计费）。失败走 Abort/孤儿扫描清理，不盲目重发。

### ❌ 反模式 5：在热路径用同步包装
`ObjectStoreExtensions`（`Put`/`Get`/`List`…）是低频便捷路径（SyncAsyncBridge 有界桥接）——高频路径直用异步族（HttpClient 本质异步）。

## 4. 测试与验证基线

| 层 | 验证内容 | 状态 |
|---|---|---|
| 离线 | SigV4 黄金向量逐字节（AWS 官方向量）+ 假 S3 服务器（服务端重算签名/chunked 链逐帧校验/503 注入重试） | 全绿（每次 CI） |
| 容器 | MinIO 契约平权套（25 例） | 全绿 |
| 真云 | 腾讯 COS 全功能面（28 例——含会话治理/流式 List/chunked PUT/条件写） | 全绿（2026-08-18） |

覆盖矩阵：[`../TC.Tier.Core/docs/unit-test-coverage.md`](../TC.Tier.Core/docs/unit-test-coverage.md) §TC.Tier.Core.IO.S3。
