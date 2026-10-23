using MilkDrop.Classic.Visualizer.Sound;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <see cref="Fft"/>（fft.cpp）的数值行为：bin 映射、包络、零填充，
/// 以及 <see cref="SoundAnalyzer.AlignWaves"/> 的零偏移不动性。
/// </summary>
public sealed class SoundAnalyzerTests
{
    /// <summary>
    /// 用朴素 DFT 交叉验证 radix-2 实现：位反转表 / 旋转因子表写错时，
    /// 频点会整体错位或镜像，这个测试会立刻挂掉。
    /// </summary>
    [Fact]
    public void FftMatchesNaiveDft()
    {
        const int samplesIn = 576;      // NUM_WAVEFORM_SAMPLES 之上再补零到 1024
        const int nfreq = 1024;         // NFREQ = NUM_FREQUENCIES * 2
        const int samplesOut = nfreq / 2;

        var wave = new float[samplesIn];
        var rng = 12345u;
        for (var i = 0; i < samplesIn; i++)
        {
            rng = rng * 1664525u + 1013904223u;
            wave[i] = ((rng >> 16) & 0xFF) - 128f;   // 与 int8 输入同量级
        }

        var fft = new Fft(samplesIn, samplesOut, equalize: false);
        var actual = new float[samplesOut];
        fft.TimeToFrequencyDomain(wave, actual);

        // 复现 InitEnvelopeTable(1.0f) + 位反转输入
        var envelope = new double[samplesIn];
        var mult = 1.0 / samplesIn * 6.2831853;
        for (var i = 0; i < samplesIn; i++)
        {
            envelope[i] = 0.5 + 0.5 * Math.Sin(i * mult - 1.5707963268);
        }

        var real = new double[nfreq];
        var imag = new double[nfreq];
        var max = 0.0;
        for (var k = 0; k < samplesOut; k++)
        {
            var re = 0.0;
            var im = 0.0;
            for (var t = 0; t < nfreq; t++)
            {
                var x = t < samplesIn ? wave[t] * envelope[t] : 0.0;
                var theta = -2.0 * Math.PI * k * t / nfreq;
                re += x * Math.Cos(theta);
                im += x * Math.Sin(theta);
            }

            var expected = Math.Sqrt(re * re + im * im);
            max = Math.Max(max, expected);

            // 相对误差（float 实现 vs double 参考）
            Assert.True(
                Math.Abs(expected - actual[k]) <= 1e-4 * Math.Max(1.0, max),
                $"bin {k}: expected {expected}, actual {actual[k]}");
        }
    }

    /// <summary>
    /// 440 Hz 正弦（44.1kHz 采样）应落在 bass 段，且三个频段都有限、非负。
    /// </summary>
    [Fact]
    public void SineToneLandsInBassBand()
    {
        var analyzer = new SoundAnalyzer();
        var wave = new sbyte[SoundAnalysis.WaveformSlots];
        for (var i = 0; i < wave.Length; i++)
        {
            wave[i] = (sbyte)(100 * Math.Sin(2 * Math.PI * 440.0 * i / 44100.0));
        }

        analyzer.ProcessFrame(wave, wave, 60f, 0);
        var imm = analyzer.Sound.Imm[0];

        Assert.True(imm[0] > imm[1], $"bass {imm[0]} 应大于 mid {imm[1]}");
        Assert.True(imm[0] > imm[2], $"bass {imm[0]} 应大于 treb {imm[2]}");
    }

    /// <summary>
    /// 相邻两帧波形完全相同时，对齐偏移应为 0（波形不应被搬动）。
    /// </summary>
    [Fact]
    public void AlignWavesKeepsIdenticalFramesInPlace()
    {
        var analyzer = new SoundAnalyzer();
        var wave = new sbyte[SoundAnalysis.WaveformSlots];
        var expected = new sbyte[SoundAnalysis.WaveformSlots];
        for (var i = 0; i < wave.Length; i++)
        {
            var v = (sbyte)(90 * Math.Sin(2 * Math.PI * 120.0 * i / 44100.0));
            wave[i] = v;
            expected[i] = v;
        }

        analyzer.ProcessFrame(wave, wave, 60f, 0);
        analyzer.ProcessFrame(wave, wave, 60f, 1);

        var actual = analyzer.Sound.Waveform[0];
        for (var i = 0; i < SoundAnalysis.WaveformSamples; i++)
        {
            Assert.Equal((float)expected[i], actual[i]);
        }
    }

    /// <summary>
    /// 稳定输入下 <c>long_avg</c> 应收敛到 <c>imm</c>，于是相对电平（预设里的 bass）回到 1.0。
    /// </summary>
    [Fact]
    public void CustomSoundAnalysisConvergesToUnityRelativeLevel()
    {
        var analyzer = new SoundAnalyzer();
        var wave = new sbyte[SoundAnalysis.WaveformSlots];
        for (var i = 0; i < wave.Length; i++)
        {
            wave[i] = (sbyte)(100 * Math.Sin(2 * Math.PI * 440.0 * i / 44100.0));
        }

        // 走 AnalyzeNewSound + DoCustomSoundAnalysis，绕开 AlignWaves 的平移，
        // 这样每帧输入完全相同，只测时间混合的收敛性。
        // 60fps 下 long_avg 的衰减系数是 0.992^0.5≈0.99598，时间常数约 250 帧，
        // 所以这里得跑够 2000 帧才能把残差压到 1e-3 以下。
        for (var frame = 0; frame < 2000; frame++)
        {
            analyzer.AnalyzeNewSound(wave, wave, 60f);
            analyzer.DoCustomSoundAnalysis(60f, frame);
        }

        var my = analyzer.MySound;
        Assert.True(my.Imm[0] > my.Imm[1], $"bass {my.Imm[0]} 应大于 mid {my.Imm[1]}");
        Assert.InRange(my.ImmRel[0], 0.999f, 1.001f);
        Assert.InRange(my.AvgRel[0], 0.999f, 1.001f);
    }

    /// <summary>
    /// 静音时 <c>long_avg</c> 接近 0，相对电平按原版规则取 1.0（避免除零）。
    /// </summary>
    [Fact]
    public void CustomSoundAnalysisUsesUnityForSilence()
    {
        var analyzer = new SoundAnalyzer();
        var silence = new sbyte[SoundAnalysis.WaveformSlots];

        analyzer.ProcessFrame(silence, silence, 60f, 0);

        Assert.Equal(1.0f, analyzer.MySound.ImmRel[0]);
        Assert.Equal(1.0f, analyzer.MySound.AvgRel[2]);
    }

    /// <summary>静音输入时所有频段必须是 0，且不产生 NaN。</summary>
    [Fact]
    public void SilenceProducesZeroBands()
    {
        var analyzer = new SoundAnalyzer();
        var silence = new sbyte[SoundAnalysis.WaveformSlots];

        analyzer.ProcessFrame(silence, silence, 60f, 0);

        for (var ch = 0; ch < SoundAnalysis.Channels; ch++)
        {
            for (var i = 0; i < SoundAnalysis.Bands; i++)
            {
                Assert.Equal(0f, analyzer.Sound.Imm[ch][i]);
                Assert.False(float.IsNaN(analyzer.Sound.Avg[ch][i]));
            }
        }
    }
}
