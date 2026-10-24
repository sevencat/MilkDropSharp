using Silk.NET.OpenGLES;

namespace MilkDrop.Rendering.Gles3;

/// <summary>帧缓冲附件的用途。</summary>
/// <remarks>对应 <c>Renderer/TextureAttachment.hpp:23-29</c>。</remarks>
public enum AttachmentType
{
    /// <summary>颜色附件（默认 <c>GL_RGBA</c>/<c>GL_UNSIGNED_BYTE</c>）。</summary>
    Color,

    /// <summary>深度附件（<c>GL_DEPTH_COMPONENT16</c>）。</summary>
    Depth,

    /// <summary>模板附件（<c>GL_STENCIL_INDEX8</c>）。</summary>
    Stencil,

    /// <summary>深度模板附件（<c>GL_DEPTH24_STENCIL8</c>）。</summary>
    DepthStencil,
}

/// <summary>
/// 帧缓冲的一个纹理附件：纹理本体 + 附件类型。
/// </summary>
/// <remarks>对应 <c>Renderer/TextureAttachment.hpp:17-100</c> 与 <c>TextureAttachment.cpp:11-122</c>。</remarks>
public sealed class TextureAttachment : IDisposable
{
    /// <summary>GL 里没有 <c>GL_STENCIL_INDEX</c> 的 SDK 用这个值（与 C++ 的处理一致）。</summary>
    private const GLEnum StencilIndex = (GLEnum)0x1901;

    private readonly AttachmentType _attachmentType;
    private Texture _texture = new();

    private readonly int _internalFormat;
    private readonly GLEnum _format;
    private readonly GLEnum _type;

    /// <summary>按附件类型创建（颜色附件会用默认 RGBA8 格式）。</summary>
    public TextureAttachment(AttachmentType attachmentType, int width, int height)
    {
        _attachmentType = attachmentType;

        if (width > 0 && height > 0)
        {
            ReplaceTexture(width, height);
        }
    }

    /// <summary>按显式格式创建颜色附件。</summary>
    public TextureAttachment(int internalFormat, GLEnum format, GLEnum type, int width, int height)
    {
        _attachmentType = AttachmentType.Color;
        _internalFormat = internalFormat;
        _format = format;
        _type = type;

        if (width > 0 && height > 0)
        {
            ReplaceTexture(width, height);
        }
    }

    public AttachmentType Type => _attachmentType;

    /// <summary>内部纹理（换页时会整体替换）。</summary>
    public Texture Texture
    {
        get => _texture;
        set => _texture = value;
    }

    /// <summary>重新分配纹理；宽高为 0 时换成空纹理。</summary>
    public void SetSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            ReplaceTexture(width, height);
        }
        else
        {
            _texture.Dispose();
            _texture = new Texture();
        }
    }

    public void Dispose()
    {
        _texture.Dispose();
    }

    /// <remarks>对应 <c>TextureAttachment.cpp:59-122</c>。</remarks>
    private unsafe void ReplaceTexture(int width, int height)
    {
        // 尺寸没变就不重建。
        if (_texture.Width == width && _texture.Height == height)
        {
            return;
        }

        int internalFormat;
        int textureFormat;
        GLEnum pixelFormat;

        switch (_attachmentType)
        {
            case AttachmentType.Color:
                if (_internalFormat == 0)
                {
                    internalFormat = (int)GLEnum.Rgba;
                    textureFormat = (int)GLEnum.Rgba;
                    pixelFormat = GLEnum.UnsignedByte;
                }
                else
                {
                    internalFormat = _internalFormat;
                    textureFormat = (int)_format;
                    pixelFormat = _type;
                }

                break;

            case AttachmentType.Depth:
                internalFormat = (int)GLEnum.DepthComponent16;
                textureFormat = (int)GLEnum.DepthComponent;
                pixelFormat = GLEnum.Float;
                break;

            case AttachmentType.Stencil:
                internalFormat = (int)GLEnum.StencilIndex8;
                textureFormat = (int)StencilIndex;
                pixelFormat = GLEnum.UnsignedByte;
                break;

            case AttachmentType.DepthStencil:
                internalFormat = (int)GLEnum.Depth24Stencil8;
                textureFormat = (int)GLEnum.DepthStencil;
                pixelFormat = GLEnum.UnsignedInt248;
                break;

            default:
                return;
        }

        _texture.Dispose();

        var gl = GlContext.Gl;
        var textureId = gl.GenTexture();
        gl.BindTexture(GLEnum.Texture2D, textureId);
        gl.TexImage2D(GLEnum.Texture2D, 0, internalFormat, (uint)width, (uint)height, 0,
            (GLEnum)textureFormat, pixelFormat, (void*)0);

        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureWrapT, (int)GLEnum.ClampToEdge);

        gl.BindTexture(GLEnum.Texture2D, 0);

        _texture = new Texture(string.Empty, textureId, GLEnum.Texture2D, width, height, false);
    }
}
