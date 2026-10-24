using MilkDrop.ShaderToy.Pass;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Pass;

/// <summary><see cref="PlaybackClock"/> 的语义（对应 iTime / iTimeDelta / iFrameRate / iFrame）。</summary>
public sealed class PlaybackClockTests
{
    [Fact]
    public void Advance_AccumulatesTimeAndFrames()
    {
        var clock = new PlaybackClock();

        clock.Advance(0.25);
        clock.Advance(0.25);

        Assert.Equal(0.5f, clock.Time, 5);
        Assert.Equal(2, clock.Frame);
        Assert.Equal(0.25f, clock.TimeDelta, 5);
        Assert.Equal(4f, clock.FrameRate, 4);
    }

    [Fact]
    public void Advance_WhenPaused_KeepsTimeAndZeroDelta()
    {
        var clock = new PlaybackClock();
        clock.Advance(0.1);
        clock.Paused = true;

        clock.Advance(1.0);

        Assert.Equal(0.1f, clock.Time, 5);
        Assert.Equal(1, clock.Frame);          // 暂停时不推进帧
        Assert.Equal(0f, clock.TimeDelta);
        Assert.Equal(0f, clock.FrameRate);
    }

    [Fact]
    public void FixedTimeStep_OverridesRealDelta()
    {
        var clock = new PlaybackClock { FixedTimeStep = 1.0 / 60.0 };

        clock.Advance(0.5); // 真实帧时间被忽略

        Assert.Equal(1.0 / 60.0, clock.Time, 6);
        Assert.Equal(60f, clock.FrameRate, 3);
    }

    [Fact]
    public void Advance_ClampsNegativeDeltaToZero()
    {
        var clock = new PlaybackClock();

        clock.Advance(-0.5);

        Assert.Equal(0f, clock.Time);
        Assert.Equal(0f, clock.TimeDelta);
        Assert.Equal(1, clock.Frame);
    }

    [Fact]
    public void Seek_JumpsToTimeAndRestartsFrameCounter()
    {
        var clock = new PlaybackClock();
        clock.Advance(0.3);
        clock.Advance(0.3);

        clock.Seek(12.5);

        Assert.Equal(12.5f, clock.Time, 4);
        Assert.Equal(0, clock.Frame);          // iFrame 重新计数（有些作品拿它当随机种子）
        Assert.Equal(0f, clock.TimeDelta);

        clock.Advance(0.1);
        Assert.Equal(12.6f, clock.Time, 4);
        Assert.Equal(1, clock.Frame);
    }

    [Fact]
    public void Seek_ClampsNegativeToZero()
    {
        var clock = new PlaybackClock();

        clock.Seek(-3);

        Assert.Equal(0f, clock.Time);
    }

    [Fact]
    public void Reset_ClearsTimeAndFrameButKeepsPaused()
    {
        var clock = new PlaybackClock();
        clock.Advance(0.3);
        clock.Paused = true;

        clock.Reset();

        Assert.Equal(0f, clock.Time);
        Assert.Equal(0, clock.Frame);
        Assert.True(clock.Paused);
    }
}
