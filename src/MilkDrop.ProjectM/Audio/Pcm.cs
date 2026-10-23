using System.Numerics;

namespace MilkDrop.ProjectM.Audio;

/// <summary>
/// 外部 PCM 输入 → 波形/频谱/节拍强度。
/// </summary>
/// <remarks>
/// <para>对应 <c>Audio/PCM.hpp:26-115</c> 与 <c>PCM.cpp:6-119</c>。</para>
/// <para>
/// 输入先写进 576 样本的环形缓冲（左/右各一份），每帧 <see cref="UpdateFrameAudioData"/> 时：
/// 读出环形缓冲的最新窗口 → FFT → 波形对齐 → 三频段响度。
/// </para>
/// <para>
/// C++ 用 <c>std::atomic&lt;size_t&gt;</c> 记录环形缓冲写指针（音频线程写、渲染线程读）。
/// 这里用一把小锁保护环形缓冲与写指针，允许宿主在音频回调线程里 <see cref="Add(ReadOnlySpan{float}, int, int)"/>、
/// 在渲染线程里 <see cref="UpdateFrameAudioData"/>。
/// </para>
/// </remarks>
public sealed class Pcm
{
    /// <summary>环形输入缓冲（左）。</summary>
    private readonly float[] _inputBufferL = new float[AudioConstants.AudioBufferSamples];

    /// <summary>环形输入缓冲（右）。</summary>
    private readonly float[] _inputBufferR = new float[AudioConstants.AudioBufferSamples];

    /// <summary>环形缓冲写指针。</summary>
    private int _start;

    /// <summary>保护环形缓冲与写指针（音频线程写、渲染线程读）。</summary>
    private readonly object _sync = new();

    private readonly float[] _waveformL = new float[AudioConstants.AudioBufferSamples];
    private readonly float[] _waveformR = new float[AudioConstants.AudioBufferSamples];

    private readonly float[] _spectrumL = new float[AudioConstants.SpectrumSamples];
    private readonly float[] _spectrumR = new float[AudioConstants.SpectrumSamples];

    private readonly MilkdropFft _fft = new(AudioConstants.WaveformSamples, AudioConstants.SpectrumSamples, true);
    private readonly WaveformAligner _alignL = new();
    private readonly WaveformAligner _alignR = new();

    private readonly Loudness _bass = new(LoudnessBand.Bass);
    private readonly Loudness _middles = new(LoudnessBand.Middles);
    private readonly Loudness _treble = new(LoudnessBand.Treble);

    /// <summary>FFT 输入的工作缓冲（C++ 每次调用新建 vector，这里复用）。</summary>
    private readonly float[] _fftInput = new float[AudioConstants.AudioBufferSamples];

    /// <summary>加入交错浮点 PCM（范围 -1..1）。</summary>
    /// <param name="samples">交错样本，左声道在偏移 0、右声道在偏移 1，其余声道忽略。</param>
    /// <param name="channels">声道数。</param>
    /// <param name="sampleCount">样本帧数（不是数组长度）。</param>
    public void Add(ReadOnlySpan<float> samples, int channels, int sampleCount)
        => AddToBuffer(samples, channels, sampleCount, 1f, 0f);

    /// <summary>加入交错浮点 PCM，<paramref name="sampleCount"/> 取 <c>samples.Length / channels</c>。</summary>
    public void Add(ReadOnlySpan<float> samples, int channels)
        => Add(samples, channels, channels > 0 ? samples.Length / channels : 0);

    /// <summary>加入交错无符号 8 位 PCM（0..255，128 为静音）。</summary>
    public void Add(ReadOnlySpan<byte> samples, int channels, int sampleCount)
        => AddToBuffer(samples, channels, sampleCount, 128f, 128f);

    /// <inheritdoc cref="Add(ReadOnlySpan{byte}, int, int)"/>
    public void Add(ReadOnlySpan<byte> samples, int channels)
        => Add(samples, channels, channels > 0 ? samples.Length / channels : 0);

    /// <summary>加入交错有符号 16 位 PCM（-32768..32767）。</summary>
    public void Add(ReadOnlySpan<short> samples, int channels, int sampleCount)
        => AddToBuffer(samples, channels, sampleCount, 32768f, 0f);

    /// <inheritdoc cref="Add(ReadOnlySpan{short}, int, int)"/>
    public void Add(ReadOnlySpan<short> samples, int channels)
        => Add(samples, channels, channels > 0 ? samples.Length / channels : 0);

    /// <summary>
    /// 更新本帧的音频数据。每帧只能调用一次（内部有跨帧的平滑与对齐）。
    /// </summary>
    /// <param name="secondsSinceLastFrame">距上一帧的秒数。</param>
    /// <param name="frame">启动以来已渲染的帧数。</param>
    /// <remarks>对应 <c>PCM.cpp:49-68</c>。</remarks>
    public void UpdateFrameAudioData(double secondsSinceLastFrame, uint frame)
    {
        // 1. 从环形输入缓冲取出最新窗口
        lock (_sync)
        {
            CopyNewWaveformData(_inputBufferL, _waveformL);
            CopyNewWaveformData(_inputBufferR, _waveformR);
        }

        // 2. 左右声道各做一次频谱
        UpdateSpectrum(_waveformL, _spectrumL);
        UpdateSpectrum(_waveformR, _spectrumR);

        // 3. 波形对齐
        _alignL.Align(_waveformL);
        _alignR.Align(_waveformR);

        // 4. 三频段响度
        //    注意：原版三个频段都吃左声道频谱（PCM.cpp:64-66），这里保持一致。
        _bass.Update(_spectrumL, secondsSinceLastFrame, frame);
        _middles.Update(_spectrumL, secondsSinceLastFrame, frame);
        _treble.Update(_spectrumL, secondsSinceLastFrame, frame);
    }

    /// <summary>取当前帧音频数据的快照。</summary>
    /// <remarks>对应 <c>PCM.cpp:70-91</c>。</remarks>
    public FrameAudioData GetFrameAudioData()
    {
        var data = new FrameAudioData();

        Array.Copy(_waveformL, data.WaveformLeft, AudioConstants.WaveformSamples);
        Array.Copy(_waveformR, data.WaveformRight, AudioConstants.WaveformSamples);
        Array.Copy(_spectrumL, data.SpectrumLeft, AudioConstants.SpectrumSamples);
        Array.Copy(_spectrumR, data.SpectrumRight, AudioConstants.SpectrumSamples);

        data.Bass = _bass.CurrentRelative;
        data.Mid = _middles.CurrentRelative;
        data.Treb = _treble.CurrentRelative;

        data.BassAtt = _bass.AverageRelative;
        data.MidAtt = _middles.AverageRelative;
        data.TrebAtt = _treble.AverageRelative;

        data.Vol = (data.Bass + data.Mid + data.Treb) * 0.333f;
        data.VolAtt = (data.BassAtt + data.MidAtt + data.TrebAtt) * 0.333f;

        return data;
    }

    /// <remarks>对应 <c>PCM.cpp:6-34</c>。</remarks>
    private void AddToBuffer<TSample>(ReadOnlySpan<TSample> samples, int channels, int sampleCount,
        float signalAmplitude, float signalOffset)
        where TSample : unmanaged, INumber<TSample>
    {
        if (channels <= 0 || sampleCount <= 0)
        {
            return;
        }

        lock (_sync)
        {
            var start = _start;
            for (var i = 0; i < sampleCount; i++)
            {
                var bufferOffset = (start + i) % AudioConstants.AudioBufferSamples;
                _inputBufferL[bufferOffset] =
                    128f * (float.CreateTruncating(samples[i * channels]) - signalOffset) / signalAmplitude;
                if (channels > 1)
                {
                    _inputBufferR[bufferOffset] =
                        128f * (float.CreateTruncating(samples[1 + (i * channels)]) - signalOffset) / signalAmplitude;
                }
                else
                {
                    _inputBufferR[bufferOffset] = _inputBufferL[bufferOffset];
                }
            }

            _start = (start + sampleCount) % AudioConstants.AudioBufferSamples;
        }
    }

    /// <remarks>对应 <c>PCM.cpp:93-109</c>。</remarks>
    private void UpdateSpectrum(float[] waveformData, float[] spectrumData)
    {
        // 输入先做一点阻尼（相邻两点平均），压掉高频噪声。
        var oldI = 0;
        for (var i = 0; i < AudioConstants.AudioBufferSamples; i++)
        {
            _fftInput[i] = 0.5f * (waveformData[i] + waveformData[oldI]);
            oldI = i;
        }

        _fft.TimeToFrequencyDomain(_fftInput, spectrumData);
    }

    /// <remarks>对应 <c>PCM.cpp:111-119</c>。</remarks>
    private void CopyNewWaveformData(float[] source, float[] destination)
    {
        var bufferStartIndex = _start;

        for (var i = 0; i < AudioConstants.AudioBufferSamples; i++)
        {
            destination[i] = source[(bufferStartIndex + i) % AudioConstants.AudioBufferSamples];
        }
    }
}
