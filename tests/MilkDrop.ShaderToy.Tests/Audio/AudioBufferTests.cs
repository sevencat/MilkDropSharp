using MilkDrop.ShaderToy.Audio;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Audio;

/// <summary>
/// 环形缓冲与重采样（S6-4）：采样率不同要保音高，缓冲不够要填静音并记账。
/// </summary>
public sealed class AudioBufferTests(ITestOutputHelper output)
{
    [Fact]
    public void Read_SameSampleRate_PassesSamplesThrough()
    {
        var buffer = new AudioBuffer(1024);
        var source = new float[200 * 2];
        for (var i = 0; i < 200; i++)
        {
            source[i * 2] = 0.5f;
            source[(i * 2) + 1] = -0.5f;
        }

        buffer.Write(source);

        var destination = new float[100 * 2];
        buffer.Read(destination, 100, AudioBuffer.SampleRate);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0.5f, destination[i * 2], 4);
            Assert.Equal(-0.5f, destination[(i * 2) + 1], 4);
        }

        Assert.Equal(100, buffer.AvailableFrames);
        Assert.Equal(0, buffer.UnderrunFrames);
    }

    [Fact]
    public void Read_HalfDeviceRate_ConsumesTwoSourceFramesPerOutput()
    {
        var buffer = new AudioBuffer(4096);

        // 一段 0 → 1 的斜坡，方便检查插值
        var source = new float[1000 * 2];
        for (var i = 0; i < 1000; i++)
        {
            source[i * 2] = i / 1000f;
            source[(i * 2) + 1] = i / 1000f;
        }

        buffer.Write(source);

        var destination = new float[100 * 2];
        buffer.Read(destination, 100, AudioBuffer.SampleRate / 2);

        // 22050 Hz 下，100 个输出帧 ≈ 200 个源帧
        Assert.InRange(1000 - buffer.AvailableFrames, 198, 202);

        // 音高保持：第 50 个输出帧 ≈ 源的第 100 帧
        Assert.InRange(destination[50 * 2], 0.096f, 0.106f);
        output.WriteLine($"剩余 {buffer.AvailableFrames} 帧，欠载 {buffer.UnderrunFrames}");
    }

    [Fact]
    public void Read_EmptyBuffer_FillsSilenceAndCountsUnderrun()
    {
        var buffer = new AudioBuffer(256);
        var destination = new float[64 * 2];

        buffer.Read(destination, 64, AudioBuffer.SampleRate);

        Assert.All(destination, sample => Assert.Equal(0f, sample));
        Assert.Equal(64, buffer.UnderrunFrames);
    }

    [Fact]
    public void Write_OverCapacity_DropsOldestFrames()
    {
        var buffer = new AudioBuffer(100);

        // 值必须在 [-1,1]：缓冲会限幅，用 0..199 会被夹成 1
        var source = new float[200 * 2];
        for (var i = 0; i < 200; i++)
        {
            source[i * 2] = i / 200f;
            source[(i * 2) + 1] = i / 200f;
        }

        buffer.Write(source);
        Assert.Equal(100, buffer.AvailableFrames);

        var destination = new float[100 * 2];
        buffer.Read(destination, 100, AudioBuffer.SampleRate);

        // 丢掉最旧的 100 帧，剩下的从 100 开始
        Assert.Equal(100 / 200f, destination[0], 3);
        Assert.Equal(199 / 200f, destination[99 * 2], 3);
    }

    [Fact]
    public void Write_ClampsOutOfRangeSamples()
    {
        var buffer = new AudioBuffer(16);
        buffer.Write([4f, -4f, float.NaN, 0.25f]);

        var destination = new float[2 * 2];
        buffer.Read(destination, 2, AudioBuffer.SampleRate);

        Assert.Equal(1f, destination[0]);
        Assert.Equal(-1f, destination[1]);
        Assert.Equal(0f, destination[2]);       // NaN → 0，不能污染下游
        Assert.Equal(0.25f, destination[3]);
    }
}
