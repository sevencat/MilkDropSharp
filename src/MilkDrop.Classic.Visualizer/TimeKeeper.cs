using System.Diagnostics;

namespace MilkDrop.Classic.Visualizer;

/// <summary>
/// <c>CPluginShell::DoTime</c>（pluginshell.cpp:1555-1651）的移植：累计时间 + 帧率估计。
/// </summary>
/// <remarks>
/// <para>
/// 帧率不是简单对 <c>1/delta</c> 做平滑：原版维护一个 128 槽的累积时间环形数组
/// （<c>m_time_hist</c>），用"回看 N 槽的累积时间差 / 槽数"算出真实帧率，
/// 并剔掉暂停造成的超长间隔。这个值直接决定声音分析里所有阻尼系数，不能省。
/// </para>
/// <para>
/// 原版优先用 <c>QueryPerformanceCounter</c>，取不到时才退回 <c>timeGetTime</c>
/// （后者用 0.93/0.8 阻尼、回看全部 128 槽）。.NET 的 <see cref="Stopwatch"/> 就是 QPC，
/// 所以这里恒走高精度分支（0.87/0.6 阻尼、回看 64 槽）。
/// </para>
/// </remarks>
public sealed class TimeKeeper
{
    /// <summary><c>TIME_HIST_SLOTS</c>（pluginshell.h:40）。</summary>
    public const int TimeHistSlots = 128;

    private readonly Func<double> _rawTimeSeconds;
    private readonly float[] _timeHist = new float[TimeHistSlots];

    private double _lastRawTime;
    private int _timeHistPos;

    /// <param name="rawTimeSeconds">
    /// 单调递增的秒数来源；默认用 <see cref="Stopwatch"/>。测试可以注入假时钟。
    /// </param>
    public TimeKeeper(Func<double>? rawTimeSeconds = null)
    {
        _rawTimeSeconds = rawTimeSeconds ?? DefaultRawTime;
    }

    /// <summary><c>m_time</c>：累计动画时间（秒）。</summary>
    public float Time { get; private set; }

    /// <summary><c>m_fps</c>：平滑后的帧率估计。</summary>
    public float Fps { get; private set; } = 60f;

    /// <summary>对应 <c>DoTime()</c>。每帧**绘制前**调用一次，<paramref name="frame"/> 是当前帧号（首帧为 0）。</summary>
    public void Update(long frame)
    {
        if (frame == 0)
        {
            Fps = 60f;
            Time = 0f;
            _timeHistPos = 0;
        }

        var newRawTime = _rawTimeSeconds();
        var elapsed = (float)(newRawTime - _lastRawTime);
        _lastRawTime = newRawTime;

        var slotsToLookBack = TimeHistSlots / 2;   // 高精度计时器分支

        Time += 1.0f / Fps;

        if (frame > TimeHistSlots)
        {
            // 帧率偏低时缩短回看窗口，否则延迟太大
            if (Fps < 60.0f)
            {
                slotsToLookBack = (int)(slotsToLookBack * (0.1f + 0.9f * (Fps / 60.0f)));
            }

            // 过滤掉暂停/卡顿造成的异常间隔
            if (elapsed > 5.0f / Fps || elapsed > 1.0f || elapsed < 0)
            {
                elapsed = 1.0f / 30.0f;
            }

            var oldHistTime = _timeHist[(_timeHistPos - slotsToLookBack + TimeHistSlots) % TimeHistSlots];
            var newHistTime = _timeHist[(_timeHistPos - 1 + TimeHistSlots) % TimeHistSlots] + elapsed;

            _timeHist[_timeHistPos] = newHistTime;
            _timeHistPos = (_timeHistPos + 1) % TimeHistSlots;

            var newFps = slotsToLookBack / (newHistTime - oldHistTime);
            const float damping = 0.87f;

            // 跳变太大（例如切窗口）直接采信新值，否则重阻尼，避免动画发抖
            if (MathF.Abs(Fps - newFps) > 3.0f)
            {
                Fps = newFps;
            }
            else
            {
                Fps = (damping * Fps) + ((1 - damping) * newFps);
            }
        }
        else
        {
            const float damping = 0.6f;

            if (frame < 2)
            {
                elapsed = 1.0f / 30.0f;
            }
            else if (elapsed > 1.0f || elapsed < 0)
            {
                elapsed = 1.0f / Fps;
            }

            var oldHistTime = _timeHist[0];
            var newHistTime = _timeHist[(_timeHistPos - 1 + TimeHistSlots) % TimeHistSlots] + elapsed;

            _timeHist[_timeHistPos] = newHistTime;
            _timeHistPos = (_timeHistPos + 1) % TimeHistSlots;

            if (frame > 0)
            {
                var newFps = frame / (newHistTime - oldHistTime);
                Fps = (damping * Fps) + ((1 - damping) * newFps);
            }
        }
    }

    private static double DefaultRawTime() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
