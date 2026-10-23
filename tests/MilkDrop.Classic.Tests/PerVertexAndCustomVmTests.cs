using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Preset;
using MilkDrop.Classic.Visualizer.Sound;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 M2 的三套自定义 VM：per-vertex（state.cpp:353-390 / milkdropfs.cpp:1750-1792）、
/// 波形 per-frame + per-point（state.cpp:393-459 / milkdropfs.cpp:2442-2620）、
/// 图形 per-frame（state.cpp:461-513 / milkdropfs.cpp:2400-2440）。
/// </summary>
public sealed class PerVertexAndCustomVmTests
{
    private static PerFrameContext Context(float bass = 0f, long frame = 0)
    {
        var sound = new MySoundData();
        sound.ImmRel[0] = bass;
        return new PerFrameContext(0f, 60f, sound, frame, 0, 48, 36, 640, 480, 1f, 1f);
    }

    private static WarpParams BaseParams(float zoom = 0.5f)
        => new(zoom, 1f, 0f, 0f, 0.5f, 0.5f, 0f, 0f, 1f, 1f, 0f, 1f);

    [Fact]
    public void PerVertexCodeUsesOwnVm()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=0.5",
            "per_pixel_1=zoom = zoom + x;");

        var state = new PresetState(new Random(1));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        Assert.Null(state.LastError);
        Assert.NotNull(state.PerVertexCode);
        Assert.True(state.PerVertex.Contains("rad"));
        Assert.True(state.PerVertex.Contains("ang"));
        Assert.True(state.PerVertex.Contains("q32"));
    }

    [Fact]
    public void PerVertexEvaluateReadsMeshAndWritesMotion()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=0.5",
            "per_pixel_1=zoom = zoom + x;");

        var state = new PresetState(new Random(2));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());
        state.RunPerFrame(Context());

        // vx=1、aspectX=1 → x = 1*0.5*1+0.5 = 1；zoom 从 per-frame 的 0.5 变成 1.5
        var result = state.PerVertex.Evaluate(BaseParams(), state.PerFrame, 1f, 0f, 1f, 0f, 1f, 1f);

        Assert.Equal(1.5f, result.Zoom, 5);

        // 状态本身不被逐顶点结果污染
        Assert.Equal(0.5, state.PerFrame["zoom"].Value, 6);
    }

    [Fact]
    public void PerVertexQIsIndependentFromPerFrameQ()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "per_frame_init_1=q1 = 5;",
            "per_pixel_1=q1 = q1 + 1;");

        var state = new PresetState(new Random(3));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());
        state.RunPerFrame(Context());

        state.PerVertex.Evaluate(BaseParams(), state.PerFrame, 0f, 0f, 0f, 0f, 1f, 1f);
        state.PerVertex.Evaluate(BaseParams(), state.PerFrame, 0f, 0f, 0f, 0f, 1f, 1f);

        // per-vertex q 跨顶点累积（两帧各执行一次 = 2），per-frame 的 q1 不受影响
        Assert.Equal(2.0, state.PerVertex.Q[0].Value, 6);
        Assert.Equal(5.0, state.PerFrame.Q[0].Value, 6);
    }

    [Fact]
    public void WaveInitCodeReadsPerFrameQAndWritesT()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "per_frame_init_1=q1 = 7;",
            "wavecode_0_enabled=1",
            "wave_0_init1=t1 = 3;",
            "wave_0_init2=t2 = q1;");

        var state = new PresetState(new Random(4));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        Assert.Null(state.LastError);
        var wave = state.WaveExprs[0];
        Assert.Equal(3.0, wave.TValuesAfterInitCode[0], 6);
        Assert.Equal(7.0, wave.TValuesAfterInitCode[1], 6);

        // 同步时 t1..t8 应取回 init 后的值，q 取 per-frame 当前值
        state.RunPerFrame(Context());
        wave.SyncPerFrame(Context(), state.PerFrame, state.Waves[0]);
        Assert.Equal(3.0, wave.PerFrameVar("t1").Value, 6);
        Assert.Equal(7.0, wave.PerFrameVar("q1").Value, 6);
    }

    [Fact]
    public void WavePerFrameOutputFlowsIntoPerPoint()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "wavecode_0_enabled=1",
            "wave_0_per_frame1=r = bass * 2;",
            "wave_0_per_point1=x = x + sample;");

        var state = new PresetState(new Random(5));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context(bass: 1.5f));

        var wave = state.WaveExprs[0];
        wave.SyncPerFrame(Context(bass: 1.5f), state.PerFrame, state.Waves[0]);
        wave.RunPerFrame();

        Assert.Equal(3.0, wave.ReadPerFrameOutputs().R, 6);

        // per-point 的 r 从 per-frame 拷过来，x 从入参出发再叠 sample
        wave.SyncPerPoint(sample: 0.25, value1: 0, value2: 0, x: 0.1, y: 0.9);
        wave.RunPerPoint();

        var outputs = wave.ReadPerPointOutputs();
        Assert.Equal(0.35, outputs.X, 6);
        Assert.Equal(3.0, outputs.R, 6);
        Assert.Equal(0.9, outputs.Y, 6);
    }

    [Fact]
    public void ShapePerFrameSeesShapeFieldsAndBass()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "shapecode_0_enabled=1",
            "shapecode_0_x=0.25",
            "shapecode_0_num_inst=3",
            "shape_0_per_frame1=x = x + bass;");

        var state = new PresetState(new Random(6));
        Assert.True(state.Import(path, 0f));
        var ctx = Context(bass: 2f);
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, ctx);

        Assert.Null(state.LastError);

        var shape = state.ShapeExprs[0];
        shape.SyncPerFrame(ctx, state.PerFrame, state.Shapes[0], instance: 2);
        shape.RunPerFrame();

        Assert.Equal(2.25, shape["x"].Value, 6);
        Assert.Equal(3, shape["num_inst"].Value, 6);
        Assert.Equal(2, shape["instance"].Value, 6);
    }

    [Fact]
    public void WarpMeshAppliesPerVertexCodeToUv()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=0.5",
            "fWarpScale=1.0",
            "per_pixel_1=zoom = zoom * 2;");

        var state = new PresetState(new Random(9));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());
        state.RunPerFrame(Context());

        var gridX = 2;
        var gridY = 2;
        var count = WarpMesh.VertexCount(gridX, gridY);
        var params0 = WarpMesh.FromState(state, 0f, 100f, 100f);

        var plain = new MilkVertex[count];
        WarpMesh.Build(plain, gridX, gridY, 100f, 100f, 1f, 1f, 1f, 1f, params0);

        var withCode = new MilkVertex[count];
        WarpMesh.Build(withCode, gridX, gridY, 100f, 100f, 1f, 1f, 1f, 1f, params0, 0xFFFFFFFF, state.PerVertex, state.PerFrame);

        // 右上角顶点（vx=1, vy=-1）：zoom 0.5→1.0，u 的偏移量正好减半（其余项与 zoom 无关）
        var plainU = plain[2].TexCoord.X;
        var codeU = withCode[2].TexCoord.X;
        Assert.Equal(0.5f, plainU - codeU, 4);
        Assert.True(MathF.Abs(codeU - 0.5f) < MathF.Abs(plainU - 0.5f));
    }

    [Fact]
    public void CompileErrorsAreCollectedPerSection()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "per_pixel_1=zoom = ;",
            "wavecode_0_enabled=1",
            "wave_0_per_point1=x = ;",
            "shapecode_0_enabled=1",
            "shape_0_per_frame1=y = ;");

        var state = new PresetState(new Random(7));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        // 每一段独立报错，互不吞掉
        Assert.Equal(3, state.Errors.Count);
        Assert.Contains("per_vertex", state.Errors[0]);
        Assert.Contains("wave1 per_point", state.Errors[1]);
        Assert.Contains("shape1 per_frame", state.Errors[2]);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public void EmptyCodeClearsOldPrograms()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "per_pixel_1=zoom = 1;",
            "wavecode_0_enabled=1",
            "wave_0_per_frame1=r = 1;");

        var state = new PresetState(new Random(8));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());
        Assert.NotNull(state.PerVertexCode);
        Assert.NotNull(state.WaveExprs[0].PerFrameCode);

        state.PerPixelExpr = "";
        state.Waves[0].PerFrameCode = "";
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        Assert.Null(state.PerVertexCode);
        Assert.Null(state.WaveExprs[0].PerFrameCode);
    }

    private static string WriteTempMilk(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"milkdrop_{Guid.NewGuid():N}.milk");
        File.WriteAllLines(path, lines);
        return path;
    }
}
