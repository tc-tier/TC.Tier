using TC.Tier.CodeGen;
using TC.Tier.Products.Kv;

// TierKv 基准宿主装配（W6 基线载体）——基准程序集自身的 [KvStore] 封闭注册
[assembly: KvStore(typeof(long), typeof(long))]
