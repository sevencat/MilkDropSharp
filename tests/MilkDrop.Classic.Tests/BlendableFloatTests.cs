using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>CBlendableFloat</c>（state.cpp:1912-1946）的语义：赋值会打断混合、混合只在窗口内插值。
/// </summary>
public sealed class BlendableFloatTests
{
    [Fact]
    public void EvalWithNegativeTimeReturnsRawValue()
    {
        var value = new BlendableFloat { Value = 7f };
        var from = new BlendableFloat { Value = 1f };

        value.StartBlendFrom(from, 0f, 10f);

        // 负时间 = "不参与混合"，per-frame 里影响像素运动的量就是这么取的
        Assert.Equal(7f, value.Eval(-1f));
        Assert.Equal(1f, value.Eval(0f));
    }

    [Fact]
    public void BlendsLinearlyWithinWindow()
    {
        var value = new BlendableFloat { Value = 3f };
        var from = new BlendableFloat { Value = 1f };

        value.StartBlendFrom(from, 2f, 4f);   // 2s 起、持续 4s

        Assert.Equal(1f, value.Eval(2f), 6);
        Assert.Equal(2f, value.Eval(4f), 6);
        Assert.Equal(3f, value.Eval(6f), 6);
    }

    [Fact]
    public void BlendingStopsAfterWindowOrOnRewind()
    {
        var value = new BlendableFloat { Value = 3f };
        var from = new BlendableFloat { Value = 1f };

        value.StartBlendFrom(from, 2f, 4f);
        Assert.True(value.IsBlending);

        Assert.Equal(3f, value.Eval(6.5f), 6);
        Assert.False(value.IsBlending);

        // 时间倒流也会打断混合
        value.StartBlendFrom(from, 2f, 4f);
        Assert.Equal(3f, value.Eval(1f), 6);
        Assert.False(value.IsBlending);
    }

    [Fact]
    public void AssignmentCancelsBlending()
    {
        var value = new BlendableFloat { Value = 3f };
        var from = new BlendableFloat { Value = 1f };

        value.StartBlendFrom(from, 0f, 10f);
        value.Value = 5f;

        Assert.False(value.IsBlending);
        Assert.Equal(5f, value.Eval(1f));

        value.StartBlendFrom(from, 0f, 10f);
        value.Add(1f);
        Assert.False(value.IsBlending);
        Assert.Equal(6f, value.Eval(1f));
    }

    [Fact]
    public void ZeroDurationDoesNotStartBlending()
    {
        var value = new BlendableFloat { Value = 3f };
        var from = new BlendableFloat { Value = 1f };

        value.StartBlendFrom(from, 0f, 0.0005f);

        Assert.False(value.IsBlending);
        Assert.Equal(3f, value.Eval(0f));
    }
}
