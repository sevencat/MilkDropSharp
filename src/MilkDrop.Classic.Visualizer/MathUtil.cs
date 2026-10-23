namespace MilkDrop.Classic.Visualizer;

/// <summary>对应 C 版 <c>utility.cpp</c> 里移植必需的小工具。</summary>
public static class MathUtil
{
    public const float Pi = 3.14159265358979323846f;

    /// <summary>把正方形区域压缩到中心（<c>utility.cpp SquishToCenter</c>）。</summary>
    public static float SquishToCenter(float x, float fExp)
    {
        if (x > 0.5f)
        {
            return (float)Math.Pow(x * 2f - 1f, fExp) * 0.5f + 0.5f;
        }

        return 1f - ((float)Math.Pow(1f - x * 2f, fExp) * 0.5f + 0.5f);
    }

    /// <summary><c>utility.cpp TrimToPow2</c>：把纹理尺寸裁到 2 的幂。</summary>
    public static int TrimToPow2(int n)
    {
        if (n <= 1)
        {
            return 1;
        }

        var rval = 1;
        while (rval * 2 <= n)
        {
            rval *= 2;
        }

        return rval;
    }

    /// <summary><c>utility.cpp GetNearestPow2Size</c>：取最近的 2 的幂。</summary>
    public static int GetNearestPow2Size(int n, int scale)
    {
        var x = (int)(Math.Log(n * scale) / Math.Log(2.0) + 0.5);
        return Math.Max(2, 1 << x);
    }

    public static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>角度归一化到 [-π, π]（对应 <c>milkdropfs.cpp</c> 里的接缝处理）。</summary>
    public static float WrapAngle(float angle)
    {
        const float twoPi = 2f * Pi;
        angle %= twoPi;
        if (angle > Pi)
        {
            angle -= twoPi;
        }
        else if (angle < -Pi)
        {
            angle += twoPi;
        }

        return angle;
    }
}
