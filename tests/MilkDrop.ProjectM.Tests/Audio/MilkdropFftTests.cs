using MilkDrop.ProjectM.Audio;
using Xunit;

namespace MilkDrop.ProjectM.Tests.Audio;

/// <summary>
/// <see cref="MilkdropFft"/> 的数值验证。
/// </summary>
/// <remarks>
/// 关掉包络与均衡（<c>envelopePower:-1</c>、<c>equalize:false</c>）后，输出应当等于
/// 输入（480 点，补零到 1024）的常规 DFT 幅度，用这份独立实现在 double 精度下对照。
/// </remarks>
public sealed class MilkdropFftTests
{
    private const int SamplesIn = AudioConstants.WaveformSamples;
    private const int SamplesOut = AudioConstants.SpectrumSamples;

    [Fact]
    public void TimeToFrequencyDomain_MatchesNaiveDft()
    {
        var fft = new MilkdropFft(SamplesIn, SamplesOut, equalize: false, envelopePower: -1f);
        Assert.Equal(SamplesOut * 2, fft.NumFrequencies);
        Assert.Equal(SamplesOut, fft.NumOutputBins);

        var waveform = BuildWaveform(SamplesIn);
        var spectrum = new float[fft.NumOutputBins];

        Assert.True(fft.TimeToFrequencyDomain(waveform, spectrum));

        var reference = NaiveDftMagnitudes(waveform, SamplesOut * 2, fft.NumOutputBins);
        var max = 0.0;
        foreach (var value in reference)
        {
            max = Math.Max(max, value);
        }

        var tolerance = max * 1e-3;

        for (var bin = 0; bin < fft.NumOutputBins; bin++)
        {
            Assert.True(Math.Abs(spectrum[bin] - reference[bin]) <= tolerance,
                $"bin {bin}: fft={spectrum[bin]:0.######} reference={reference[bin]:0.######} tolerance={tolerance:0.######}");
        }
    }

    [Fact]
    public void TimeToFrequencyDomain_EqualizeZeroesDcBin()
    {
        // 均衡表第 0 项是 scaling * log(1) = 0，所以纯直流输入的能量会被完全压掉。
        var waveform = new float[SamplesIn];
        Array.Fill(waveform, 1f);

        var equalized = new MilkdropFft(SamplesIn, SamplesOut, equalize: true, envelopePower: -1f);
        var equalizedSpectrum = new float[equalized.NumOutputBins];
        Assert.True(equalized.TimeToFrequencyDomain(waveform, equalizedSpectrum));
        Assert.Equal(0f, equalizedSpectrum[0], 6);

        // 关掉均衡时，1024 点 FFT 里前 480 点全 1 的直流分量正好是 480。
        var raw = new MilkdropFft(SamplesIn, SamplesOut, equalize: false, envelopePower: -1f);
        var rawSpectrum = new float[raw.NumOutputBins];
        Assert.True(raw.TimeToFrequencyDomain(waveform, rawSpectrum));
        Assert.Equal(SamplesIn, rawSpectrum[0], 3);
    }

    [Fact]
    public void TimeToFrequencyDomain_PeaksAtInputFrequency()
    {
        // 480 点里放 20 个完整周期的正弦（周期 24 点），1024 点 FFT 的峰值应落在 bin 20 附近。
        var fft = new MilkdropFft(SamplesIn, SamplesOut, equalize: false, envelopePower: -1f);

        var waveform = new float[SamplesIn];
        for (var i = 0; i < SamplesIn; i++)
        {
            waveform[i] = MathF.Sin(2f * MathF.PI * 20f * i / SamplesIn);
        }

        var spectrum = new float[fft.NumOutputBins];
        Assert.True(fft.TimeToFrequencyDomain(waveform, spectrum));

        var peakBin = 0;
        for (var bin = 1; bin < fft.NumOutputBins; bin++)
        {
            if (spectrum[bin] > spectrum[peakBin])
            {
                peakBin = bin;
            }
        }

        // 20 个周期 / 480 点 = 每点 1/24 周期；1024 点 FFT 对应 bin = 1024/24 ≈ 42.67。
        // 输入不是整数个周期于 480 点时能量会分散，这里只要求峰值在这个理论位置附近。
        Assert.InRange(peakBin, 40, 43);
    }

    [Fact]
    public void TimeToFrequencyDomain_RejectsShortInput()
    {
        var fft = new MilkdropFft(SamplesIn, SamplesOut);

        Assert.False(fft.TimeToFrequencyDomain(new float[SamplesIn - 1], new float[fft.NumOutputBins]));
        Assert.False(fft.TimeToFrequencyDomain(new float[SamplesIn], new float[fft.NumOutputBins - 1]));
    }

    [Fact]
    public void Constructor_RejectsNonPowerOfTwoOutput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MilkdropFft(480, 500));
    }

    /// <summary>可重复的多频混合信号。</summary>
    private static float[] BuildWaveform(int count)
    {
        var waveform = new float[count];
        for (var i = 0; i < count; i++)
        {
            waveform[i] = (0.6f * MathF.Sin(2f * MathF.PI * 7f * i / count))
                + (0.3f * MathF.Sin(2f * MathF.PI * 31f * i / count))
                + (0.1f * MathF.Cos(2f * MathF.PI * 97f * i / count));
        }

        return waveform;
    }

    /// <summary>朴素 DFT（double 精度），作为对照实现。</summary>
    private static double[] NaiveDftMagnitudes(ReadOnlySpan<float> waveform, int dftSize, int bins)
    {
        var result = new double[bins];

        for (var bin = 0; bin < bins; bin++)
        {
            double real = 0;
            double imag = 0;

            for (var n = 0; n < waveform.Length; n++)
            {
                var angle = -2.0 * Math.PI * bin * n / dftSize;
                real += waveform[n] * Math.Cos(angle);
                imag += waveform[n] * Math.Sin(angle);
            }

            result[bin] = Math.Sqrt((real * real) + (imag * imag));
        }

        return result;
    }
}
