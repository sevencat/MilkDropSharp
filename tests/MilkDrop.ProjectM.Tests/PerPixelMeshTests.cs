using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.Rendering;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="PerPixelMesh"/> 的网格生成与逐顶点计算（需要离屏 GL）。</summary>
[Collection("GL")]
public sealed class PerPixelMeshTests(OffscreenGlFixture gl)
{
    [Fact]
    public void InitializeMesh_BuildsGridVerticesAndIndices()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        (PresetState state, _, _) = CreateState(4, 2);
        using var mesh = new PerPixelMesh();

        mesh.InitializeMesh(state);

        Assert.Equal(4, mesh.GridSizeX);
        Assert.Equal(2, mesh.GridSizeY);

        // (gridX + 1) * (gridY + 1) 个顶点，gridX*gridY*6 个索引。
        Assert.Equal(5 * 3, mesh.Vertices.Count);
        Assert.Equal(4 * 2 * 6, mesh.ListIndices.Count);

        // 左上角 (-1,-1)，右上角 (1,-1)，左下角 (-1,1)。
        Assert.Equal(-1f, mesh.Vertices[0].X);
        Assert.Equal(-1f, mesh.Vertices[0].Y);
        Assert.Equal(1f, mesh.Vertices[4].X);
        Assert.Equal(-1f, mesh.Vertices[4].Y);
        Assert.Equal(1f, mesh.Vertices[^1].X);
        Assert.Equal(1f, mesh.Vertices[^1].Y);

        // 中心顶点角度固定为 0（避免 atan2(0,0)）。
        Assert.Equal(0f, mesh.Vertices[1 * 5 + 2].Angle);
        Assert.NotEqual(0f, mesh.Vertices[0].Angle);

        // 半径按宽高比修正。
        Assert.Equal(MathF.Sqrt(1 + 1), mesh.Vertices[0].Radius, 5);
    }

    [Fact]
    public void InitializeMesh_ReusesVerticesWhenNothingChanged()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        (PresetState state, _, _) = CreateState(4, 2);
        using var mesh = new PerPixelMesh();

        mesh.InitializeMesh(state);
        mesh.Vertices[0] = default;
        mesh.InitializeMesh(state);

        // 尺寸与视口都没变时不会重新初始化顶点。
        Assert.Equal(0f, mesh.Vertices[0].X);
    }

    [Fact]
    public void CalculateMesh_CopiesPerFrameValuesWithoutPerPixelCode()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        (PresetState state, PerFrameContext frame, PerPixelContext pixel) = CreateState(2, 2);
        using var mesh = new PerPixelMesh();

        mesh.InitializeMesh(state);

        frame.Zoom.Value = 1.5;
        frame.Rot.Value = 0.25;
        frame.Cx.Value = 0.4;
        frame.Sy.Value = 2.0;

        mesh.CalculateMesh(state, frame, pixel);

        Assert.All(mesh.Vertices, vertex =>
        {
            Assert.Equal(1.5f, vertex.Zoom);
            Assert.Equal(0.25f, vertex.Rot);
            Assert.Equal(0.4f, vertex.CenterX);
            Assert.Equal(2.0f, vertex.StretchY);
        });
    }

    [Fact]
    public void CalculateMesh_RunsPerPixelCodeForEveryVertex()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        (PresetState state, PerFrameContext frame, PerPixelContext pixel) = CreateState(2, 2);
        using var mesh = new PerPixelMesh();

        mesh.InitializeMesh(state);
        pixel.CompilePerPixelCode("zoom = zoom * 2; rot = rad;");

        frame.Zoom.Value = 1.25;
        frame.Rot.Value = 9;

        mesh.CalculateMesh(state, frame, pixel);

        Assert.All(mesh.Vertices, vertex =>
        {
            Assert.Equal(2.5f, vertex.Zoom);
            // rot 被 per-pixel 代码覆盖成每个顶点各自的 rad。
            Assert.NotEqual(9f, vertex.Rot);
        });

        // 中心顶点的 rad 已在 InitializeMesh 里算过，与别处不同。
        Assert.All(mesh.Vertices, vertex => Assert.Equal(vertex.Radius, vertex.Rot, 5));
    }

    [Fact]
    public void Draw_RendersMeshWithDefaultWarpShader()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        (PresetState state, PerFrameContext frame, PerPixelContext pixel) = CreateState(16, 12);
        state.RenderContext.ViewportSizeX = 320;
        state.RenderContext.ViewportSizeY = 240;

        using var mainTexture = new Texture("main", 32, 32, false);
        state.MainTexture = mainTexture;

        using var mesh = new PerPixelMesh();
        mesh.Draw(state, frame, pixel);

        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }

    private static (PresetState State, PerFrameContext Frame, PerPixelContext Pixel) CreateState(int meshX, int meshY)
    {
        var state = new PresetState();
        state.RenderContext.PerPixelMeshX = meshX;
        state.RenderContext.PerPixelMeshY = meshY;
        state.RenderContext.ViewportSizeX = 640;
        state.RenderContext.ViewportSizeY = 480;
        state.RenderContext.AspectX = 1f;
        state.RenderContext.AspectY = 1f;
        state.RenderContext.InvAspectX = 1f;
        state.RenderContext.InvAspectY = 1f;

        var frame = new PerFrameContext(state.GlobalRegisters);
        frame.RegisterBuiltinVariables();

        var pixel = new PerPixelContext(state.GlobalRegisters);
        pixel.RegisterBuiltinVariables();
        pixel.LoadStateReadOnlyVariables(state, frame);

        return (state, frame, pixel);
    }
}
