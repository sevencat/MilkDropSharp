using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Shader = MilkDrop.ProjectM.Renderer.Shader;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 画面中心的暗角：一个朝向屏幕中心渐隐的三角形扇。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/DarkenCenter.hpp</c> 与 <c>DarkenCenter.cpp</c>。</remarks>
public sealed class DarkenCenter : RenderItem
{
    /// <summary>中心暗角半径（C++ 里硬编码 0.05）。</summary>
    private const float HalfSize = 0.05f;

    private readonly PresetState _presetState;

    private float _aspectY;

    public DarkenCenter(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexLayout.ColoredPointSize, (void*)0);
        gl.VertexAttribPointer(1, 4, GLEnum.Float, false, VertexLayout.ColoredPointSize,
            (void*)VertexLayout.ColoredPointRgbOffset);
    }

    /// <summary>画中心暗角（对应 <c>DarkenCenter::Draw</c>）。</summary>
    public unsafe void Draw()
    {
        var gl = GlContext.Gl;
        gl.BindVertexArray(VaoId);

        float aspectY = _presetState.RenderContext.AspectY;
        if (aspectY != _aspectY)
        {
            _aspectY = aspectY;

            // 宽高比变了就重建网格（照抄 C++ 的 6 个顶点数值）。
            var vertices = new ColoredPoint[6];
            vertices[0] = new ColoredPoint { X = 0, Y = 0, R = 0, G = 0, B = 0, A = 3.0f / 32.0f };
            vertices[1] = new ColoredPoint { X = -HalfSize * _aspectY, Y = 0, R = 0, G = 0, B = 0, A = 0 };
            vertices[2] = new ColoredPoint { X = 0, Y = -HalfSize, R = 0, G = 0, B = 0, A = 0 };
            vertices[3] = new ColoredPoint { X = HalfSize * _aspectY, Y = 0, R = 0, G = 0, B = 0, A = 0 };
            vertices[4] = new ColoredPoint { X = 0, Y = HalfSize, R = 0, G = 0, B = 0, A = 0 };
            vertices[5] = new ColoredPoint { X = -HalfSize * _aspectY, Y = 0, R = 0, G = 0, B = 0, A = 0 };

            gl.BindBuffer(GLEnum.ArrayBuffer, VboId);
            gl.BufferData<ColoredPoint>(GLEnum.ArrayBuffer, vertices, GLEnum.StaticDraw);
            gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        }

        gl.Enable(GLEnum.Blend);
        gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        Shader shader = _presetState.UntexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformFloat("vertex_point_size", 1.0f);

        gl.DrawArrays(GLEnum.TriangleFan, 0, 6);

        gl.Disable(GLEnum.Blend);
        gl.BindVertexArray(0);
        Shader.Unbind();
    }
}
