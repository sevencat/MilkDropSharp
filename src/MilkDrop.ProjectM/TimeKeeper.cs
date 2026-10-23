namespace MilkDrop.ProjectM;

/// <summary>
/// 预设时长与软/硬切换的时间管理。
/// </summary>
/// <remarks>
/// <para>对应 <c>TimeKeeper.hpp:8-114</c> 与 <c>TimeKeeper.cpp:8-118</c>。</para>
/// <para>
/// C++ 直接用 <c>std::chrono::high_resolution_clock</c>；这里换成 .NET 的 <see cref="TimeProvider"/>，
/// 语义相同（单调计时、可注入假时钟做单测）。随机数用 <see cref="Random"/> 代替 mt19937，
/// 高斯分布用 Box-Muller 实现，数值上不可能与原生逐位一致，但分布一致。
/// </para>
/// </remarks>
public sealed class TimeKeeper
{
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private readonly Random _random;

    private double _secondsSinceLastFrame;

    private double _easterEgg;

    private double _presetDuration;
    private double _presetDurationA;
    private double _presetDurationB;
    private double _softCutDuration;
    private double _hardCutDuration;

    private double _currentTime;
    private double _presetTimeA;
    private double _presetTimeB;

    private int _presetFrameA;
    private int _presetFrameB;

    private bool _isSmoothing;

    /// <param name="presetDuration">单个预设显示时长（秒）。</param>
    /// <param name="smoothDuration">软切（crossfade）时长（秒）。</param>
    /// <param name="hardCutDuration">至少显示多久才允许硬切（秒）。</param>
    /// <param name="easterEgg">预设时长的高斯抖动标准差（0 = 关闭，按固定时长）。</param>
    /// <param name="timeProvider">时钟（单测可注入假时钟）。</param>
    /// <param name="random">随机源（单测可注入定种子实例）。</param>
    public TimeKeeper(double presetDuration, double smoothDuration, double hardCutDuration, double easterEgg,
        TimeProvider? timeProvider = null, Random? random = null)
    {
        _easterEgg = easterEgg;
        _presetDuration = presetDuration;
        _softCutDuration = smoothDuration;
        _hardCutDuration = hardCutDuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        _startTimestamp = _timeProvider.GetTimestamp();

        UpdateTimers();
    }

    /// <summary>距上一帧的秒数（≈ 1/FPS）。</summary>
    public double SecondsSinceLastFrame => _secondsSinceLastFrame;

    /// <summary>当前预设的标称时长。</summary>
    public double PresetDuration => _presetDuration;

    /// <summary>推进计时器，每帧调用一次。</summary>
    /// <remarks>对应 <c>TimeKeeper.cpp:17-26</c>。</remarks>
    public void UpdateTimers()
    {
        var currentFrameTime = _timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds;
        _secondsSinceLastFrame = currentFrameTime - _currentTime;
        _currentTime = currentFrameTime;
        _presetFrameA++;
        _presetFrameB++;
    }

    /// <summary>开始新预设（A 槽）。</summary>
    public void StartPreset()
    {
        _isSmoothing = false;
        _presetTimeA = _currentTime;
        _presetFrameA = 1;
        _presetDurationA = SampledPresetDuration();
    }

    /// <summary>开始软切（B 槽装新预设，A 槽继续画旧预设）。</summary>
    public void StartSmoothing()
    {
        _isSmoothing = true;
        _presetTimeB = _currentTime;
        _presetFrameB = 1;
        _presetDurationB = SampledPresetDuration();
    }

    /// <summary>软切结束，把 B 槽提为当前预设。</summary>
    public void EndSmoothing()
    {
        _isSmoothing = false;
        _presetTimeA = _presetTimeB;
        _presetFrameA = _presetFrameB;
        _presetDurationA = _presetDurationB;
    }

    /// <summary>当前预设是否已经显示够久，可以硬切。</summary>
    public bool CanHardCut() => _currentTime - _presetTimeA > _hardCutDuration;

    /// <summary>软切进度（0..1 之外不裁剪，交给调用方）。</summary>
    public double SmoothRatio() => (_currentTime - _presetTimeB) / _softCutDuration;

    public bool IsSmoothing() => _isSmoothing;

    public double GetRunningTime() => _currentTime;

    public double PresetProgressA() => _isSmoothing ? 1.0 : Math.Min((_currentTime - _presetTimeA) / _presetDurationA, 1.0);

    public double PresetProgressB() => Math.Min((_currentTime - _presetTimeB) / _presetDurationB, 1.0);

    public int PresetFrameA() => _presetFrameA;

    public int PresetFrameB() => _presetFrameB;

    public double PresetTimeA() => _presetTimeA;

    public double PresetTimeB() => _presetTimeB;

    /// <summary>
    /// 本次预设的实际时长：easter egg 开启时按高斯分布抖动（下限 1 秒）。
    /// </summary>
    /// <remarks>对应 <c>TimeKeeper.cpp:107-116</c>。</remarks>
    public double SampledPresetDuration()
    {
        if (_easterEgg < 0.001)
        {
            return _presetDuration;
        }

        return Math.Max(1.0, NextGaussian(_presetDuration, _easterEgg));
    }

    public void ChangeHardCutDuration(double seconds) => _hardCutDuration = seconds;

    public void ChangeSoftCutDuration(double seconds) => _softCutDuration = seconds;

    public void ChangePresetDuration(double seconds) => _presetDuration = seconds;

    public void ChangeEasterEgg(float value) => _easterEgg = value;

    /// <summary>Box-Muller 生成正态分布随机数（对应 <c>std::normal_distribution</c>）。</summary>
    private double NextGaussian(double mean, double standardDeviation)
    {
        // u1 取 (0,1]，避免 log(0)。
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + (standardDeviation * normal);
    }
}
