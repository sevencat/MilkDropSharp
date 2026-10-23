using MilkDrop.Classic.Visualizer.Audio;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>audiobuf.cpp GetAudioBuf</c> 的两个语义：读出顺序是最老→最新、
/// 同一批数据只能被消费一次。写错会让 FFT 每帧都吃同一段波形。
/// </summary>
public sealed class AudioRingBufferTests
{
    private const int Frames = AudioRingBuffer.SampleCount;

    /// <summary>第 i 帧：左 = i%100，右 = -(i%100)（都能精确落进 int8 网格）。</summary>
    private static float[] Interleaved(int count)
    {
        var data = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            data[i * 2] = (i % 100) / 128f;
            data[(i * 2) + 1] = -(i % 100) / 128f;
        }

        return data;
    }

    [Fact]
    public void ReadsOldestToNewest()
    {
        var buffer = new AudioRingBuffer();
        buffer.Write(Interleaved(Frames));

        var left = new sbyte[Frames];
        var right = new sbyte[Frames];

        Assert.True(buffer.ReadAudioBuf(left, right));

        for (var i = 0; i < Frames; i++)
        {
            Assert.Equal((sbyte)(i % 100), left[i]);
            Assert.Equal((sbyte)-(i % 100), right[i]);
        }
    }

    [Fact]
    public void KeepsOnlyNewestWindowWhenOverwritten()
    {
        const int written = 1200;
        var buffer = new AudioRingBuffer();
        buffer.Write(Interleaved(written));

        var left = new sbyte[Frames];
        var right = new sbyte[Frames];

        Assert.True(buffer.ReadAudioBuf(left, right));

        var firstVisible = written - Frames;
        for (var i = 0; i < Frames; i++)
        {
            Assert.Equal((sbyte)((firstVisible + i) % 100), left[i]);
        }
    }

    [Fact]
    public void SecondReadWithoutNewDataIsSilence()
    {
        var buffer = new AudioRingBuffer();
        buffer.Write(Interleaved(Frames));

        var left = new sbyte[Frames];
        var right = new sbyte[Frames];

        Assert.True(buffer.ReadAudioBuf(left, right));

        // 原版 pcmBufDrained：没写新数据之前，再读就只有静音
        Assert.False(buffer.ReadAudioBuf(left, right));
        Assert.All(left, s => Assert.Equal(0, s));
        Assert.All(right, s => Assert.Equal(0, s));
    }

    [Fact]
    public void UnderrunBeforeFullWindowIsSilence()
    {
        var buffer = new AudioRingBuffer();
        buffer.Write(Interleaved(Frames - 1));

        var left = new sbyte[Frames];
        var right = new sbyte[Frames];

        Assert.False(buffer.ReadAudioBuf(left, right));
        Assert.All(left, s => Assert.Equal(0, s));
    }
}
