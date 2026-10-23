using System.Numerics;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.Renderer;

/// <summary>
/// 把一张纹理拷到当前帧缓冲 / 目标纹理 / 目标 FBO，可水平或垂直翻转。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/CopyTexture.hpp:16-77</c> 与 <c>CopyTexture.cpp:9-231</c>。</para>
/// <para>
/// 三个重载的分工：<br/>
/// 1) 直接画到当前绑定的目标（只是画个全屏 quad）；<br/>
/// 2) 画到内部 FBO 再换附件，等价于"拷贝成自己的纹理"；<br/>
/// 3) 与另一个 FBO 交换颜色附件，实现原地翻转（projectM 每帧翻转上一帧就靠这个）。
/// </para>
/// </remarks>
public sealed class CopyTexture : RenderItem
{
    // GLES 只吃 300 es；precision 声明来自原版的 shader 头部。
    private const string ShaderVersion = "#version 300 es\n\n";

    private const string VertexShaderSource = """
        precision mediump float;

        layout(location = 0) in vec2 position;
        layout(location = 1) in vec2 tex_coord;

        out vec2 fragment_tex_coord;

        uniform ivec2 flip;

        void main() {
            gl_Position = vec4(position, 0.0, 1.0);
            fragment_tex_coord = tex_coord;
            if (flip.x > 0)
            {
                fragment_tex_coord.s = 1.0 - fragment_tex_coord.s;
            }
            if (flip.y > 0)
            {
                fragment_tex_coord.t = 1.0 - fragment_tex_coord.t;
            }
        }
        """;

    private const string FragmentShaderSource = """
        precision mediump float;

        in vec2 fragment_tex_coord;

        uniform sampler2D texture_sampler;

        out vec4 color;

        void main(){
            color = texture(texture_sampler, fragment_tex_coord);
        }
        """;

    private readonly Shader _shader;
    private readonly Framebuffer _framebuffer = new(1);
    private readonly Sampler _sampler = new((int)GLEnum.ClampToEdge, (int)GLEnum.Nearest);

    private int _width;
    private int _height;

    public CopyTexture()
    {
        Init();

        _framebuffer.CreateColorAttachment(0, 0);

        _shader = new Shader();
        _shader.CompileProgram(ShaderVersion + VertexShaderSource, ShaderVersion + FragmentShaderSource);
    }

    /// <summary>否翻转后得到的纹理（内部 FBO 的第一个颜色附件）。</summary>
    public Texture? Texture => _framebuffer.GetColorAttachmentTexture(0, 0);

    public override unsafe void InitVertexAttrib()
    {
        var gl = GlContext.Gl;
        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);

        gl.VertexAttribPointer(0, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize, (void*)0);
        gl.VertexAttribPointer(1, 2, GLEnum.Float, false, VertexLayout.TexturedPointSize,
            (void*)VertexLayout.TexturedPointUOffset);

        var points = new TexturedPoint[4];

        points[0].X = -1f;
        points[0].Y = 1f;
        points[1].X = 1f;
        points[1].Y = 1f;
        points[2].X = -1f;
        points[2].Y = -1f;
        points[3].X = 1f;
        points[3].Y = -1f;

        points[0].U = 0f;
        points[0].V = 1f;
        points[1].U = 1f;
        points[1].V = 1f;
        points[2].U = 0f;
        points[2].V = 0f;
        points[3].U = 1f;
        points[3].V = 0f;

        gl.BufferData<TexturedPoint>(GLEnum.ArrayBuffer, points, GLEnum.StaticDraw);
    }

    /// <summary>把纹理画到当前绑定的目标上。</summary>
    public void Draw(Texture? originalTexture, bool flipVertical = false, bool flipHorizontal = false)
    {
        if (originalTexture is null)
        {
            return;
        }

        originalTexture.Bind(0);
        Copy(flipVertical, flipHorizontal);
    }

    /// <summary>把纹理拷到内部 FBO（或给定目标纹理）。</summary>
    public void Draw(Texture? originalTexture, Texture? targetTexture, bool flipVertical, bool flipHorizontal)
    {
        if (originalTexture is null
            || originalTexture.Empty
            || (targetTexture is not null && targetTexture.Empty)
            || ReferenceEquals(originalTexture, targetTexture))
        {
            return;
        }

        UpdateTextureSize(targetTexture?.Width ?? originalTexture.Width, targetTexture?.Height ?? originalTexture.Height);

        if (_width == 0 || _height == 0)
        {
            return;
        }

        _framebuffer.Bind(0);
        originalTexture.Bind(0);

        Texture? internalTexture = null;
        var attachment = _framebuffer.GetAttachment(0, AttachmentType.Color, 0);
        if (targetTexture is not null && attachment is not null)
        {
            internalTexture = _framebuffer.GetColorAttachmentTexture(0, 0);
            attachment.Texture = targetTexture;
        }

        Copy(flipVertical, flipHorizontal);

        // 换回自己的纹理。
        if (targetTexture is not null && attachment is not null && internalTexture is not null)
        {
            attachment.Texture = internalTexture;
        }

        Framebuffer.Unbind();
    }

    /// <summary>与目标 FBO 交换颜色附件，实现原地翻转。</summary>
    public void Draw(Texture? originalTexture, Framebuffer framebuffer, int framebufferIndex,
        bool flipVertical = false, bool flipHorizontal = false)
    {
        if (originalTexture is null || originalTexture.Empty)
        {
            return;
        }

        var targetTexture = framebuffer.GetColorAttachmentTexture(framebufferIndex, 0);
        if (targetTexture is null || targetTexture.Empty)
        {
            return;
        }

        UpdateTextureSize(framebuffer.Width, framebuffer.Height);

        if (_width == 0 || _height == 0)
        {
            return;
        }

        _framebuffer.Bind(0);
        originalTexture.Bind(0);

        Copy(flipVertical, flipHorizontal);

        // 交换两个 FBO 的第一个颜色附件。
        var tempAttachment = framebuffer.GetAttachment(framebufferIndex, AttachmentType.Color, 0);
        var ownAttachment = _framebuffer.GetAttachment(0, AttachmentType.Color, 0);
        if (tempAttachment is null || ownAttachment is null)
        {
            Framebuffer.Unbind();
            return;
        }

        framebuffer.RemoveColorAttachment(framebufferIndex, 0);
        framebuffer.SetAttachment(framebufferIndex, 0, ownAttachment);
        _framebuffer.RemoveColorAttachment(0, 0);
        _framebuffer.SetAttachment(0, 0, tempAttachment);

        Framebuffer.Unbind();
    }

    public override void Dispose()
    {
        _sampler.Dispose();
        _shader.Dispose();
        _framebuffer.Dispose();
        base.Dispose();
    }

    private void UpdateTextureSize(int width, int height)
    {
        if (_width == width && _height == height)
        {
            return;
        }

        _width = width;
        _height = height;

        _framebuffer.SetSize(_width, _height);
    }

    private void Copy(bool flipVertical, bool flipHorizontal)
    {
        var gl = GlContext.Gl;

        _shader.Bind();
        _shader.SetUniformInt("texture_sampler", 0);
        _shader.SetUniformInt2("flip", flipHorizontal ? 1 : 0, flipVertical ? 1 : 0);

        _sampler.Bind(0);

        gl.BindVertexArray(VaoId);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);

        gl.BindTexture(GLEnum.Texture2D, 0);
        Sampler.Unbind(0);
        Shader.Unbind();
    }
}
