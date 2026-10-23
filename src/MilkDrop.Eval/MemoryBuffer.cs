namespace ProjectMEvalSharp;

/// <summary>内存缓冲（megabuf/gmegabuf），128 块 × 65536 个 double，按块懒分配。</summary>
public sealed class MemoryBuffer
{
    public const int BlockCount = 128;
    public const int ItemsPerBlock = 65536;
    public const int MaxIndex = BlockCount * ItemsPerBlock; // 8388608

    private readonly double[]?[] _blocks = new double[]?[BlockCount];

    public double[]? GetBlock(int block)
    {
        if (block < 0 || block >= BlockCount)
        {
            return null;
        }
        return _blocks[block] ??= new double[ItemsPerBlock];
    }

    public (double[] Arr, int Offset)? Address(int index)
    {
        if (index < 0 || index >= MaxIndex)
        {
            return null;
        }
        var arr = GetBlock(index / ItemsPerBlock);
        return arr is null ? null : (arr, index & (ItemsPerBlock - 1));
    }

    public double this[int index]
    {
        get
        {
            if (index < 0 || index >= MaxIndex)
            {
                return 0.0;
            }
            var arr = _blocks[index / ItemsPerBlock];
            return arr is null ? 0.0 : arr[index & (ItemsPerBlock - 1)];
        }
        set
        {
            if (index < 0 || index >= MaxIndex)
            {
                return;
            }
            var arr = GetBlock(index / ItemsPerBlock);
            if (arr is not null)
            {
                arr[index & (ItemsPerBlock - 1)] = value;
            }
        }
    }

    /// <summary>memcpy 移植，允许重叠，返回 dest 引用。</summary>
    public (double[] Arr, int Offset)? Copy(int dest, int src, int count)
    {
        if (src < 0)
        {
            count += src;
            dest -= src;
            src = 0;
        }
        if (dest < 0)
        {
            count += dest;
            src -= dest;
            dest = 0;
        }

        int resultDest = dest;

        while (count > 0)
        {
            int copyLength = count;
            int maxDstLen = ItemsPerBlock - (dest & (ItemsPerBlock - 1));
            int maxSrcLen = ItemsPerBlock - (src & (ItemsPerBlock - 1));

            if (dest >= MaxIndex || src >= MaxIndex)
            {
                break;
            }
            if (copyLength > maxDstLen)
            {
                copyLength = maxDstLen;
            }
            if (copyLength > maxSrcLen)
            {
                copyLength = maxSrcLen;
            }
            if (copyLength < 1)
            {
                break;
            }

            var srcArr = GetBlock(src / ItemsPerBlock);
            var dstArr = GetBlock(dest / ItemsPerBlock);
            if (srcArr is null || dstArr is null)
            {
                break;
            }

            // Array.Copy 对重叠区域采用 memmove 语义。
            Array.Copy(srcArr, src & (ItemsPerBlock - 1), dstArr, dest & (ItemsPerBlock - 1), copyLength);

            src += copyLength;
            dest += copyLength;
            count -= copyLength;
        }

        return Address(resultDest);
    }

    /// <summary>memset 移植，返回 dest 引用。</summary>
    public (double[] Arr, int Offset)? Set(int dest, double value, int count)
    {
        if (dest < 0)
        {
            count += dest;
            dest = 0;
        }
        if (dest >= MaxIndex)
        {
            return Address(dest);
        }
        if (dest + count > MaxIndex)
        {
            count = MaxIndex - dest;
        }
        if (count < 1)
        {
            return Address(dest);
        }

        int remaining = count;
        int offset = dest;
        while (remaining > 0)
        {
            var block = GetBlock(offset / ItemsPerBlock);
            if (block is null)
            {
                break;
            }
            int blockCount = ItemsPerBlock - (offset & (ItemsPerBlock - 1));
            if (blockCount > remaining)
            {
                blockCount = remaining;
            }
            Array.Fill(block, value, offset & (ItemsPerBlock - 1), blockCount);
            remaining -= blockCount;
            offset += blockCount;
        }

        return Address(dest);
    }
}
