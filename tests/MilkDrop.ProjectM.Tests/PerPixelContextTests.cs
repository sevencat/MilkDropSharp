using MilkDrop.ProjectM.MilkdropPreset;
using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="PerPixelContext"/> 的变量装载与执行（不需要 GL）。</summary>
public sealed class PerPixelContextTests
{
    private static (PresetState State, PerFrameContext Frame, PerPixelContext Pixel) Create()
    {
        var state = new PresetState();
        var frame = new PerFrameContext(state.GlobalRegisters);
        frame.RegisterBuiltinVariables();
        var pixel = new PerPixelContext(state.GlobalRegisters);
        pixel.RegisterBuiltinVariables();
        return (state, frame, pixel);
    }

    [Fact]
    public void LoadStateReadOnlyVariables_CopiesAudioAndRenderContext()
    {
        (PresetState state, PerFrameContext frame, PerPixelContext pixel) = Create();

        frame.Time.Value = 3.5;
        frame.Bass.Value = 0.5;
        state.RenderContext.PerPixelMeshX = 24;
        state.RenderContext.ViewportSizeY = 720;
        state.RenderContext.AspectX = 1.25f;

        pixel.LoadStateReadOnlyVariables(state, frame);

        Assert.Equal(3.5, pixel.Time.Value, 6);
        Assert.Equal(0.5, pixel.Bass.Value, 6);
        Assert.Equal(24, pixel.Meshx.Value, 6);
        Assert.Equal(720, pixel.Pixelsy.Value, 6);
        Assert.Equal(1.25, pixel.Aspectx.Value, 6);
    }

    [Fact]
    public void LoadPerFrameQVariables_CopiesToStateAndContext()
    {
        (PresetState state, PerFrameContext frame, PerPixelContext pixel) = Create();

        frame.QVar(0).Value = 0.75;
        frame.QVar(31).Value = 2.5;

        pixel.LoadPerFrameQVariables(state, frame);

        Assert.Equal(0.75, pixel.QVar(0).Value, 6);
        Assert.Equal(2.5, pixel.QVar(31).Value, 6);
        Assert.Equal(0.75, state.FrameQVariables[0], 6);
        Assert.Equal(2.5, state.FrameQVariables[31], 6);
    }

    [Fact]
    public void ExecutePerPixelCode_UsesPerVertexCoordinates()
    {
        (_, PerFrameContext frame, PerPixelContext pixel) = Create();

        pixel.CompilePerPixelCode("zoom = zoom + rad * 0.5 + ang;");
        pixel.Time.Value = frame.Time.Value;

        pixel.Zoom.Value = 1.0;
        pixel.Rad.Value = 2.0;
        pixel.Ang.Value = 0.25;
        pixel.ExecutePerPixelCode();

        Assert.Equal(2.25, pixel.Zoom.Value, 6);
    }

    [Fact]
    public void CompilePerPixelCode_ThrowsOnSyntaxError()
    {
        (_, _, PerPixelContext pixel) = Create();

        var exception = Assert.Throws<MilkdropCompileException>(() => pixel.CompilePerPixelCode("zoom = ;"));
        Assert.Contains("per-pixel code", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPerPixelCode_IsNoOp()
    {
        (_, _, PerPixelContext pixel) = Create();

        pixel.CompilePerPixelCode(string.Empty);
        pixel.ExecutePerPixelCode();

        Assert.Null(pixel.PerPixelCodeHandle);
    }
}
