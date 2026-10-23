using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>CState::Default</c>（state.cpp:515-702）与 <c>RandomizePresetVars</c>（:1875-1899）。
/// </summary>
public sealed class PresetStateTests
{
    [Fact]
    public void DefaultSetsDocumentedValues()
    {
        var state = new PresetState(new Random(1));
        state.Default();

        Assert.Equal(PresetState.InvalidPresetDesc, state.Description);
        Assert.Equal(-1f, state.PresetStartTime);
        Assert.False(state.IsBlending);
        Assert.Equal(0, state.MinPsVersion);
        Assert.Equal(0, state.MaxPsVersion);

        // general
        Assert.Equal(3.0f, state.Rating);
        Assert.Equal(0.98f, state.Decay.Value);
        Assert.Equal(2.0f, state.GammaAdj.Value);
        Assert.Equal(2.0f, state.VideoEchoZoom.Value);
        Assert.Equal(0.0f, state.VideoEchoAlpha.Value);
        Assert.Equal(0, state.VideoEchoOrientation);
        Assert.True(state.TexWrap);
        Assert.False(state.Brighten);
        Assert.Equal(0.25f, state.Blur1EdgeDarken.Value);
        Assert.Equal(1.0f, state.Blur3Max.Value);

        // wave
        Assert.Equal(0, state.WaveMode);
        Assert.Equal(-1, state.OldWaveMode);
        Assert.Equal(0.8f, state.WaveAlpha.Value);
        Assert.Equal(1.0f, state.WaveScale.Value);
        Assert.Equal(0.75f, state.WaveSmoothing.Value);
        Assert.Equal(0.5f, state.WaveX.Value);
        Assert.Equal(12.0f, state.MvX.Value);
        Assert.Equal(9.0f, state.MvY.Value);
        Assert.True(state.MaximizeWaveColor);

        // motion
        Assert.Equal(1.0f, state.Zoom.Value);
        Assert.Equal(0.0f, state.Rot.Value);
        Assert.Equal(0.5f, state.RotCX.Value);
        Assert.Equal(1.0f, state.ZoomExponent.Value);
        Assert.Equal(0.01f, state.OuterBorderSize.Value);
        Assert.Equal(0.25f, state.InnerBorderR.Value);
    }

    [Fact]
    public void DefaultRespectsSectionFlags()
    {
        var state = new PresetState(new Random(2));
        state.Description = "my preset";
        state.Decay.Value = 0.5f;
        state.Zoom.Value = 2f;
        state.WaveAlpha.Value = 0.1f;

        state.Default(PresetState.StateMotion);

        // 只重置 motion 区
        Assert.Equal("my preset", state.Description);
        Assert.Equal(0.5f, state.Decay.Value);
        Assert.Equal(0.1f, state.WaveAlpha.Value);
        Assert.Equal(1.0f, state.Zoom.Value);
    }

    [Fact]
    public void WaveAndShapeDefaults()
    {
        var state = new PresetState(new Random(3));
        state.Default();

        foreach (var wave in state.Waves)
        {
            Assert.Equal(0, wave.Enabled);
            Assert.Equal(512, wave.Samples);
            Assert.Equal(0.5f, wave.Smoothing);
            Assert.Equal(1.0f, wave.A);
            Assert.Equal("", wave.InitCode);
        }

        foreach (var shape in state.Shapes)
        {
            Assert.Equal(0, shape.Enabled);
            Assert.Equal(4, shape.Sides);
            Assert.Equal(1, shape.Instances);
            Assert.Equal(0.5f, shape.X);
            Assert.Equal(0.1f, shape.Rad);
            Assert.Equal(0.1f, shape.BorderA);
        }
    }

    [Fact]
    public void RandomizeProducesDocumentedRanges()
    {
        var state = new PresetState(new Random(1234));
        state.RandomizePresetVars();

        Assert.InRange(state.RandPreset.X, 0f, 1f);
        Assert.InRange(state.RandPreset.W, 0f, 1f);
        Assert.Equal(20, state.Xlate.Length);

        for (var k = 0; k < state.Xlate.Length; k++)
        {
            Assert.InRange(state.Xlate[k].X, -1f, 1f);
            Assert.InRange(state.RotBase[k].X, 0f, 6.28f);

            // rot_mult = 0.9*(k/8)^3.2，k=0 时三个分量都必须是 0
            var limit = 0.9f * MathF.Pow(k / 8.0f, 3.2f);
            Assert.InRange(MathF.Abs(state.RotSpeed[k].X), 0f, limit);
            Assert.InRange(MathF.Abs(state.RotSpeed[k].Y), 0f, limit);
            Assert.InRange(MathF.Abs(state.RotSpeed[k].Z), 0f, limit);
        }

        Assert.Equal(0f, state.RotSpeed[0].X);
        Assert.Equal(0f, state.RotSpeed[0].Y);
        Assert.Equal(0f, state.RotSpeed[0].Z);
    }

    [Fact]
    public void RandomizeIsReproducibleWithSeed()
    {
        var a = new PresetState(new Random(42));
        var b = new PresetState(new Random(42));

        a.RandomizePresetVars();
        b.RandomizePresetVars();

        Assert.Equal(a.RandPreset, b.RandPreset);
        Assert.Equal(a.Xlate[7], b.Xlate[7]);
        Assert.Equal(a.RotSpeed[19], b.RotSpeed[19]);
    }
}
