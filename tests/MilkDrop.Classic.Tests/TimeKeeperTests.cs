using MilkDrop.Classic.Visualizer;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>DoTime</c>（pluginshell.cpp:1555-1651）的两个可观察行为：
/// 稳定帧率下收敛到真值、<c>m_time</c> 按 <c>1/fps</c> 累积。
/// </summary>
public sealed class TimeKeeperTests
{
    [Fact]
    public void ConvergesToSteadyFrameRate()
    {
        var now = 0.0;
        var keeper = new TimeKeeper(() => now);

        for (var frame = 0; frame < 400; frame++)
        {
            keeper.Update(frame);
            now += 1.0 / 60.0;
        }

        // 前 128 帧走"累计平均"分支，之后走 64 槽回看 + 0.87 阻尼
        Assert.InRange(keeper.Fps, 59.0f, 61.0f);
    }

    [Fact]
    public void TimeAccumulatesOneOverFps()
    {
        var now = 0.0;
        var keeper = new TimeKeeper(() => now);

        var expected = 0f;
        for (var frame = 0; frame < 120; frame++)
        {
            keeper.Update(frame);
            expected += 1.0f / keeper.Fps;
            now += 1.0 / 60.0;
        }

        // m_time += 1/m_fps 用的是"本帧开始时的 fps"，所以这里按帧累加比对
        Assert.Equal(expected, keeper.Time, 3);
    }

    [Fact]
    public void FirstFrameResetsState()
    {
        var now = 0.0;
        var keeper = new TimeKeeper(() => now);

        // 首帧：m_time 先归零再 += 1/60，fps 保持 60（frame>0 才更新）
        keeper.Update(0);
        Assert.Equal(1.0f / 60.0f, keeper.Time, 5);
        Assert.Equal(60f, keeper.Fps);

        now += 1.0 / 60.0;
        keeper.Update(1);
        Assert.True(keeper.Time > 1.0f / 60.0f);
        Assert.InRange(keeper.Fps, 1f, 200f);
    }
}
