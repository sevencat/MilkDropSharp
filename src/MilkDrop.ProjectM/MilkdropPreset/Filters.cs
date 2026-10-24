using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Shader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// Milkdrop 1.x 的后处理滤镜：brighten / darken / solarize / invert，
/// 全靠调整混合方程叠一张全屏白色 quad 实现。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/Filters.hpp</c> 与 <c>Filters.cpp</c>。</remarks>
public sealed class Filters : RenderItem
{
    private readonly PresetState _presetState;

    private int _viewportWidth;
    private int _viewportHeight;

    public Filters(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.DisableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 2 * sizeof(float), (void*)0);
    }

    /// <summary>按预设开关依次应用滤镜（对应 <c>Filters::Draw</c>）。</summary>
    public unsafe void Draw()
    {
        UpdateMesh();

        if (_viewportWidth == 0 || _viewportHeight == 0)
        {
            return;
        }

        var gl = GlContext.Gl;

        gl.Enable(GLEnum.Blend);

        Shader shader = _presetState.UntexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformFloat("vertex_point_size", 1.0f);

        gl.BindVertexArray(VaoId);
        gl.VertexAttrib4(1, 1.0f, 1.0f, 1.0f, 1.0f);

        if (_presetState.Brighten)
        {
            Brighten();
        }

        if (_presetState.Darken)
        {
            Darken();
        }

        if (_presetState.Solarize)
        {
            Solarize();
        }

        if (_presetState.Invert)
        {
            Invert();
        }

        gl.BindVertexArray(0);

        Shader.Unbind();

        gl.Disable(GLEnum.Blend);
    }

    private void Brighten()
    {
        var gl = GlContext.Gl;

        gl.BlendFunc(GLEnum.OneMinusDstColor, GLEnum.Zero);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
        gl.BlendFunc(GLEnum.Zero, GLEnum.DstColor);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
        gl.BlendFunc(GLEnum.OneMinusDstColor, GLEnum.Zero);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
    }

    private void Darken()
    {
        var gl = GlContext.Gl;

        gl.BlendFunc(GLEnum.Zero, GLEnum.DstColor);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
    }

    private void Solarize()
    {
        var gl = GlContext.Gl;

        gl.BlendFunc(GLEnum.Zero, GLEnum.OneMinusDstColor);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
        gl.BlendFunc(GLEnum.DstColor, GLEnum.One);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
    }

    private void Invert()
    {
        var gl = GlContext.Gl;

        gl.BlendFunc(GLEnum.OneMinusDstColor, GLEnum.Zero);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
    }

    /// <summary>视口尺寸变化时重建全屏 quad（对应 <c>Filters::UpdateMesh</c>）。</summary>
    private unsafe void UpdateMesh()
    {
        RenderContext renderContext = _presetState.RenderContext;

        if (_viewportWidth == renderContext.ViewportSizeX &&
            _viewportHeight == renderContext.ViewportSizeY)
        {
            return;
        }

        _viewportWidth = renderContext.ViewportSizeX;
        _viewportHeight = renderContext.ViewportSizeY;

        var points = new Point[4];

        float onePlusInvWidth = 1.0f + 1.0f / _viewportWidth;
        float onePlusInvHeight = 1.0f + 1.0f / _viewportHeight;
        points[0] = new Point { X = -onePlusInvWidth, Y = onePlusInvHeight };
        points[1] = new Point { X = onePlusInvWidth, Y = onePlusInvHeight };
        points[2] = new Point { X = -onePlusInvWidth, Y = -onePlusInvHeight };
        points[3] = new Point { X = onePlusInvWidth, Y = -onePlusInvHeight };

        var gl = GlContext.Gl;
        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);
        gl.BufferData<Point>(GLEnum.ArrayBuffer, points, GLEnum.StaticDraw);
        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
    }
}
