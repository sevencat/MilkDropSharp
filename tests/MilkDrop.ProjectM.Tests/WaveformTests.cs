using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.MilkdropPreset.Waveforms;
using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// 主波形顶点计算（P5）：16 种模式都能出顶点，且数值落在合理范围内。
/// </summary>
/// <remarks>这些测试不需要 GL，只跑纯数学部分。</remarks>
public sealed class WaveformMathTests
{
    public static TheoryData<WaveformMode> AllModes()
    {
        var data = new TheoryData<WaveformMode>();
        for (var mode = WaveformMode.Circle; mode < WaveformMode.Count; mode++)
        {
            data.Add(mode);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void GetVertices_ProducesSmoothedVerticesForEveryMode(WaveformMode mode)
    {
        PresetState state = CreateState();
        PerFrameContext context = CreateContext(state);

        WaveformMath waveformMath = Factory.Create(mode)!;
        Assert.NotNull(waveformMath);

        var vertices = waveformMath.GetVertices(state, context);

        // 至少第一组必须有顶点。
        Assert.NotEmpty(vertices[0]);
        Assert.All(vertices[0], p =>
        {
            Assert.False(float.IsNaN(p.X) || float.IsInfinity(p.X));
            Assert.False(float.IsNaN(p.Y) || float.IsInfinity(p.Y));
            Assert.InRange(p.X, -10.0f, 10.0f);
            Assert.InRange(p.Y, -10.0f, 10.0f);
        });
    }

    [Fact]
    public void SmoothWaveRoughlyDoublesVertexCount()
    {
        PresetState state = CreateState();
        PerFrameContext context = CreateContext(state);

        // Circle 固定 480/2 = 240 个样本，平滑后是 2*240-1。
        var circle = new Circle();
        var vertices = circle.GetVertices(state, context);

        Assert.Equal(2 * (AudioConstants.WaveformSamples / 2) - 1, vertices[0].Length);
    }

    [Fact]
    public void DoubleLineProducesTwoWaves()
    {
        PresetState state = CreateState();
        PerFrameContext context = CreateContext(state);

        var vertices = new DoubleLine().GetVertices(state, context);

        Assert.NotEmpty(vertices[0]);
        Assert.NotEmpty(vertices[1]);
    }

    [Fact]
    public void FactoryReturnsImplementationForEveryMode()
    {
        for (var mode = WaveformMode.Circle; mode < WaveformMode.Count; mode++)
        {
            Assert.NotNull(Factory.Create(mode));
        }
    }

    private static PresetState CreateState()
    {
        var state = new PresetState();
        state.RenderContext.ViewportSizeX = 640;
        state.RenderContext.ViewportSizeY = 480;
        state.RenderContext.Time = 1.0f;
        state.WaveScale = 1.0f;
        state.WaveSmoothing = 0.75f;

        for (var i = 0; i < AudioConstants.WaveformSamples; i++)
        {
            float value = MathF.Sin(i * 0.05f);
            state.AudioData.WaveformLeft[i] = value;
            state.AudioData.WaveformRight[i] = -value;
        }

        for (var i = 0; i < AudioConstants.SpectrumSamples; i++)
        {
            state.AudioData.SpectrumLeft[i] = i / (float)AudioConstants.SpectrumSamples;
            state.AudioData.SpectrumRight[i] = 1.0f - i / (float)AudioConstants.SpectrumSamples;
        }

        state.AudioData.Treb = 1.0f;
        state.AudioData.Vol = 1.0f;
        return state;
    }

    private static PerFrameContext CreateContext(PresetState state)
    {
        var context = new PerFrameContext(state.GlobalRegisters);
        context.RegisterBuiltinVariables();
        context.WaveX.Value = 0.5;
        context.WaveY.Value = 0.5;
        context.WaveA.Value = 1.0;
        context.WaveR.Value = 1.0;
        context.WaveG.Value = 0.5;
        context.WaveB.Value = 0.25;
        context.WaveMystery.Value = 0.0;
        context.WaveBrighten.Value = 1.0;
        return context;
    }
}
