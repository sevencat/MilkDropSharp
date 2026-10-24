using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;
using Shader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 内外两层边框：每层用 4 次旋转的三角形扇拼成"回"字形。
/// </summary>
/// <remarks>对应 <c>MilkdropPreset/Border.hpp</c> 与 <c>Border.cpp</c>。</remarks>
public sealed class Border : RenderItem
{
    private readonly PresetState _presetState;

    public Border(PresetState presetState)
    {
        ArgumentNullException.ThrowIfNull(presetState);
        _presetState = presetState;

        Init();
    }

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, 0, (void*)0);
        gl.DisableVertexAttribArray(1);

        // 先占好 4 个顶点的空间，后面每帧只改数据。
        var vertices = new Point[4];
        gl.BufferData<Point>(GLEnum.ArrayBuffer, vertices, GLEnum.StreamDraw);
    }

    /// <summary>画内外边框（对应 <c>Border::Draw</c>）。</summary>
    public unsafe void Draw(PerFrameContext presetPerFrameContext)
    {
        ArgumentNullException.ThrowIfNull(presetPerFrameContext);

        var gl = GlContext.Gl;

        var outerBorderSize = (float)presetPerFrameContext.ObSize.Value;
        var innerBorderSize = (float)presetPerFrameContext.IbSize.Value;

        gl.BindVertexArray(VaoId);
        gl.BindBuffer(GLEnum.ArrayBuffer, VboId);

        // 边框不做叠加混合。
        gl.Enable(GLEnum.Blend);
        gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

        Shader shader = _presetState.UntexturedShader;
        shader.Bind();
        shader.SetUniformMat4x4("vertex_transformation", PresetState.OrthogonalProjection);
        shader.SetUniformFloat("vertex_point_size", 1.0f);

        var vertices = new Point[4];
        for (var border = 0; border < 2; border++)
        {
            var r = (float)(border == 0 ? presetPerFrameContext.ObR.Value : presetPerFrameContext.IbR.Value);
            var g = (float)(border == 0 ? presetPerFrameContext.ObG.Value : presetPerFrameContext.IbG.Value);
            var b = (float)(border == 0 ? presetPerFrameContext.ObB.Value : presetPerFrameContext.IbB.Value);
            var a = (float)(border == 0 ? presetPerFrameContext.ObA.Value : presetPerFrameContext.IbA.Value);

            if (a <= 0.001f)
            {
                continue;
            }

            gl.VertexAttrib4(1, r, g, b, a);

            float innerRadius = border == 0 ? 1.0f - outerBorderSize : 1.0f - outerBorderSize - innerBorderSize;
            float outerRadius = border == 0 ? 1.0f : 1.0f - outerBorderSize;

            vertices[0] = new Point { X = innerRadius, Y = innerRadius };
            vertices[1] = new Point { X = outerRadius, Y = outerRadius };
            vertices[2] = new Point { X = outerRadius, Y = -outerRadius };
            vertices[3] = new Point { X = innerRadius, Y = -innerRadius };

            for (var rot = 0; rot < 4; rot++)
            {
                gl.BufferSubData<Point>(GLEnum.ArrayBuffer, 0, vertices);
                gl.DrawArrays(GLEnum.TriangleFan, 0, 4);

                // 旋转 90 度：cos(PI/2)=0、sin(PI/2)=1，直接把表达式化简掉（与 C++ 一致）。
                for (var vertex = 0; vertex < 4; vertex++)
                {
                    float x = vertices[vertex].X;
                    float y = vertices[vertex].Y;
                    vertices[vertex].X = -y;
                    vertices[vertex].Y = x;
                }
            }
        }

        Shader.Unbind();

        gl.Disable(GLEnum.Blend);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindVertexArray(0);
    }
}
