using MilkDrop.ProjectM.Audio;
using Xunit;

namespace MilkDrop.ProjectM.Tests.Audio;

/// <summary><see cref="WaveformAligner"/> 的对齐行为。</summary>
/// <remarks>
/// 注意：**首帧会被整体平移**——权重表还没生成时"上一帧"是全零，最优偏移是在找信号最弱的
/// 96 个样本位置。原生 projectM 行为相同（<c>WaveformAligner.cpp:193-206</c> 先
/// <c>GenerateWeights</c> 再 <c>CalculateOffset</c>，而 <c>m_oldWaveformMips</c> 初值为 0），
/// 所以测试里先跑一帧建立基准，再验证后续对齐。
/// </remarks>
public sealed class WaveformAlignerTests
{
    [Fact]
    public void Align_SixOctavesForDefaultBufferSizes()
    {
        // floor(log2(576-480)) = floor(log2(96)) = 6
        Assert.Equal(6, new WaveformAligner().Octaves);
    }

    [Fact]
    public void Align_FirstFrameShiftsAgainstSilence()
    {
        // 记录这个"首帧会动"的既有行为，避免以后有人以为是 bug 去修（修了就和原生对不上了）。
        var aligner = new WaveformAligner();
        var reference = BuildSignal(0);
        var waveform = (float[])reference.Clone();

        aligner.Align(waveform);

        Assert.NotEqual(reference[0], waveform[0]);
    }

    [Fact]
    public void Align_SameWaveformAsPreviousFrameStaysPut()
    {
        var aligner = new WaveformAligner();

        var current = BuildSignal(0);
        aligner.Align(current);

        // 与上一帧完全一致时最优偏移就是 0，波形不应再被移动。
        var previous = (float[])current.Clone();
        aligner.Align(current);

        for (var i = 0; i < AudioConstants.AudioBufferSamples; i++)
        {
            Assert.Equal(previous[i], current[i], 6);
        }
    }

    [Fact]
    public void Align_DelayedWaveformIsShiftedBack()
    {
        var aligner = new WaveformAligner();

        var previousFrame = BuildSignal(0);
        aligner.Align(previousFrame);

        // 新波形比上一帧延迟 5 个样本（前面补 0），对齐后应当被拉回去。
        const int delay = 5;
        var delayed = new float[AudioConstants.AudioBufferSamples];
        for (var i = delay; i < AudioConstants.AudioBufferSamples; i++)
        {
            delayed[i] = previousFrame[i - delay];
        }

        aligner.Align(delayed);

        // 对齐后前 400 个样本应当重新贴近上一帧。
        for (var i = 0; i < 400; i++)
        {
            Assert.True(MathF.Abs(delayed[i] - previousFrame[i]) < 0.02f,
                $"i={i} delayed={delayed[i]:0.####} previousFrame={previousFrame[i]:0.####}");
        }
    }

    [Fact]
    public void Align_RejectsShortWaveform()
    {
        var aligner = new WaveformAligner();
        Assert.Throws<ArgumentException>(() => aligner.Align(new float[AudioConstants.AudioBufferSamples - 1]));
    }

    /// <summary>两路不同频率的正弦叠加，周期与 480 不整除，偏移可唯一判定。</summary>
    private static float[] BuildSignal(int offset)
    {
        var waveform = new float[AudioConstants.AudioBufferSamples];
        for (var i = 0; i < AudioConstants.WaveformSamples; i++)
        {
            var t = i + offset;
            waveform[i] = (0.7f * MathF.Sin(2f * MathF.PI * t / 97f))
                + (0.3f * MathF.Sin(2f * MathF.PI * t / 31f + 0.7f));
        }

        return waveform;
    }
}
