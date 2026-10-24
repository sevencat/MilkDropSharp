using Silk.NET.OpenGLES;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// Milkdrop 的噪声纹理生成器（8 位 RGBA 的 2D / 3D 噪声，四个通道各自独立）。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/MilkdropNoise.hpp</c> 与 <c>MilkdropNoise.cpp</c>。</para>
/// <para>
/// 预设里的 <c>noise_lq</c>/<c>noise_lq_lite</c>/<c>noise_mq</c>/<c>noise_hq</c>、
/// <c>noisevol_lq</c>/<c>noisevol_hq</c> 都来自这里，由 <c>TextureManager</c> 预载。
/// 尺寸参数是"随机数据点个数"，zoomFactor 越大插值越平滑（纹理边长始终是 size×zoomFactor 的整数倍关系）。
/// </para>
/// <para>
/// 与 C++ 的差异：随机源用 <see cref="Random"/> 而不是 <c>std::default_random_engine</c>，
/// 噪声内容不同（本来就每次启动随机），分布区间一致。
/// </para>
/// </remarks>
public static class MilkdropNoise
{
    /// <summary>低质量 2D 噪声（256×256，无平滑）。</summary>
    public static Texture LowQuality() => CreateTexture2D("noise_lq", Generate2D(256, 1));

    /// <summary>低质量 2D 噪声（32×32，无平滑）。</summary>
    public static Texture LowQualityLite() => CreateTexture2D("noise_lq_lite", Generate2D(32, 1));

    /// <summary>中质量 2D 噪声（256×256，zoom 4）。</summary>
    public static Texture MediumQuality() => CreateTexture2D("noise_mq", Generate2D(256, 4));

    /// <summary>高质量 2D 噪声（256×256，zoom 8）。</summary>
    public static Texture HighQuality() => CreateTexture2D("noise_hq", Generate2D(256, 8));

    /// <summary>低质量 3D 噪声（32³，无平滑）。</summary>
    public static Texture LowQualityVolume() => CreateTexture3D("noisevol_lq", Generate3D(32, 1));

    /// <summary>高质量 3D 噪声（32³，zoom 4）。</summary>
    public static Texture HighQualityVolume() => CreateTexture3D("noisevol_hq", Generate3D(32, 4));

    /// <summary>GLES 只支持 <c>GL_RGB</c>/<c>GL_RGBA</c>，统一用后者。</summary>
    private static GLEnum GetPreferredInternalFormat() => GLEnum.Rgba;

    /// <summary>Milkdrop 2D 噪声算法（对应 <c>generate2D</c>）。</summary>
    internal static uint[] Generate2D(int size, int zoomFactor)
    {
        var random = new Random();
        var textureData = new uint[size * size];

        int range = zoomFactor > 1 ? 216 : 256;

        for (int y = 0; y < size; y++)
        {
            int row = y * size;

            for (int x = 0; x < size; x++)
            {
                textureData[row + x] =
                    ((uint)(random.Next() % range) + (uint)(range / 2) << 24) |
                    ((uint)(random.Next() % range) + (uint)(range / 2) << 16) |
                    ((uint)(random.Next() % range) + (uint)(range / 2) << 8) |
                    ((uint)(random.Next() % range) + (uint)(range / 2));
            }

            // 随机交换像素，进一步提升"随机感"。
            for (int x = 0; x < size; x++)
            {
                int x1 = random.Next() % size;
                int x2 = random.Next() % size;
                (textureData[row + x1], textureData[row + x2]) = (textureData[row + x2], textureData[row + x1]);
            }
        }

        if (zoomFactor > 1)
        {
            // 先在 X 方向按主行做三次插值。
            for (int y = 0; y < size; y += zoomFactor)
            {
                int baseY = y * size;

                for (int x = 0; x < size; x++)
                {
                    if (x % zoomFactor != 0)
                    {
                        int baseX = x / zoomFactor * zoomFactor + size;
                        uint y0 = textureData[baseY + (baseX - zoomFactor) % size];
                        uint y1 = textureData[baseY + baseX % size];
                        uint y2 = textureData[baseY + (baseX + zoomFactor) % size];
                        uint y3 = textureData[baseY + (baseX + zoomFactor * 2) % size];

                        float t = (float)(x % zoomFactor) / zoomFactor;
                        textureData[baseY + x] = InterpolateCubic(y0, y1, y2, y3, t);
                    }
                }
            }

            // 再在 Y 方向对每一列做三次插值。
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    if (y % zoomFactor != 0)
                    {
                        int baseY = y / zoomFactor * zoomFactor + size;
                        uint y0 = textureData[(baseY - zoomFactor) % size * size + x];
                        uint y1 = textureData[baseY % size * size + x];
                        uint y2 = textureData[(baseY + zoomFactor) % size * size + x];
                        uint y3 = textureData[(baseY + zoomFactor * 2) % size * size + x];

                        float t = (float)(y % zoomFactor) / zoomFactor;
                        textureData[y * size + x] = InterpolateCubic(y0, y1, y2, y3, t);
                    }
                }
            }
        }

        return textureData;
    }

    /// <summary>Milkdrop 3D 噪声算法（对应 <c>generate3D</c>）。</summary>
    internal static uint[] Generate3D(int size, int zoomFactor)
    {
        var random = new Random();
        var textureData = new uint[size * size * size];

        int range = zoomFactor > 1 ? 216 : 256;

        for (int z = 0; z < size; z++)
        {
            for (int y = 0; y < size; y++)
            {
                int row = z * size * size + y * size;

                for (int x = 0; x < size; x++)
                {
                    textureData[row + x] =
                        ((uint)(random.Next() % range) + (uint)(range / 2) << 24) |
                        ((uint)(random.Next() % range) + (uint)(range / 2) << 16) |
                        ((uint)(random.Next() % range) + (uint)(range / 2) << 8) |
                        ((uint)(random.Next() % range) + (uint)(range / 2));
                }

                for (int x = 0; x < size; x++)
                {
                    int x1 = random.Next() % size;
                    int x2 = random.Next() % size;
                    (textureData[row + x1], textureData[row + x2]) = (textureData[row + x2], textureData[row + x1]);
                }
            }
        }

        if (zoomFactor > 1)
        {
            // X 方向插值（只在 z/y 都是 zoomFactor 整数倍的切片上）。
            // 注意 baseY 的写法与 4.1.8 一致：z*size + y*size，而不是 z*size*size + y*size
            // （见 projectMIssue.md I-28）。
            for (int z = 0; z < size; z += zoomFactor)
            {
                for (int y = 0; y < size; y += zoomFactor)
                {
                    for (int x = 0; x < size; x++)
                    {
                        if (x % zoomFactor != 0)
                        {
                            int baseX = x / zoomFactor * zoomFactor + size;
                            int baseY = z * size + y * size;
                            uint y0 = textureData[baseY + (baseX - zoomFactor) % size];
                            uint y1 = textureData[baseY + baseX % size];
                            uint y2 = textureData[baseY + (baseX + zoomFactor) % size];
                            uint y3 = textureData[baseY + (baseX + zoomFactor * 2) % size];

                            float t = (float)(x % zoomFactor) / zoomFactor;
                            textureData[z * size + y * size + x] = InterpolateCubic(y0, y1, y2, y3, t);
                        }
                    }
                }
            }

            // Y 方向插值（在主切片上）。
            for (int z = 0; z < size; z += zoomFactor)
            {
                for (int x = 0; x < size; x++)
                {
                    for (int y = 0; y < size; y++)
                    {
                        if (y % zoomFactor != 0)
                        {
                            int baseY = y / zoomFactor * zoomFactor + size;
                            int baseZ = z * size;
                            uint y0 = textureData[(baseY - zoomFactor) % size * size + baseZ + x];
                            uint y1 = textureData[baseY % size * size + baseZ + x];
                            uint y2 = textureData[(baseY + zoomFactor) % size * size + baseZ + x];
                            uint y3 = textureData[(baseY + zoomFactor * 2) % size * size + baseZ + x];

                            float t = (float)(y % zoomFactor) / zoomFactor;
                            textureData[y * size + baseZ + x] = InterpolateCubic(y0, y1, y2, y3, t);
                        }
                    }
                }
            }

            // Z 方向插值（全部）。
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int z = 0; z < size; z++)
                    {
                        if (z % zoomFactor != 0)
                        {
                            int baseY = y * size;
                            int baseZ = z / zoomFactor * zoomFactor + size;
                            uint y0 = textureData[(baseZ - zoomFactor) % size * size + baseY + x];
                            uint y1 = textureData[baseZ % size * size + baseY + x];
                            uint y2 = textureData[(baseZ + zoomFactor) % size * size + baseY + x];
                            uint y3 = textureData[(baseZ + zoomFactor * 2) % size * size + baseY + x];

                            float t = (float)(z % zoomFactor) / zoomFactor;
                            textureData[z * size + baseY + x] = InterpolateCubic(y0, y1, y2, y3, t);
                        }
                    }
                }
            }
        }

        return textureData;
    }

    /// <summary>对四个 8 位通道分别做三次插值（对应 <c>dwCubicInterpolate</c>）。</summary>
    internal static uint InterpolateCubic(uint y0, uint y1, uint y2, uint y3, float t)
    {
        uint result = 0;

        for (int shift = 0; shift < 32; shift += 8)
        {
            float f = CubicInterpolate(
                ((y0 >> shift) & 0xFF) / 255.0f,
                ((y1 >> shift) & 0xFF) / 255.0f,
                ((y2 >> shift) & 0xFF) / 255.0f,
                ((y3 >> shift) & 0xFF) / 255.0f,
                t);

            f = Math.Clamp(f, 0.0f, 1.0f);
            result |= (uint)(f * 255) << shift;
        }

        return result;
    }

    /// <summary>一维三次插值（对应 <c>fCubicInterpolate</c>）。</summary>
    internal static float CubicInterpolate(float y0, float y1, float y2, float y3, float t)
    {
        float t2 = t * t;
        float a0 = y3 - y2 - y0 + y1;
        float a1 = y0 - y1 - a0;
        float a2 = y2 - y0;
        float a3 = y1;

        return a0 * t * t2 + a1 * t2 + a2 * t + a3;
    }

    private static unsafe Texture CreateTexture2D(string name, uint[] data)
    {
        int size = (int)Math.Sqrt(data.Length);
        var gl = GlContext.Gl;

        uint textureId = gl.GenTexture();
        gl.BindTexture(GLEnum.Texture2D, textureId);

        ReadOnlySpan<byte> bytes = ToBytes(data);
        fixed (byte* pointer = bytes)
        {
            gl.TexImage2D(GLEnum.Texture2D, 0, (int)GetPreferredInternalFormat(), (uint)size, (uint)size, 0,
                GLEnum.Rgba, GLEnum.UnsignedByte, pointer);
        }

        gl.BindTexture(GLEnum.Texture2D, 0);

        return new Texture(name, textureId, GLEnum.Texture2D, size, size, false);
    }

    private static unsafe Texture CreateTexture3D(string name, uint[] data)
    {
        int size = (int)Math.Round(Math.Pow(data.Length, 1.0 / 3.0));
        var gl = GlContext.Gl;

        uint textureId = gl.GenTexture();
        gl.BindTexture(GLEnum.Texture3D, textureId);

        ReadOnlySpan<byte> bytes = ToBytes(data);
        fixed (byte* pointer = bytes)
        {
            gl.TexImage3D(GLEnum.Texture3D, 0, (int)GetPreferredInternalFormat(), (uint)size, (uint)size, (uint)size, 0,
                GLEnum.Rgba, GLEnum.UnsignedByte, pointer);
        }

        gl.BindTexture(GLEnum.Texture3D, 0);

        // 3D 纹理的 C# Texture 只记录 x/y 尺寸，z 尺寸由 GL 对象自己保存。
        return new Texture(name, textureId, GLEnum.Texture3D, size, size, false);
    }

    private static ReadOnlySpan<byte> ToBytes(uint[] data) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan());
}
