namespace MilkDrop.Classic.Visualizer.Noise;

/// <summary>
/// 内置噪声纹理的合成，对应 <c>CPlugin::AddNoiseTex</c>（plugin.cpp:2427）与
/// <c>CPlugin::AddNoiseVol</c>（plugin.cpp:2561）。
/// </summary>
/// <remarks>
/// <para>算法照抄原版：先逐行填随机色，再在行内随机交换像素，最后用三次插值做平滑。</para>
/// <para>
/// <c>zoomFactor</c> 决定"特征尺度"：1 = 纯随机；2 = 线性平滑；4/8/16 = 三次插值（越大越糊）。
/// 原版用 <c>QueryPerformanceCounter</c> 混种求随机，这里改成显式 <c>seed</c>，
/// 保证同一预设两次启动的画面一致（也方便测试）。
/// </para>
/// <para>
/// 颜色值的溢出行为（<c>range/2 + rand()%range</c> 在 range=216 时会超过 255）与 C 版一致，
/// 不要"顺手修正"成 clamp —— 那会改变画面。
/// </para>
/// </remarks>
public static class NoiseTexture
{
    /// <summary>合成 2D 噪声（<c>size</c>×<c>size</c>），返回 <c>0xAARRGGBB</c> 像素。</summary>
    public static uint[] Create2D(int size, int zoomFactor, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(zoomFactor, 1);

        var random = new Random(seed);
        var pixels = new uint[size * size];
        var range = zoomFactor > 1 ? 216 : 256;

        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                pixels[row + x] = RandomColor(random, range);
            }

            ShuffleRow(pixels, row, size, random);
        }

        if (zoomFactor > 1)
        {
            Smooth2DHorizontal(pixels, size, zoomFactor);
            Smooth2DVertical(pixels, size, zoomFactor);
        }

        return pixels;
    }

    /// <summary>合成 3D 噪声（<c>size</c>³），返回按 z/y/x 排的 <c>0xAARRGGBB</c> 体素。</summary>
    public static uint[] CreateVolume(int size, int zoomFactor, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(zoomFactor, 1);

        var random = new Random(seed);
        var voxels = new uint[size * size * size];
        var range = zoomFactor > 1 ? 216 : 256;

        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                var row = ((z * size) + y) * size;
                for (var x = 0; x < size; x++)
                {
                    voxels[row + x] = RandomColor(random, range);
                }

                ShuffleRow(voxels, row, size, random);
            }
        }

        if (zoomFactor > 1)
        {
            SmoothVolumeX(voxels, size, zoomFactor);
            SmoothVolumeY(voxels, size, zoomFactor);
            SmoothVolumeZ(voxels, size, zoomFactor);
        }

        return voxels;
    }

    /// <summary>"随机值 + range/2"，刻意保留 C 版可能溢出的行为（见类型注释）。</summary>
    private static uint RandomColor(Random random, int range)
    {
        var half = range / 2;
        var a = (uint)(random.Next(range) + half);
        var r = (uint)(random.Next(range) + half);
        var g = (uint)(random.Next(range) + half);
        var b = (uint)(random.Next(range) + half);

        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>行内随机交换像素（提升"随机感"，plugin.cpp:2488-2495）。</summary>
    private static void ShuffleRow(uint[] pixels, int row, int size, Random random)
    {
        for (var x = 0; x < size; x++)
        {
            var a = row + random.Next(size);
            var b = row + random.Next(size);
            (pixels[a], pixels[b]) = (pixels[b], pixels[a]);
        }
    }

    private static void Smooth2DHorizontal(uint[] pixels, int size, int zoom)
    {
        for (var y = 0; y < size; y += zoom)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                if (x % zoom == 0)
                {
                    continue;
                }

                var baseX = ((x / zoom) * zoom) + size;
                var y0 = pixels[row + Mod(baseX - zoom, size)];
                var y1 = pixels[row + Mod(baseX, size)];
                var y2 = pixels[row + Mod(baseX + zoom, size)];
                var y3 = pixels[row + Mod(baseX + (zoom * 2), size)];

                pixels[row + x] = CubicInterpolate(y0, y1, y2, y3, (x % zoom) / (float)zoom);
            }
        }
    }

    private static void Smooth2DVertical(uint[] pixels, int size, int zoom)
    {
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                if (y % zoom == 0)
                {
                    continue;
                }

                var baseY = ((y / zoom) * zoom) + size;
                var y0 = pixels[(Mod(baseY - zoom, size) * size) + x];
                var y1 = pixels[(Mod(baseY, size) * size) + x];
                var y2 = pixels[(Mod(baseY + zoom, size) * size) + x];
                var y3 = pixels[(Mod(baseY + (zoom * 2), size) * size) + x];

                pixels[(y * size) + x] = CubicInterpolate(y0, y1, y2, y3, (y % zoom) / (float)zoom);
            }
        }
    }

    private static void SmoothVolumeX(uint[] voxels, int size, int zoom)
    {
        var slice = size * size;

        for (var z = 0; z < size; z += zoom)
        {
            for (var y = 0; y < size; y += zoom)
            {
                var row = (z * slice) + (y * size);

                for (var x = 0; x < size; x++)
                {
                    if (x % zoom == 0)
                    {
                        continue;
                    }

                    var baseX = ((x / zoom) * zoom) + size;
                    var y0 = voxels[row + Mod(baseX - zoom, size)];
                    var y1 = voxels[row + Mod(baseX, size)];
                    var y2 = voxels[row + Mod(baseX + zoom, size)];
                    var y3 = voxels[row + Mod(baseX + (zoom * 2), size)];

                    voxels[row + x] = CubicInterpolate(y0, y1, y2, y3, (x % zoom) / (float)zoom);
                }
            }
        }
    }

    private static void SmoothVolumeY(uint[] voxels, int size, int zoom)
    {
        var slice = size * size;

        for (var z = 0; z < size; z += zoom)
        {
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    if (y % zoom == 0)
                    {
                        continue;
                    }

                    var baseY = ((y / zoom) * zoom) + size;
                    var baseZ = z * slice;
                    var y0 = voxels[(Mod(baseY - zoom, size) * size) + baseZ + x];
                    var y1 = voxels[(Mod(baseY, size) * size) + baseZ + x];
                    var y2 = voxels[(Mod(baseY + zoom, size) * size) + baseZ + x];
                    var y3 = voxels[(Mod(baseY + (zoom * 2), size) * size) + baseZ + x];

                    voxels[(y * size) + baseZ + x] = CubicInterpolate(y0, y1, y2, y3, (y % zoom) / (float)zoom);
                }
            }
        }
    }

    private static void SmoothVolumeZ(uint[] voxels, int size, int zoom)
    {
        var slice = size * size;

        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                var baseY = y * size;

                for (var z = 0; z < size; z++)
                {
                    if (z % zoom == 0)
                    {
                        continue;
                    }

                    var baseZ = ((z / zoom) * zoom) + size;
                    var y0 = voxels[(Mod(baseZ - zoom, size) * slice) + baseY + x];
                    var y1 = voxels[(Mod(baseZ, size) * slice) + baseY + x];
                    var y2 = voxels[(Mod(baseZ + zoom, size) * slice) + baseY + x];
                    var y3 = voxels[(Mod(baseZ + (zoom * 2), size) * slice) + baseY + x];

                    voxels[(z * slice) + baseY + x] = CubicInterpolate(y0, y1, y2, y3, (z % zoom) / (float)zoom);
                }
            }
        }
    }

    /// <summary><c>dwCubicInterpolate</c>（plugin.cpp:2403）：对 D3DCOLOR 的四个通道分别插值。</summary>
    private static uint CubicInterpolate(uint y0, uint y1, uint y2, uint y3, float t)
    {
        var result = 0u;

        for (var channel = 0; channel < 4; channel++)
        {
            var shift = channel * 8;
            var value = Cubic(
                ((y0 >> shift) & 0xFFu) / 255f,
                ((y1 >> shift) & 0xFFu) / 255f,
                ((y2 >> shift) & 0xFFu) / 255f,
                ((y3 >> shift) & 0xFFu) / 255f,
                t);

            value = Math.Clamp(value, 0f, 1f);
            result |= (uint)(value * 255f) << shift;
        }

        return result;
    }

    /// <summary><c>fCubicInterpolate</c>（plugin.cpp:2390）。</summary>
    private static float Cubic(float y0, float y1, float y2, float y3, float t)
    {
        var t2 = t * t;
        var a0 = y3 - y2 - y0 + y1;
        var a1 = y0 - y1 - a0;
        var a2 = y2 - y0;
        var a3 = y1;

        return (a0 * t * t2) + (a1 * t2) + (a2 * t) + a3;
    }

    /// <summary>C 的 <c>%</c> 语义（操作数可能为负时仍返回非负）。</summary>
    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
