using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 预设切换的混合状态机与 <c>progress</c> 计算。
/// </summary>
/// <remarks>
/// 对应 <c>CState::StartBlendFrom</c>（state.cpp:704）、<c>UpdateTime</c> 里的
/// <c>m_fBlendProgress</c> 更新（milkdropfs.cpp:815-822）与 <c>var_pf_progress</c>（milkdropfs.cpp:494）。
/// </remarks>
public sealed class PresetBlendTests
{
    [Fact]
    public void StartBlendFromMarksBlending()
    {
        var from = new PresetState();
        var to = new PresetState();

        to.StartBlendFrom(from, animTime: 10f, timespan: 2f);

        Assert.True(to.IsBlending);
        Assert.Equal(10f, to.BlendStartTime);
        Assert.Equal(2f, to.BlendDuration);
        Assert.Equal(0f, to.BlendProgress);
    }

    [Fact]
    public void UpdateBlendRampsProgress()
    {
        var to = new PresetState();
        to.StartBlendFrom(new PresetState(), animTime: 10f, timespan: 2f);

        to.UpdateBlend(11f);
        Assert.Equal(0.5f, to.BlendProgress, 3);
        Assert.True(to.IsBlending);

        // 原版用的是 >1（不是 >=1），正好 1.0 时还算"在混合"
        to.UpdateBlend(12f);
        Assert.Equal(1f, to.BlendProgress, 3);
        Assert.True(to.IsBlending);

        to.UpdateBlend(12.01f);
        Assert.False(to.IsBlending);
    }

    [Fact]
    public void ZeroDurationIsHardCut()
    {
        var to = new PresetState();
        to.StartBlendFrom(new PresetState(), animTime: 0f, timespan: 0f);

        to.UpdateBlend(0f);

        Assert.False(to.IsBlending);
        Assert.Equal(1f, to.BlendProgress);
    }

    [Fact]
    public void ProgressOvershootsButStopsBlending()
    {
        // 换预设晚了 1 秒：progress > 1，但混合已经结束（原版也不夹 0..1）
        var to = new PresetState();
        to.StartBlendFrom(new PresetState(), animTime: 0f, timespan: 2f);

        to.UpdateBlend(3f);

        Assert.Equal(1.5f, to.BlendProgress, 3);
        Assert.False(to.IsBlending);
    }

    [Fact]
    public void UpdateBlendIsNoOpWhenNotBlending()
    {
        var state = new PresetState();

        state.UpdateBlend(123f);

        Assert.False(state.IsBlending);
        Assert.Equal(0f, state.BlendProgress);
    }

    [Fact]
    public void ProgressIsZeroWithoutSchedule()
    {
        // 没有安排下一次切换（宿主没设 NextPresetTime）→ 恒为 0，保持旧行为
        Assert.Equal(0.0, VisualizerEngine.ComputeProgressAt(100f, 50f, -1f));
    }

    [Fact]
    public void ProgressGrowsTowardsNextPreset()
    {
        // 预设 50s 开始，66s 换下一个 → 第 58s 时约 0.5
        var progress = VisualizerEngine.ComputeProgressAt(58f, 50f, 66f);
        Assert.Equal(0.5, progress, 3);

        Assert.Equal(0.0, VisualizerEngine.ComputeProgressAt(50f, 50f, 66f), 3);

        // 原版不夹：到点之后还会继续增大
        Assert.True(VisualizerEngine.ComputeProgressAt(70f, 50f, 66f) > 1.0);
    }

    [Fact]
    public void DegenerateScheduleDoesNotDivideByZero()
    {
        Assert.Equal(0.0, VisualizerEngine.ComputeProgressAt(10f, 10f, 10f));
    }
}
