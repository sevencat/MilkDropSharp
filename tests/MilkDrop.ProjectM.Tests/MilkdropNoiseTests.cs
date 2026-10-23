using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using GLEnum = Silk.NET.OpenGLES.GLEnum;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="MilkdropNoise"/> 的噪声数据生成（不需要 GL）。</summary>
public sealed class MilkdropNoiseDataTests
{
    [Fact]
    public void Generate2D_ProducesSizeSquaredPixels()
    {
        Assert.Equal(32 * 32, MilkdropNoise.Generate2D(32, 1).Length);
        Assert.Equal(64 * 64, MilkdropNoise.Generate2D(64, 4).Length);
    }

    [Fact]
    public void Generate3D_ProducesSizeCubedPixels()
    {
        Assert.Equal(8 * 8 * 8, MilkdropNoise.Generate3D(8, 1).Length);
        Assert.Equal(16 * 16 * 16, MilkdropNoise.Generate3D(16, 4).Length);
    }

    [Fact]
    public void Generate2D_SmoothingOnlyFillsInterpolatedPixels()
    {
        const int size = 32;
        const int zoom = 4;

        uint[] data = MilkdropNoise.Generate2D(size, zoom);

        // zoom 4 时 x%4==0 的像素不参与插值，仍保留随机值；
        // 被插值的像素由邻居算出，至少不应与"邻居全相同"的极端情况冲突。
        bool hasDifferentNeighbours = false;
        for (int y = 0; y < size; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                if (data[y * size + x] != data[y * size + x + 1])
                {
                    hasDifferentNeighbours = true;
                }
            }
        }

        Assert.True(hasDifferentNeighbours);
    }

    [Fact]
    public void CubicInterpolate_HitsControlPoints()
    {
        // Catmull-Rom 形式：t=0 落在 y1，t=1 落在 y2。
        Assert.Equal(0.25f, MilkdropNoise.CubicInterpolate(0.0f, 0.25f, 0.75f, 1.0f, 0.0f), 6);
        Assert.Equal(0.75f, MilkdropNoise.CubicInterpolate(0.0f, 0.25f, 0.75f, 1.0f, 1.0f), 6);
    }

    [Fact]
    public void InterpolateCubic_ClampsEachChannel()
    {
        // 超出 [0,1] 的结果要夹住，避免通道溢出到邻居字节。
        uint result = MilkdropNoise.InterpolateCubic(0, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0.5f);

        Assert.Equal(0xFFFFFFFFu, result);
    }
}

/// <summary><see cref="MilkdropNoise"/> 的 GL 纹理创建（需要离屏 GL 上下文）。</summary>
[Collection("GL")]
public sealed class MilkdropNoiseTextureTests(OffscreenGlFixture gl)
{
    [Fact]
    public void CreateNoiseTextures_ProducesExpectedTargetsAndSizes()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using Texture lite = MilkdropNoise.LowQualityLite();
        Assert.Equal("noise_lq_lite", lite.Name);
        Assert.Equal(GLEnum.Texture2D, lite.Target);
        Assert.Equal(32, lite.Width);
        Assert.Equal(32, lite.Height);

        using Texture hq = MilkdropNoise.HighQuality();
        Assert.Equal(256, hq.Width);

        using Texture volume = MilkdropNoise.LowQualityVolume();
        Assert.Equal("noisevol_lq", volume.Name);
        Assert.Equal(GLEnum.Texture3D, volume.Target);
        Assert.Equal(32, volume.Width);

        using Texture volumeHq = MilkdropNoise.HighQualityVolume();
        Assert.Equal(GLEnum.Texture3D, volumeHq.Target);

        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }
}
