using Silk.NET.OpenGLES;

using Framebuffer = MilkDrop.Rendering.Gles3.Framebuffer;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Renderer;

/// <summary>
/// 一个 pass 的双缓冲渲染目标（两张纹理 + 两个 FBO）。
/// </summary>
/// <remarks>
/// <para>
/// buffer pass 必须 ping-pong：GL 里同时把一张纹理当"采样源"和"渲染目标"是非法操作
/// （见 <c>shaderToyIssue.md</c> S3-1）。上游也是每个 pass 两张 image（<c>GpuInput</c> 里带
/// <c>current_image</c> / <c>previous_image</c>）。
/// </para>
/// <para>
/// 语义约定：<see cref="ReadTexture"/> 永远是"上一帧画完的结果"，<see cref="WriteTexture"/> 是本帧要画的。
/// 每个 pass 画完立刻 <see cref="Swap"/> —— 这样同一个 buffer 被后面的 pass 引用时，读到的是本帧结果；
/// 而它自己引用自己时，绑定发生在画之前，读到的自然是上一帧。
/// </para>
/// </remarks>
public sealed class PassTarget : IDisposable
{
    private readonly Framebuffer _framebuffer;
    private bool _writeToSecond;

    public PassTarget(
        string name,
        int width,
        int height,
        int internalFormat,
        GLEnum format,
        GLEnum type)
    {
        Name = name;

        // 两个 FBO 各挂一张颜色附件，尺寸共享
        _framebuffer = new Framebuffer(2);
        for (var index = 0; index < 2; index++)
        {
            _framebuffer.CreateColorAttachment(index, 0, internalFormat, format, type);
        }

        _framebuffer.SetSize(Math.Max(1, width), Math.Max(1, height));
        Clear();
    }

    public string Name { get; }

    public int Width => _framebuffer.Width;

    public int Height => _framebuffer.Height;

    public Framebuffer Framebuffer => _framebuffer;

    /// <summary>本帧要写的 FBO 索引。</summary>
    public int WriteIndex => _writeToSecond ? 1 : 0;

    /// <summary>上一帧结果的 FBO 索引。</summary>
    public int ReadIndex => _writeToSecond ? 0 : 1;

    public Texture? WriteTexture => _framebuffer.GetColorAttachmentTexture(WriteIndex, 0);

    public Texture? ReadTexture => _framebuffer.GetColorAttachmentTexture(ReadIndex, 0);

    /// <summary>绑成本帧的写目标（同时绑定读写）。</summary>
    public void BindWrite() => _framebuffer.Bind(WriteIndex);

    /// <summary>绑成上一帧结果（给 blit / 回读用）。</summary>
    public void BindRead() => _framebuffer.BindRead(ReadIndex);

    /// <summary>交换读写角色。画完一个 pass 后立刻调用。</summary>
    public void Swap() => _writeToSecond = !_writeToSecond;

    /// <summary>改尺寸（内容会丢，两张都重建）。</summary>
    public bool SetSize(int width, int height)
    {
        bool changed = _framebuffer.SetSize(Math.Max(1, width), Math.Max(1, height));
        return changed;
    }

    /// <summary>两张附件都清成黑色（新建 / 重置作品时用）。</summary>
    public void Clear()
    {
        var gl = GlContext.Gl;
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Disable(EnableCap.ScissorTest);

        for (var index = 0; index < 2; index++)
        {
            _framebuffer.Bind(index);
            gl.Viewport(0, 0, (uint)_framebuffer.Width, (uint)_framebuffer.Height);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        Framebuffer.Unbind();
    }

    /// <summary>两个 FBO 是否都可渲染。</summary>
    public bool IsComplete()
    {
        var gl = GlContext.Gl;
        var complete = true;

        for (var index = 0; index < 2 && complete; index++)
        {
            _framebuffer.Bind(index);
            complete = gl.CheckFramebufferStatus(GLEnum.Framebuffer) == GLEnum.FramebufferComplete;
        }

        Framebuffer.Unbind();
        return complete;
    }

    public void Dispose() => _framebuffer.Dispose();
}
