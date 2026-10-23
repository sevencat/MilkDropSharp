using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="TimeKeeper"/> 的计时与软/硬切换判定。</summary>
public sealed class TimeKeeperTests
{
    [Fact]
    public void StartPreset_TracksProgress()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(4, 2, 3, 0, clock);

        keeper.StartPreset();
        Assert.Equal(0.0, keeper.PresetProgressA(), 6);

        clock.Advance(TimeSpan.FromSeconds(2));
        keeper.UpdateTimers();

        Assert.Equal(0.5, keeper.PresetProgressA(), 6);
        Assert.False(keeper.CanHardCut());
        Assert.Equal(2.0, keeper.GetRunningTime(), 6);
    }

    [Fact]
    public void CanHardCut_BecomesTrueAfterHardCutDuration()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(4, 2, 3, 0, clock);

        keeper.StartPreset();

        clock.Advance(TimeSpan.FromSeconds(3.1));
        keeper.UpdateTimers();
        Assert.True(keeper.CanHardCut());

        keeper.StartPreset();
        Assert.False(keeper.CanHardCut());
    }

    [Fact]
    public void Smoothing_ReportsRatioAndCommitsOnEnd()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(4, 2, 3, 0, clock);

        keeper.StartPreset();
        clock.Advance(TimeSpan.FromSeconds(3.5));
        keeper.UpdateTimers();

        keeper.StartSmoothing();
        Assert.True(keeper.IsSmoothing());
        Assert.Equal(0.0, keeper.SmoothRatio(), 6);

        // 软切期间 A 槽进度钉在 1（旧预设保持画完）。
        Assert.Equal(1.0, keeper.PresetProgressA(), 6);

        clock.Advance(TimeSpan.FromSeconds(1));
        keeper.UpdateTimers();
        Assert.Equal(0.5, keeper.SmoothRatio(), 6);

        // B 槽的进度按 B 自己的预设时长算：1s / 4s = 0.25
        Assert.Equal(0.25, keeper.PresetProgressB(), 6);

        keeper.EndSmoothing();
        Assert.False(keeper.IsSmoothing());
        Assert.Equal(0.25, keeper.PresetProgressA(), 6);
        Assert.Equal(keeper.PresetTimeB(), keeper.PresetTimeA(), 6);
    }

    [Fact]
    public void SampledPresetDuration_WithoutEasterEggIsFixed()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(7.5, 2, 3, 0, clock, new Random(1));

        Assert.Equal(7.5, keeper.SampledPresetDuration(), 6);
        Assert.Equal(7.5, keeper.SampledPresetDuration(), 6);
    }

    [Fact]
    public void SampledPresetDuration_WithEasterEggVariesAroundMean()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(10, 2, 3, 2, clock, new Random(12345));

        var samples = new List<double>();
        for (var i = 0; i < 2000; i++)
        {
            samples.Add(keeper.SampledPresetDuration());
        }

        Assert.True(samples.Any(v => Math.Abs(v - 10) > 1), "抖动没有生效");
        Assert.True(samples.All(v => v >= 1.0), "时长下限 1 秒被打破");
        Assert.InRange(samples.Average(), 9.5, 10.5);
    }

    [Fact]
    public void ChangePresetDuration_AffectsNextPreset()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(4, 2, 3, 0, clock);

        keeper.ChangePresetDuration(8);
        keeper.StartPreset();

        clock.Advance(TimeSpan.FromSeconds(2));
        keeper.UpdateTimers();

        Assert.Equal(0.25, keeper.PresetProgressA(), 6);
    }

    [Fact]
    public void UpdateTimers_ReportsSecondsSinceLastFrame()
    {
        var clock = new FakeTimeProvider();
        var keeper = new TimeKeeper(4, 2, 3, 0, clock);

        clock.Advance(TimeSpan.FromSeconds(0.5));
        keeper.UpdateTimers();
        Assert.Equal(0.5, keeper.SecondsSinceLastFrame, 6);

        clock.Advance(TimeSpan.FromSeconds(0.25));
        keeper.UpdateTimers();
        Assert.Equal(0.25, keeper.SecondsSinceLastFrame, 6);
    }

    /// <summary>1 tick = 1 ms 的假时钟，避免单测依赖真实时间。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan delta) => _timestamp += (long)Math.Round(delta.TotalMilliseconds);
    }
}
