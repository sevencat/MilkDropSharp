namespace MilkDrop.ShaderToy.Pass;

/// <summary>
/// 播放时钟：<c>iTime / iTimeDelta / iFrameRate / iFrame</c> 的来源。
/// </summary>
/// <remarks>
/// 与 projectM 的 <c>TimeKeeper</c> 不同：那边管预设切换（软切/硬切/时长抖动），这里只管
/// "一个作品播了多久"。固定步长用于截图对比与回归（同帧号 = 同状态）。
/// </remarks>
public sealed class PlaybackClock
{
    /// <summary>固定步长（秒）。大于 0 时忽略真实帧时间，每帧推进这么多。</summary>
    public double FixedTimeStep { get; set; }

    /// <summary>暂停：不推进时间，<see cref="TimeDelta"/> 归零，画面保持。</summary>
    public bool Paused { get; set; }

    /// <summary>播放时间（秒）。</summary>
    public float Time { get; private set; }

    /// <summary>上一帧推进的秒数（暂停时为 0）。</summary>
    public float TimeDelta { get; private set; }

    /// <summary>帧率，由 <see cref="TimeDelta"/> 推得。</summary>
    public float FrameRate => TimeDelta > 0f ? 1f / TimeDelta : 0f;

    /// <summary>已渲染帧数。</summary>
    public int Frame { get; private set; }

    /// <summary>推进一帧。<paramref name="deltaSeconds"/> 是真实帧时间，固定步长模式下忽略。</summary>
    public void Advance(double deltaSeconds)
    {
        if (Paused)
        {
            TimeDelta = 0f;
            return;
        }

        double step = FixedTimeStep > 0 ? FixedTimeStep : Math.Max(0.0, deltaSeconds);

        TimeDelta = (float)step;
        Time += (float)step;
        Frame++;
    }

    /// <summary>回到 0 帧 / 0 秒（暂停状态保持不变）。</summary>
    public void Reset()
    {
        Time = 0f;
        TimeDelta = 0f;
        Frame = 0;
    }
}
