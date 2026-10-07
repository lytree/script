// ─────────────────────────────────────────────────────────────
// 本文件移植自 chunsou（Python）: https://github.com/Funsiooo/chunsou
// 对应原项目文件：modules/core/icon.py
// Copyright (C) Funsiooo    Licensed under GPL-3.0（见 LICENSE）
// ─────────────────────────────────────────────────────────────

namespace Chunsou;

/// <summary>
/// MurmurHash3 x86_32，用于计算 favicon 的 mmh3 哈希。
/// 对应 Python 的 mmh3.hash(bytes)（有符号 32 位），
/// icon.py 中取 (h &amp; 0xffffffff) 转无符号再转十进制字符串。
/// </summary>
public static class Murmur3
{
    private const uint C1 = 0xcc9e2d51;
    private const uint C2 = 0x1b873593;

    public static uint Hash32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var h1 = seed;
        var length = data.Length;
        var i = 0;

        // 4 字节一块
        for (; i + 4 <= length; i += 4)
        {
            var k1 = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;

            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = h1 * 5 + 0xe6546b64;
        }

        // 尾部剩余字节
        var k1Tail = 0u;
        switch (length & 3)
        {
            case 3:
                k1Tail ^= (uint)data[i + 2] << 16;
                goto case 2;
            case 2:
                k1Tail ^= (uint)data[i + 1] << 8;
                goto case 1;
            case 1:
                k1Tail ^= data[i];
                k1Tail *= C1;
                k1Tail = RotateLeft(k1Tail, 15);
                k1Tail *= C2;
                h1 ^= k1Tail;
                break;
        }

        // 收尾混淆
        h1 ^= (uint)length;
        h1 ^= h1 >> 16;
        h1 *= 0x85ebca6b;
        h1 ^= h1 >> 13;
        h1 *= 0xc2b2ae35;
        h1 ^= h1 >> 16;

        return h1;
    }

    private static uint RotateLeft(uint x, int r) => (x << r) | (x >> (32 - r));
}
