#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

// ═══════════════════════════════════════════════════════════════
// 128-bit CAS — x64/ARM64 内联指令（16B 对齐）+ 分片锁兜底（非对齐）
// ═══════════════════════════════════════════════════════════════
//
// 设计要点：
//   • cmpxchg16b（x64）/ ldaxp-stlxp（ARM64）均要求 16 字节对齐，非对齐
//     触发 #GP（x64）或对齐异常（ARM64）。.NET 托管堆字段不保证 16B 对齐，
//     故必须检查对齐：对齐走硬件 CAS（~5ns），非对齐走兜底。
//   • 旧版兜底用「进程级单锁 _tc_lock」串行化所有非对齐 CAS——多核下是致命瓶颈。
//     现改为 256 桶地址哈希分片锁：不同地址的非对齐 CAS 可并行，仅同桶串行。
//   • 生产路径（水位字段）应走 AlignedMemoryManager 对齐分配，使对齐检查恒真，
//     永远命中硬件快路径。兜底仅为托管堆字段的防御性正确性保障。

// ── 导出宏（防符号剥离）──
#if defined(_MSC_VER)
  #define TC_API __declspec(dllexport)
#elif defined(__GNUC__) || defined(__clang__)
  #define TC_API __attribute__((visibility("default")))
#else
  #define TC_API
#endif

// ── 对齐检查 ──
#define IS_ALIGNED_16(ptr) (((uintptr_t)(ptr) & 0xF) == 0)

// ── 分片自旋锁兜底（256 桶，地址哈希）──
//   旧版用单把 _tc_lock 串行化所有非对齐 CAS，多核下严重退化。
//   256 桶按地址哈希分散竞争：不同地址的兜底 CAS 可并行，仅同桶串行。
//   兜底是冷路径（仅托管堆非对齐字段命中），桶数 256 足够避免热点。
#define TC_FB_BUCKETS 256
static volatile int _tc_fb_locks[TC_FB_BUCKETS];

#if defined(_MSC_VER)
#include <intrin.h>
#define TC_PAUSE() _mm_pause()
static inline volatile int* tc_fb_lock_for(void* p)
    { return &_tc_fb_locks[((uintptr_t)p >> 4) & (TC_FB_BUCKETS - 1)]; }
static inline void tc_fb_lock(volatile int* lk)
    { while (_InterlockedExchange((volatile long*)lk, 1)) TC_PAUSE(); }
static inline void tc_fb_unlock(volatile int* lk)
    { _InterlockedExchange((volatile long*)lk, 0); }
#else
#if defined(__x86_64__) || defined(__i386__)
#define TC_PAUSE() __builtin_ia32_pause()
#else
#define TC_PAUSE() __asm__ volatile("yield")
#endif
static inline volatile int* tc_fb_lock_for(void* p)
    { return &_tc_fb_locks[((uintptr_t)p >> 4) & (TC_FB_BUCKETS - 1)]; }
static inline void tc_fb_lock(volatile int* lk)
    { while (__sync_lock_test_and_set(lk, 1)) TC_PAUSE(); }
static inline void tc_fb_unlock(volatile int* lk)
    { __sync_lock_release(lk); }
#endif

// ── 软件 CAS（分片锁保护，非对齐兜底路径）──
static bool tc_cmpxchg128_fallback(
    void*        location,
    uint64_t*    old_lo,
    uint64_t*    old_hi,
    uint64_t     new_lo,
    uint64_t     new_hi)
{
    volatile int* lk = tc_fb_lock_for(location);
    tc_fb_lock(lk);
    uint64_t *p = (uint64_t *)location;
    bool ok = (p[0] == *old_lo && p[1] == *old_hi);
    if (ok)
        { p[0] = new_lo; p[1] = new_hi; }
    else
        { *old_lo = p[0]; *old_hi = p[1]; }
    tc_fb_unlock(lk);
    return ok;
}

#if defined(_MSC_VER)

// ── Windows x64 MSVC: _InterlockedCompareExchange128 ──
#include <intrin.h>
#pragma intrinsic(_InterlockedCompareExchange128)

TC_API bool tc_cmpxchg128(
    volatile void* location,
    uint64_t*      old_lo,
    uint64_t*      old_hi,
    uint64_t       new_lo,
    uint64_t       new_hi)
{
    // cmpxchg16b 要求 16B 对齐；非对齐走分片锁兜底（不触发 #GP）。
    if (!IS_ALIGNED_16(location))
        return tc_cmpxchg128_fallback((void*)location, old_lo, old_hi, new_lo, new_hi);

    __int64 expect[2];
    expect[0] = (__int64)*old_lo;
    expect[1] = (__int64)*old_hi;
    bool ok = _InterlockedCompareExchange128(
        (__int64 volatile *)location,
        (__int64)new_hi, (__int64)new_lo, expect) != 0;
    if (!ok) { *old_lo = (uint64_t)expect[0]; *old_hi = (uint64_t)expect[1]; }
    return ok;
}

#elif defined(__x86_64__) || defined(_M_X64)

// ── Linux/macOS x86-64: lock cmpxchg16b ──
TC_API bool tc_cmpxchg128(
    volatile void* location,
    uint64_t*      old_lo,
    uint64_t*      old_hi,
    uint64_t       new_lo,
    uint64_t       new_hi)
{
    // cmpxchg16b 要求 16B 对齐；非对齐走分片锁兜底（不触发 #GP）。
    if (!IS_ALIGNED_16(location))
        return tc_cmpxchg128_fallback((void*)location, old_lo, old_hi, new_lo, new_hi);

    uint64_t lo = *old_lo;
    uint64_t hi = *old_hi;
    uint8_t result;
    __asm__ volatile (
        "lock cmpxchg16b (%[loc])\n"
        "setz %[ret]"
        : [ret] "=r"(result), "+m"(*(volatile __int128*)location),
          "+a"(lo), "+d"(hi)
        : [loc] "r"(location),
          "c"(new_hi), "b"(new_lo)
        : "memory"
    );
    // cmpxchg16b 失败时 RAX/RDX 自动回写 old_lo/old_hi（通过 +a/+d 约束）
    if (!result) { *old_lo = lo; *old_hi = hi; }
    return result;
}

#elif defined(__aarch64__) || defined(_M_ARM64)

// ── Linux/macOS ARM64: ldaxp/stlxp CAS loop ──
TC_API bool tc_cmpxchg128(
    volatile void* location,
    uint64_t*      old_lo,
    uint64_t*      old_hi,
    uint64_t     new_lo,
    uint64_t     new_hi)
{
    // ldaxp/stlxp 要求 16B 对齐；非对齐走分片锁兜底。
    if (!IS_ALIGNED_16(location))
        return tc_cmpxchg128_fallback((void*)location, old_lo, old_hi, new_lo, new_hi);

    uint32_t result;
    __asm__ volatile (
        "1:  ldaxp x9, x10, [%[loc]]\n"
        "    cmp  x9, %[old_lo]\n"
        "    ccmp x10, %[old_hi], #0, eq\n"
        "    b.ne 2f\n"
        "    stlxp w11, %[new_lo], %[new_hi], [%[loc]]\n"
        "    cbnz w11, 1b\n"
        "    mov  %w[ret], #1\n"
        "    b    3f\n"
        "2:  str  x9, [%[out_lo]]\n"
        "    str  x10, [%[out_hi]]\n"
        "    mov  %w[ret], #0\n"
        "3:"
        : [ret] "=r"(result)
        : [loc] "r"(location),
          [old_lo] "r"(*old_lo), [old_hi] "r"(*old_hi),
          [new_lo] "r"(new_lo), [new_hi] "r"(new_hi),
          [out_lo] "r"(old_lo), [out_hi] "r"(old_hi)
        : "x9", "x10", "x11", "memory", "cc"
    );
    return result;
}

#else
#error "tc_cmpxchg128: unsupported architecture"
#endif
