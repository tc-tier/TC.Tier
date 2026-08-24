using TC.Tier.CodeGen;

// ★ 内建 Key 特化封闭注册（ring-generic-key 设计稿 §2.2）——RingOfLong 由生成器产出，
//   消费方（测试组合根/TierKV）直接用封闭类型；自定义 Key 在消费程序集自行 [assembly: RingKey(typeof(...))]。
[assembly: RingKey(typeof(long))]
