using System.Text;
using MilkDrop.ProjectM.MilkdropPreset;
using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="PresetState.Initialize"/> 的解析用例（对应 <c>PresetState.cpp:40-165</c>）。</summary>
public sealed class PresetStateTests
{
    private const string PresetText = """
        [preset00]
        fDecay=0.9
        fGammaAdj=2.2
        zoom=1.05
        rot=0.02
        cx=0.4
        bAdditiveWaves=1
        bWaveDots=1
        nWaveMode=3
        wave_r=0.6
        ob_size=0.05
        nMotionVectorsX=20
        bMotionVectorsOn=1
        b1n=0.1
        b1x=0.9
        per_frame_init_1=q1 = 0.25;
        per_frame_1=zoom = zoom + 0.01;
        per_frame_2=q1 = bass * 2;
        per_pixel_1=rot = rot + 0.1;
        wave_0_init1=q2 = 0.5;
        wave_0_per_frame1=q3 = q2;
        wave_0_per_point1=q4 = 1;
        shape_0_init1=q5 = 1;
        shape_0_per_frame1=q6 = 2;
        warp_1=`shader_body
        warp_2=`{
        comp_1=`shader_body
        MILKDROP_PRESET_VERSION=201
        PSVERSION_WARP=2
        PSVERSION_COMP=3
        """;

    private static PresetState Load(string text = PresetText)
    {
        var parser = new PresetFileParser();
        Assert.True(parser.ReadBytes(Encoding.Latin1.GetBytes(text)));

        var state = new PresetState();
        state.Initialize(parser);
        return state;
    }

    [Fact]
    public void Initialize_LoadsScalarValues()
    {
        PresetState state = Load();

        Assert.Equal(0.9f, state.Decay);
        Assert.Equal(2.2f, state.GammaAdj);
        Assert.Equal(1.05f, state.Zoom);
        Assert.Equal(0.02f, state.Rot);
        Assert.Equal(0.4f, state.RotCX);
        Assert.Equal(0.6f, state.WaveR);
        Assert.Equal(0.05f, state.OuterBorderSize);
        Assert.Equal(20f, state.MvX);
        Assert.Equal(0.1f, state.Blur1Min);
        Assert.Equal(0.9f, state.Blur1Max);
        Assert.True(state.AdditiveWaves);
        Assert.True(state.WaveDots);
        Assert.Equal(3, state.WaveMode);
    }

    [Fact]
    public void Initialize_UsesMotionVectorsFlagWhenMvAlphaMissing()
    {
        // 老预设只有 bMotionVectorsOn，没有 mv_a。
        Assert.Equal(1f, Load().MvA);
    }

    [Fact]
    public void Initialize_LoadsExpressionCode()
    {
        PresetState state = Load();

        Assert.Equal("q1 = 0.25;\n", state.PerFrameInitCode);
        Assert.Equal("zoom = zoom + 0.01;\nq1 = bass * 2;\n", state.PerFrameCode);
        Assert.Equal("rot = rot + 0.1;\n", state.PerPixelCode);
    }

    [Fact]
    public void Initialize_LoadsCustomWaveAndShapeCode()
    {
        PresetState state = Load();

        Assert.Equal("q2 = 0.5;\n", state.CustomWaveInitCode[0]);
        Assert.Equal("q3 = q2;\n", state.CustomWavePerFrameCode[0]);
        Assert.Equal("q4 = 1;\n", state.CustomWavePerPointCode[0]);
        Assert.Equal("q5 = 1;\n", state.CustomShapeInitCode[0]);
        Assert.Equal("q6 = 2;\n", state.CustomShapePerFrameCode[0]);

        // 没有用到的槽位保持空串。
        Assert.Equal(string.Empty, state.CustomWaveInitCode[1]);
    }

    [Fact]
    public void Initialize_LoadsShaderCode()
    {
        PresetState state = Load();

        Assert.Equal("shader_body\n{\n", state.WarpShader);
        Assert.Equal("shader_body\n", state.CompositeShader);
    }

    [Fact]
    public void Initialize_ReadsPerShaderVersions()
    {
        PresetState state = Load();

        Assert.Equal(201, state.PresetVersion);
        Assert.Equal(2, state.WarpShaderVersion);
        Assert.Equal(3, state.CompositeShaderVersion);
    }

    [Fact]
    public void Initialize_Milkdrop1PresetsHaveNoShaders()
    {
        PresetState state = Load("MILKDROP_PRESET_VERSION=100\nzoom=1.1\n");

        Assert.Equal(0, state.WarpShaderVersion);
        Assert.Equal(0, state.CompositeShaderVersion);
    }

    [Fact]
    public void Initialize_Milkdrop2UsesSinglePsVersion()
    {
        PresetState state = Load("MILKDROP_PRESET_VERSION=200\nPSVERSION=2\n");

        Assert.Equal(2, state.WarpShaderVersion);
        Assert.Equal(2, state.CompositeShaderVersion);
    }

    [Fact]
    public void HueRandomOffsets_AreWithinPresetRanges()
    {
        PresetState state = Load();

        Assert.InRange(state.HueRandomOffsets[0], 0f, 648.41f);
        Assert.InRange(state.HueRandomOffsets[1], 0f, 537.51f);
        Assert.InRange(state.HueRandomOffsets[2], 0f, 426.61f);
        Assert.InRange(state.HueRandomOffsets[3], 0f, 315.71f);
    }

    [Fact]
    public void OrthogonalProjection_MatchesGlmOrtho()
    {
        // glm::ortho(-1, 1, 1, -1, -40, 40) 列主序对角元 = (1, -1, -2/80, 1)
        Assert.Equal(1f, PresetState.OrthogonalProjection[0]);
        Assert.Equal(-1f, PresetState.OrthogonalProjection[5]);
        Assert.Equal(-0.025f, PresetState.OrthogonalProjection[10], 6);
        Assert.Equal(1f, PresetState.OrthogonalProjection[15]);

        Assert.Equal(1f, PresetState.OrthogonalProjectionFlipped[5]);
    }
}
