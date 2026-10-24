using MilkDrop.ShaderToy.Audio;
using MilkDrop.ShaderToy.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Audio;

/// <summary>
/// 音频通道纹理（S6-7）：512×2，第 0 行频谱、第 1 行波形，r = 左、g = 右。
/// </summary>
[Collection("GL")]
public sealed class MusicTextureTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Width = MusicTexture.Width;
    private const int Height = MusicTexture.Height;

    [Fact]
    public void WaveformRow_TracksSignalAndSpectrumRow_PeaksAtToneFrequency()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var music = new MusicTexture();

        const float frequency = 440f;
        var frames = 1024;
        var stereo = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            float value = MathF.Sin(2f * MathF.PI * frequency * i / AudioBuffer.SampleRate);
            stereo[i * 2] = value;
            stereo[(i * 2) + 1] = value;
        }

        music.Push(stereo);

        byte[] pixels = GlReadback.Read(fixture, music.Texture, Width, Height);
        Assert.Equal(Width * Height * 4, pixels.Length);

        // 波形行（y = 1）：必须同时出现正负半周（0 对应 128）
        int waveformMin = 255;
        int waveformMax = 0;
        for (var x = 0; x < Width; x++)
        {
            int red = GlReadback.Red(pixels, Width, x, Height - 1);
            waveformMin = Math.Min(waveformMin, red);
            waveformMax = Math.Max(waveformMax, red);
        }

        output.WriteLine($"波形行 min={waveformMin} max={waveformMax}");
        Assert.True(waveformMin < 40, "波形行没有负半周");
        Assert.True(waveformMax > 215, "波形行没有正半周");

        // 频谱行（y = 0）：峰值列应该落在 440 Hz 对应的对数刻度上
        int peakColumn = 0;
        int peakValue = 0;
        for (var x = 0; x < Width; x++)
        {
            int red = GlReadback.Red(pixels, Width, x, 0);
            if (red > peakValue)
            {
                peakValue = red;
                peakColumn = x;
            }
        }

        int expectedColumn = ExpectedLogColumn(frequency);
        output.WriteLine($"频谱峰值列 {peakColumn}（期望 ≈{expectedColumn}），幅度 {peakValue}");
        Assert.InRange(peakColumn, expectedColumn - 12, expectedColumn + 12);
        Assert.True(peakValue > 100, "频谱行几乎全黑");
    }

    [Fact]
    public void EmptyTexture_IsSilent()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var music = new MusicTexture();
        byte[] pixels = GlReadback.Read(fixture, music.Texture, Width, Height);

        // 波形行全在 0 附近（128），频谱行全黑
        Assert.InRange(GlReadback.Red(pixels, Width, 10, 1), 120, 136);
        Assert.InRange(GlReadback.Red(pixels, Width, 10, 0), 0, 8);
    }

    private static int ExpectedLogColumn(float frequency)
    {
        // 与 MusicTexture 内部一致：20 Hz .. 20 kHz 对数分布铺满 512 列
        double fraction = Math.Log(frequency / 20.0) / Math.Log(20000.0 / 20.0);
        return (int)Math.Round(fraction * (Width - 1));
    }
}
