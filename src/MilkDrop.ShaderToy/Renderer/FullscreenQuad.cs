using MilkDrop.Rendering.Gles3;
using Silk.NET.OpenGLES;

namespace MilkDrop.ShaderToy.Renderer;

/// <summary>
/// 全屏三角形：3 个顶点、无顶点缓冲，靠 <c>gl_VertexID</c> 生成坐标。
/// </summary>
/// <remarks>
/// 顶点着色器见 <see cref="Compile.ShaderToyTranspiler.FullscreenVertexSource"/>。
/// 这里仍然建一个空 VAO —— GLES3 里画图必须有 VAO 绑定（默认 VAO 已经是废弃概念），
/// 没有它部分驱动会报 <c>GL_INVALID_OPERATION</c>。
/// </remarks>
public sealed class FullscreenQuad : RenderItem
{
    public FullscreenQuad()
    {
        Init();
    }

    /// <inheritdoc />
    public override void InitVertexAttrib()
    {
        // 顶点由 gl_VertexID 算出，不需要任何属性数组。
    }

    /// <summary>画一个覆盖整个视口的三角形。</summary>
    public void Draw()
    {
        var gl = GlContext.Gl;
        gl.BindVertexArray(VaoId);
        gl.DrawArrays(GLEnum.Triangles, 0, 3);
        gl.BindVertexArray(0);
    }
}
