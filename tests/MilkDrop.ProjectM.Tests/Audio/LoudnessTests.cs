using MilkDrop.ProjectM.Audio;
using Xunit;

namespace MilkDrop.ProjectM.Tests.Audio;

/// <summary><see cref="Loudness"/> 的频段划分与相对强度行为。</summary>
public sealed class LoudnessTests
{
    private const double FrameSeconds = 1.0 / 60.0;

    [Fact]
    public void Update_SumBandRespectsSixthRanges()
    {
        var spectrum = new float[AudioConstants.SpectrumSamples];
        spectrum[200] = 5f;

        var bass = new Loudness(LoudnessBand.Bass);
        var middles = new Loudness(LoudnessBand.Middles);
        var treble = new Loudness(LoudnessBand.Treble);

        bass.Update(spectrum, FrameSeconds, 100);
        middles.Update(spectrum, FrameSeconds, 100);
        treble.Update(spectrum, FrameSeconds, 100);

        // 512/6 → bass=[0,85)、middles=[85,170)、treble=[170,256)
        Assert.Equal(0f, bass.Current);
        Assert.Equal(0f, middles.Current);
        Assert.Equal(5f, treble.Current);
    }

    [Fact]
    public void Update_SilenceKeepsRelativeAtOne()
    {
        var loudness = new Loudness(LoudnessBand.Bass);
        var spectrum = new float[AudioConstants.SpectrumSamples];

        for (uint frame = 0; frame < 120; frame++)
        {
            loudness.Update(spectrum, FrameSeconds, frame);
        }

        // 长期平均接近 0 时相对值固定为 1，避免除零把画面炸掉。
        Assert.Equal(1f, loudness.CurrentRelative);
        Assert.Equal(1f, loudness.AverageRelative);
    }

    [Fact]
    public void Update_LoudThenSilentDropsRelativeBelowOne()
    {
        var loudness = new Loudness(LoudnessBand.Bass);
        var spectrum = new float[AudioConstants.SpectrumSamples];
        for (var i = 0; i < 85; i++)
        {
            spectrum[i] = 1f;
        }

        // 先建立长期平均。
        for (uint frame = 0; frame < 120; frame++)
        {
            loudness.Update(spectrum, FrameSeconds, frame);
        }

        var loudRelative = loudness.CurrentRelative;
        Assert.InRange(loudRelative, 0.9f, 1.1f);

        // 静音后当前值下降，长期平均还没散掉，相对值应明显小于 1。
        var silence = new float[AudioConstants.SpectrumSamples];
        for (uint frame = 120; frame < 180; frame++)
        {
            loudness.Update(silence, FrameSeconds, frame);
        }

        Assert.True(loudness.CurrentRelative < 0.2f, $"静音后 CurrentRelative={loudness.CurrentRelative}");
        Assert.Equal(0f, loudness.Current);
    }

    [Fact]
    public void Update_RejectsShortSpectrum()
    {
        var loudness = new Loudness(LoudnessBand.Bass);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            loudness.Update(new float[AudioConstants.SpectrumSamples - 1], FrameSeconds, 0));
    }
}
