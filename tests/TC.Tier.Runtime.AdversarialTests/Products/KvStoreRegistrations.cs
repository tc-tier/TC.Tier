using TC.Tier.CodeGen;

// ★ 对抗性项目 [KvStore] 注册（Products.Tests/KvStoreRegistrations.cs 同款契约形态）——
//   sweep kill 对抗线（KvExpirySweepAdversarialTests）所需封闭形态在本程序集编译期产出。
[assembly: KvStore(typeof(long), typeof(long))]
