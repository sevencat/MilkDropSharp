using MilkDrop.Classic.Visualizer.Noise;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 内置噪点/噪声体纹理的合成（<c>AddNoiseTex</c> / <c>AddNoiseVol</c>）。
/// </summary>
public sealed class NoiseTextureTests
{
    [Fact]
    public void Create2DReturnsOnePixelPerTexel()
    {
        var pixels = NoiseTexture.Create2D(32, 4, seed: 1);
        Assert.Equal(32 * 32, pixels.Length);
    }

    [Fact]
    public void CreateVolumeReturnsOneVoxelPerTexel()
    {
        var voxels = NoiseTexture.CreateVolume(8, 4, seed: 1);
        Assert.Equal(8 * 8 * 8, voxels.Length);
    }

    [Fact]
    public void SameSeedIsReproducible()
    {
        var a = NoiseTexture.Create2D(16, 4, seed: 7);
        var b = NoiseTexture.Create2D(16, 4, seed: 7);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeedsDiffer()
    {
        var a = NoiseTexture.CreateVolume(8, 1, seed: 1);
        var b = NoiseTexture.CreateVolume(8, 1, seed: 2);

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void HigherZoomIsSmoother(int zoom)
    {
        var raw = NoiseTexture.Create2D(64, 1, seed: 3);
        var smooth = NoiseTexture.Create2D(64, zoom, seed: 3);

        Assert.True(
            MeanHorizontalDelta(smooth, 64) < MeanHorizontalDelta(raw, 64) * 0.75f,
            $"zoom={zoom} 的相邻像素差没有明显变小");
    }

    [Fact]
    public void VolumeSmoothingAlsoWorks()
    {
        var raw = NoiseTexture.CreateVolume(16, 1, seed: 4);
        var smooth = NoiseTexture.CreateVolume(16, 4, seed: 4);

        Assert.True(MeanHorizontalDelta(smooth, 16 * 16) < MeanHorizontalDelta(raw, 16 * 16) * 0.75f);
    }

    /// <summary>按行统计相邻像素的亮度平均绝对差（行宽 <paramref name="rowLength"/>）。</summary>
    private static float MeanHorizontalDelta(uint[] pixels, int rowLength)
    {
        long total = 0;
        var count = 0;

        for (var row = 0; row < pixels.Length; row += rowLength)
        {
            for (var x = 0; x < rowLength - 1; x++)
            {
                total += Math.Abs(Luminance(pixels[row + x]) - Luminance(pixels[row + x + 1]));
                count++;
            }
        }

        return count == 0 ? 0f : total / (float)count;
    }

    private static int Luminance(uint argb)
    {
        var r = (int)((argb >> 16) & 0xFF);
        var g = (int)((argb >> 8) & 0xFF);
        var b = (int)(argb & 0xFF);
        return (r * 32) + (g * 49) + (b * 29);
    }
}
