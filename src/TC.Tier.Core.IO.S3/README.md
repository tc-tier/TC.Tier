# TC.Tier.Core.IO.S3

TC.Tier 的 **S3 兼容对象存储**实现——SigV4 签名自写（零外部依赖），即插即用 S3 / 阿里云 OSS / MinIO / Cloudflare R2 / 腾讯云 COS。

## 能力

- **SigV4 流式签名上传**（大对象不分批落内存）
- **TierFs `network:///s3` 介质**：换 endpoint 即达，与 TC.Tier 引擎/结构层介质平权
- **IObjectStore 契约**（契约定义于 `TC.Tier.Core/IO/Remote`——任何实现经 `RemoteFileSystem` 桥升格为网络文件系统）
- MinIO 契约测试背书

## 快速开始

```csharp
using TC.Tier.Core.IO;

// endpoint 即换即用：S3 / OSS / MinIO / R2 / COS
var fs = TierFs.OpenOrCreate("network:///s3/endpoint/bucket/prefix");
```

## 依赖

- TC.Tier.Core

## 文档

- 完整文档站：https://docs.mytzz.top/
- S3 客户端指南：https://docs.mytzz.top/docs/core/s3-protocol.html
