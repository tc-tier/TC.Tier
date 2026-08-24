# TC.Tier.CodeGen

TC.Tier 的**源生成器**（analyzer 包）——编译期生成，零运行时反射。安装后自动提供：

## 能力

- **BinaryLayout**：为标注类型生成高效的二进制序列化/反序列化代码（布局 + codec）
- **TierFs 协议注册桥**：引用协议程序集（如 S3）即自动注册 TierFs 介质协议，消费方零配置

## 安装

```bash
dotnet add package TC.Tier.CodeGen
```

包内 analyzer 在编译期自动生效（无需额外配置）；项目需配合 `TC.Tier.CodeGen.Abstractions` 中的标注特性使用。

## 文档

- 完整文档站：https://docs.mytzz.top/
- API 参考：https://docs.mytzz.top/api/
