using Silk.NET.OpenGLES;

using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Renderer;

/// <summary>
/// cubemap pass 的渲染目标：一张 <c>GL_TEXTURE_CUBE_MAP</c> 的 6 个面 + 一个可换面的 FBO。
/// </summary>
/// <remarks>
/// <para>
/// 公共库的 <see cref="MilkDrop.Rendering.Gles3.Framebuffer"/> 只支持 2D 附件（projectM 用不到立方体贴图），
/// 所以这里直接走原生 GL。面序与 Shadertoy 一致：
/// <c>+X, -X, +Y, -Y, +Z, -Z</c>。
/// </para>
/// <para>
/// 尺寸固定 1024x1024（网页端 cubemap pass 的尺寸），渲染时把 pass 的 <c>iResolution</c> 设成它。
/// </para>
/// <para>
/// 只支持单 mip（level 0）：cubemap 在 Shadertoy 里是环境贴图，不参与 mipmap 链。
/// </para>
/// </remarks>
public sealed class CubeTarget : IDisposable
{
    /// <summary>面尺寸（与网页端一致）。</summary>
    public const int FaceSize = 1024;

    private readonly uint _textureId;
    private readonly uint _framebufferId;
    private Texture? _texture;
    private bool _disposed;

    public CubeTarget(int size, int internalFormat, GLEnum format, GLEnum type)
    {
        Size = Math.Max(1, size);

        var gl = GlContext.Gl;

        _textureId = gl.GenTexture();
        gl.BindTexture(GLEnum.TextureCubeMap, _textureId);

        for (var face = 0; face < 6; face++)
        {
            gl.TexImage2D(
                FaceTarget(face),
                0,
                internalFormat,
                (uint)Size,
                (uint)Size,
                0,
                format,
                type,
                ReadOnlySpan<byte>.Empty);
        }

        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapR, (int)GLEnum.ClampToEdge);
        gl.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMaxLevel, 0);
        gl.BindTexture(GLEnum.TextureCubeMap, 0);

        _framebufferId = gl.GenFramebuffer();

        // 所有权交给 Texture，方便走统一的 Bind(unit, sampler) 路径
        _texture = new Texture("cubemap", _textureId, GLEnum.TextureCubeMap, Size, Size, isUserTexture: false);
    }

    public int Size { get; }

    /// <summary>包好的立方体贴图（可当普通纹理绑定）。</summary>
    public Texture? Texture => _texture;

    /// <summary>把所有面清成黑色。</summary>
    public void Clear()
    {
        var gl = GlContext.Gl;
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Disable(EnableCap.ScissorTest);

        for (var face = 0; face < 6; face++)
        {
            BeginFace(face);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        EndFace();
    }

    /// <summary>把 FBO 切到某个面（同时设好视口）。调用方画完要 <see cref="EndFace"/>。</summary>
    public void BeginFace(int face)
    {
        var gl = GlContext.Gl;
        gl.BindFramebuffer(GLEnum.Framebuffer, _framebufferId);
        gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0, FaceTarget(face), _textureId, 0);
        gl.Viewport(0, 0, (uint)Size, (uint)Size);
    }

    /// <summary>解绑 FBO。</summary>
    public void EndFace() => GlContext.Gl.BindFramebuffer(GLEnum.Framebuffer, 0);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var gl = GlContext.Gl;
        _texture?.Dispose(); // 连带删除 _textureId
        _texture = null;
        gl.DeleteFramebuffer(_framebufferId);
    }

    private static GLEnum FaceTarget(int face) => (GLEnum)((int)GLEnum.TextureCubeMapPositiveX + face);
}
