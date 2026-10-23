using MilkDrop.ProjectM.MilkdropPreset;
using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="PerFrameContext"/> 的变量装载与代码执行用例。</summary>
public sealed class PerFrameContextTests
{
    private static (PresetState State, PerFrameContext Context) Create()
    {
        var state = new PresetState();
        var context = new PerFrameContext(state.GlobalRegisters);
        context.RegisterBuiltinVariables();
        return (state, context);
    }

    [Fact]
    public void LoadStateVariables_CopiesPresetState()
    {
        (PresetState state, PerFrameContext context) = Create();

        state.Zoom = 1.25f;
        state.RotCX = 0.4f;
        state.Decay = 0.93f;
        state.WaveDots = true;
        state.RenderContext.Time = 12.5f;
        state.RenderContext.ViewportSizeX = 1024;
        state.RenderContext.PerPixelMeshX = 32;
        state.AudioData.Bass = 0.75f;

        context.LoadStateVariables(state);

        Assert.Equal(1.25, context.Zoom.Value, 6);
        Assert.Equal(0.4, context.Cx.Value, 6);
        Assert.Equal(0.93, context.Decay.Value, 6);
        Assert.Equal(1.0, context.WaveUsedots.Value, 6); // bool → 1
        Assert.Equal(12.5, context.Time.Value, 6);
        Assert.Equal(1024, context.Pixelsx.Value, 6);
        Assert.Equal(32, context.Meshx.Value, 6);
        Assert.Equal(0.75, context.Bass.Value, 6);
    }

    [Fact]
    public void ExecutePerFrameCode_UpdatesVariablesForThisFrameOnly()
    {
        (PresetState state, PerFrameContext context) = Create();

        state.PerFrameCode = "zoom = zoom + 0.1; q1 = bass * 2;";
        context.CompilePerFrameCode(state.PerFrameCode);

        state.Zoom = 1.2f;
        state.AudioData.Bass = 0.5f;

        context.LoadStateVariables(state);
        context.ExecutePerFrameCode();

        Assert.Equal(1.3, context.Zoom.Value, 6);
        Assert.Equal(1.0, context.QVar(0).Value, 6);

        // 下一帧重新从 PresetState 取值（表达式结果不回写 state）。
        context.LoadStateVariables(state);
        context.ExecutePerFrameCode();

        Assert.Equal(1.3, context.Zoom.Value, 6);
        Assert.Equal(1.2f, state.Zoom);
    }

    [Fact]
    public void EvaluateInitCode_SeedsQVariablesForEveryFrame()
    {
        (PresetState state, PerFrameContext context) = Create();

        state.PerFrameInitCode = "q1 = 0.25; q2 = 2;";
        state.PerFrameCode = "q1 = q1 + 1;";

        context.EvaluateInitCode(state);
        context.CompilePerFrameCode(state.PerFrameCode);

        Assert.Equal(0.25, state.FrameQVariables[0], 6);
        Assert.Equal(2.0, state.FrameQVariables[1], 6);

        // 两帧都从 init 后的 q1 = 0.25 起算。
        for (int frame = 0; frame < 2; frame++)
        {
            context.LoadStateVariables(state);
            context.ExecutePerFrameCode();
            Assert.Equal(1.25, context.QVar(0).Value, 6);
            Assert.Equal(2.0, context.QVar(1).Value, 6);
        }
    }

    [Fact]
    public void GlobalRegisters_AreSharedWithPresetState()
    {
        (PresetState state, PerFrameContext context) = Create();

        state.GlobalRegisters[5] = 3;
        context.CompilePerFrameCode("reg05 = reg05 + 1;");

        context.LoadStateVariables(state);
        context.ExecutePerFrameCode();

        Assert.Equal(4, state.GlobalRegisters[5], 6);
    }

    [Fact]
    public void CompilePerFrameCode_ThrowsOnSyntaxError()
    {
        (_, PerFrameContext context) = Create();

        var exception = Assert.Throws<MilkdropCompileException>(
            () => context.CompilePerFrameCode("zoom = (1 + ;"));

        Assert.Contains("per-frame code", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCode_IsNoOp()
    {
        (PresetState state, PerFrameContext context) = Create();

        state.Zoom = 1.5f;
        context.LoadStateVariables(state);
        context.EvaluateInitCode(state);
        context.CompilePerFrameCode(string.Empty);
        context.ExecutePerFrameCode();

        Assert.Equal(1.5, context.Zoom.Value, 6);
        Assert.Null(context.PerFrameCodeHandle);
    }
}
