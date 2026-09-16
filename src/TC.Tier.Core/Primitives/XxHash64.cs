// ══ 移植自 dotnet/runtime System.IO.Hashing (XxHash64.cs + XxHash64.State.cs, v8.0.0, MIT)——
//    自研化收编：算法/字节序/性能与官方逐位一致。下行为原版权头（MIT 要求保留）。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// 按上游实现：https://github.com/Cyan4973/xxHash/blob/f9155bd4c57e2270a4ffbb176485e5d713de1c9b/doc/xxhash_spec.md
//
// 说明：仅保留本仓库需要的静态一次性路径（XxHash64.HashToUInt64）；官方实例/byte[] API 未移植。
//    被 XxHash128（HashLength0To16/1To3 路径）引用内部 Avalanche。

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static TC.Tier.Core.Primitives.XxHashShared;

namespace TC.Tier.Core.Primitives;

internal static class XxHash64
{
    private const int StripeSize = 4 * sizeof(ulong);

    /// <summary>XXH64 雪崩（官方原样——XxHash128 短输入路径同用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Avalanche(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= Prime64_2;
        hash ^= hash >> 29;
        hash *= Prime64_3;
        hash ^= hash >> 32;
        return hash;
    }

    /// <summary>一次性 XXH64（seed 默认 0——与官方 <c>XxHash64.HashToUInt64(source)</c> 逐位一致）。</summary>
    public static ulong HashToUInt64(ReadOnlySpan<byte> source, long seed = 0)
    {
        int totalLength = source.Length;
        State state = new State((ulong)seed);

        while (source.Length >= StripeSize)
        {
            state.ProcessStripe(source);
            source = source.Slice(StripeSize);
        }

        return state.Complete((uint)totalLength, source);
    }

    private struct State
    {
        private ulong _acc1;
        private ulong _acc2;
        private ulong _acc3;
        private ulong _acc4;
        private readonly ulong _smallAcc;
        private bool _hadFullStripe;

        internal State(ulong seed)
        {
            _acc1 = seed + unchecked(Prime64_1 + Prime64_2);
            _acc2 = seed + Prime64_2;
            _acc3 = seed;
            _acc4 = seed - Prime64_1;

            _smallAcc = seed + Prime64_5;
            _hadFullStripe = false;
        }

        internal void ProcessStripe(ReadOnlySpan<byte> source)
        {
            source = source.Slice(0, StripeSize);

            _acc1 = ApplyRound(_acc1, source);
            _acc2 = ApplyRound(_acc2, source.Slice(sizeof(ulong)));
            _acc3 = ApplyRound(_acc3, source.Slice(2 * sizeof(ulong)));
            _acc4 = ApplyRound(_acc4, source.Slice(3 * sizeof(ulong)));

            _hadFullStripe = true;
        }

        private static ulong MergeAccumulator(ulong acc, ulong accN)
        {
            acc ^= ApplyRound(0, accN);
            acc *= Prime64_1;
            acc += Prime64_4;

            return acc;
        }

        private readonly ulong Converge()
        {
            ulong acc =
                BitOperations.RotateLeft(_acc1, 1) +
                BitOperations.RotateLeft(_acc2, 7) +
                BitOperations.RotateLeft(_acc3, 12) +
                BitOperations.RotateLeft(_acc4, 18);

            acc = MergeAccumulator(acc, _acc1);
            acc = MergeAccumulator(acc, _acc2);
            acc = MergeAccumulator(acc, _acc3);
            acc = MergeAccumulator(acc, _acc4);

            return acc;
        }

        private static ulong ApplyRound(ulong acc, ReadOnlySpan<byte> lane)
        {
            return ApplyRound(acc, BinaryPrimitives.ReadUInt64LittleEndian(lane));
        }

        private static ulong ApplyRound(ulong acc, ulong lane)
        {
            acc += lane * Prime64_2;
            acc = BitOperations.RotateLeft(acc, 31);
            acc *= Prime64_1;

            return acc;
        }

        // 上游注：NoInlining 防止 HashToUInt64 热路径时间预算耗尽导致 Span.Slice 退化为非内联调用
        //（https://github.com/dotnet/runtime/issues/85531）。保留。
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal readonly ulong Complete(long length, ReadOnlySpan<byte> remaining)
        {
            ulong acc = _hadFullStripe ? Converge() : _smallAcc;

            acc += (ulong)length;

            while (remaining.Length >= sizeof(ulong))
            {
                ulong lane = BinaryPrimitives.ReadUInt64LittleEndian(remaining);
                acc ^= ApplyRound(0, lane);
                acc = BitOperations.RotateLeft(acc, 27);
                acc *= Prime64_1;
                acc += Prime64_4;

                remaining = remaining.Slice(sizeof(ulong));
            }

            // 至多一次（尾部余量 < 8 时最多含一个 4B 段）
            if (remaining.Length >= sizeof(uint))
            {
                ulong lane = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
                acc ^= lane * Prime64_1;
                acc = BitOperations.RotateLeft(acc, 23);
                acc *= Prime64_2;
                acc += Prime64_3;

                remaining = remaining.Slice(sizeof(uint));
            }

            for (int i = 0; i < remaining.Length; i++)
            {
                ulong lane = remaining[i];
                acc ^= lane * Prime64_5;
                acc = BitOperations.RotateLeft(acc, 11);
                acc *= Prime64_1;
            }

            return Avalanche(acc);
        }
    }
}
