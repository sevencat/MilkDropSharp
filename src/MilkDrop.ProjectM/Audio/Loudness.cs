namespace MilkDrop.ProjectM.Audio;

/// <summary>频段（只用频谱的前半段，每 1/6 一个段）。</summary>
/// <remarks>对应 <c>Audio/Loudness.hpp:26-31</c>。</remarks>
public enum LoudnessBand
{
    /// <summary>低频段（频谱前 1/6）。</summary>
    Bass = 0,

    /// <summary>中频段（频谱第二个 1/6）。</summary>
    Middles = 1,

    /// <summary>高频段（频谱第三个 1/6）。</summary>
    Treble = 2,
}

/// <summary>相对上一帧的响度/节拍强度。</summary>
/// <remarks>对应 <c>Audio/Loudness.hpp:19-94</c> 与 <c>Loudness.cpp:8-59</c>。</remarks>
public sealed class Loudness(LoudnessBand band)
{
    private readonly LoudnessBand _band = band;

    /// <summary>当前帧该频段的频谱强度和。</summary>
    private float _current;

    /// <summary>短期平均。</summary>
    private float _average;

    /// <summary>长期平均。</summary>
    private float _longAverage;

    private float _currentRelative = 1f;

    private float _averageRelative = 1f;

    /// <summary>当前帧该频段的频谱强度和（原生是私有字段，这里暴露给诊断与单测）。</summary>
    public float Current => _current;

    /// <summary>当前帧未平滑的相对响度（围绕 1.0）。</summary>
    public float CurrentRelative => _currentRelative;

    /// <summary>平滑后的相对响度（围绕 1.0，变化更慢）。</summary>
    public float AverageRelative => _averageRelative;

    /// <summary>每帧调用一次。</summary>
    /// <param name="spectrumSamples">当前帧频谱，至少 <see cref="AudioConstants.SpectrumSamples"/> 个。</param>
    /// <param name="secondsSinceLastFrame">距上一帧的秒数（≈ 1/FPS）。</param>
    /// <param name="frame">已渲染帧数（前 50 帧用更快的阻尼，避免起始抖动）。</param>
    public void Update(ReadOnlySpan<float> spectrumSamples, double secondsSinceLastFrame, uint frame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(spectrumSamples.Length, AudioConstants.SpectrumSamples);

        SumBand(spectrumSamples);
        UpdateBandAverage(secondsSinceLastFrame, frame);
    }

    /// <remarks>对应 <c>Loudness.cpp:29-39</c>。</remarks>
    private void SumBand(ReadOnlySpan<float> spectrumSamples)
    {
        var start = AudioConstants.SpectrumSamples * (int)_band / 6;
        var end = AudioConstants.SpectrumSamples * ((int)_band + 1) / 6;

        _current = 0f;
        for (var sample = start; sample < end; sample++)
        {
            _current += spectrumSamples[sample];
        }
    }

    /// <remarks>对应 <c>Loudness.cpp:41-51</c>。</remarks>
    private void UpdateBandAverage(double secondsSinceLastFrame, uint frame)
    {
        var rate = AdjustRateToFps(_current > _average ? 0.2f : 0.5f, secondsSinceLastFrame);
        _average = _average * rate + _current * (1f - rate);

        rate = AdjustRateToFps(frame < 50 ? 0.9f : 0.992f, secondsSinceLastFrame);
        _longAverage = _longAverage * rate + _current * (1f - rate);

        _currentRelative = MathF.Abs(_longAverage) < 0.001f ? 1f : _current / _longAverage;
        _averageRelative = MathF.Abs(_longAverage) < 0.001f ? 1f : _average / _longAverage;
    }

    /// <summary>把"按 30 FPS 每秒衰减"的系数换算成"按当前帧间隔每帧衰减"的系数。</summary>
    /// <remarks>对应 <c>Loudness.cpp:53-59</c>。</remarks>
    private static float AdjustRateToFps(float rate, double secondsSinceLastFrame)
    {
        var perSecondDecayRateAtFps1 = MathF.Pow(rate, 30f);
        return MathF.Pow(perSecondDecayRateAtFps1, (float)secondsSinceLastFrame);
    }
}
