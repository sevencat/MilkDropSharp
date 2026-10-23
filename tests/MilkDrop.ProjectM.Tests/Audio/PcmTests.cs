using MilkDrop.ProjectM.Audio;
using Xunit;

namespace MilkDrop.ProjectM.Tests.Audio;

/// <summary><see cref="Pcm"/> 的输入归一化与每帧数据组装。</summary>
public sealed class PcmTests
{
    private const double FrameSeconds = 1.0 / 60.0;

    [Fact]
    public void Add_FloatIsScaledToPm128()
    {
        var pcm = new Pcm();
        var samples = new float[AudioConstants.AudioBufferSamples];
        Array.Fill(samples, 0.5f);

        pcm.Add(samples, channels: 1, sampleCount: samples.Length);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        var data = pcm.GetFrameAudioData();
        for (var i = 0; i < AudioConstants.WaveformSamples; i++)
        {
            Assert.Equal(64f, data.WaveformLeft[i], 4);
            Assert.Equal(64f, data.WaveformRight[i], 4);
        }
    }

    [Fact]
    public void Add_UnsignedByteIsCenteredAndScaled()
    {
        var pcm = new Pcm();
        var samples = new byte[AudioConstants.AudioBufferSamples];
        Array.Fill(samples, (byte)255);

        pcm.Add(samples, channels: 1, sampleCount: samples.Length);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        var data = pcm.GetFrameAudioData();
        Assert.Equal(127f, data.WaveformLeft[0], 4);

        // 128 是 8 位无符号的静音中点。
        var silence = new Pcm();
        var zeros = new byte[AudioConstants.AudioBufferSamples];
        Array.Fill(zeros, (byte)128);
        silence.Add(zeros, channels: 1, sampleCount: zeros.Length);
        silence.UpdateFrameAudioData(FrameSeconds, 0);
        Assert.Equal(0f, silence.GetFrameAudioData().WaveformLeft[0], 4);
    }

    [Fact]
    public void Add_SignedShortIsScaledToPm128()
    {
        var pcm = new Pcm();
        var samples = new short[AudioConstants.AudioBufferSamples];
        Array.Fill(samples, (short)16384);

        pcm.Add(samples, channels: 1, sampleCount: samples.Length);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        Assert.Equal(64f, pcm.GetFrameAudioData().WaveformLeft[0], 4);
    }

    [Fact]
    public void Add_SplitsStereoChannels()
    {
        var pcm = new Pcm();
        const int frames = AudioConstants.AudioBufferSamples;
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            interleaved[i * 2] = 0.25f;
            interleaved[(i * 2) + 1] = -0.25f;
        }

        pcm.Add(interleaved, channels: 2, sampleCount: frames);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        var data = pcm.GetFrameAudioData();
        Assert.Equal(32f, data.WaveformLeft[0], 4);
        Assert.Equal(-32f, data.WaveformRight[0], 4);
    }

    [Fact]
    public void Add_MonoDuplicatesIntoRightChannel()
    {
        var pcm = new Pcm();
        var samples = new float[AudioConstants.AudioBufferSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i / (float)samples.Length * 2f - 1f;
        }

        pcm.Add(samples, channels: 1, sampleCount: samples.Length);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        var data = pcm.GetFrameAudioData();
        for (var i = 0; i < AudioConstants.WaveformSamples; i++)
        {
            Assert.Equal(data.WaveformLeft[i], data.WaveformRight[i], 4);
        }
    }

    [Fact]
    public void UpdateFrameAudioData_LeftOnlySignalKeepsRightSpectrumFlat()
    {
        var pcm = new Pcm();
        const int frames = AudioConstants.AudioBufferSamples;
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            interleaved[i * 2] = MathF.Sin(2f * MathF.PI * 30f * i / frames);
            interleaved[(i * 2) + 1] = 0f;
        }

        pcm.Add(interleaved, channels: 2, sampleCount: frames);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        var data = pcm.GetFrameAudioData();

        var leftEnergy = data.SpectrumLeft.Sum(MathF.Abs);
        var rightEnergy = data.SpectrumRight.Sum(MathF.Abs);

        Assert.True(leftEnergy > 10f, $"左声道频谱能量过低: {leftEnergy}");
        Assert.True(rightEnergy < 1e-3f, $"右声道应当无能量: {rightEnergy}");
    }

    [Fact]
    public void GetFrameAudioData_ProducesAveragedBands()
    {
        // 全静音时长期平均为 0，三个频段的相对值都是 1；
        // 平均值按原生写死的 0.333f 加权，所以是 0.999 而不是 1。
        var pcm = new Pcm();
        pcm.Add(new float[AudioConstants.AudioBufferSamples], channels: 1);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        var data = pcm.GetFrameAudioData();
        Assert.Equal(0.999f, data.Vol, 4);
        Assert.Equal(0.999f, data.VolAtt, 4);
        Assert.Equal(1f, data.Bass, 4);
        Assert.Equal(1f, data.Mid, 4);
        Assert.Equal(1f, data.Treb, 4);
    }

    [Fact]
    public void Add_IgnoresEmptyInput()
    {
        var pcm = new Pcm();
        pcm.Add(ReadOnlySpan<float>.Empty, channels: 2, sampleCount: 0);
        pcm.UpdateFrameAudioData(FrameSeconds, 0);

        Assert.All(pcm.GetFrameAudioData().WaveformLeft, value => Assert.Equal(0f, value));
    }
}
