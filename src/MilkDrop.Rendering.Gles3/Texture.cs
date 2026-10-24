using Silk.NET.OpenGLES;

namespace MilkDrop.Rendering.Gles3;

/// <summary>
/// 一块 GPU 纹理（拥有 GL 纹理对象）。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/Texture.hpp:19-141</c> 与 <c>Texture.cpp:8-113</c>。</para>
/// <para>
/// C++ 用 move-only 语义 + 析构函数释放；C# 用 <see cref="IDisposable"/>。
/// 因为 <see cref="Framebuffer"/> 的附件会互相交换（<c>CopyTexture</c> 的翻页技巧），
/// 这里不缓存 GL 入口，纹理 ID 归零即视为已释放（重复 Dispose 无副作用）。
/// </para>
/// </remarks>
public sealed class Texture : IDisposable
{
    private uint _textureId;
    private GLEnum _target = GLEnum.None;
    private readonly string _name;
    private int _width;
    private int _height;
    private readonly bool _isUserTexture;

    private readonly int _internalFormat;
    private readonly GLEnum _format;
    private readonly GLEnum _type;

    /// <summary>创建一个尚未分配的空纹理（ID 0）。</summary>
    public Texture()
    {
        _name = string.Empty;
    }

    /// <summary>创建 RGB8 空纹理。</summary>
    public Texture(string name, int width, int height, bool isUserTexture)
        : this(name, width, height, (int)GLEnum.Rgba, GLEnum.Rgba, GLEnum.UnsignedByte, isUserTexture)
    {
    }

    /// <summary>按指定格式创建空纹理。</summary>
    public Texture(string name, int width, int height, int internalFormat, GLEnum format, GLEnum type, bool isUserTexture)
    {
        _target = GLEnum.Texture2D;
        _name = name;
        _width = width;
        _height = height;
        _isUserTexture = isUserTexture;
        _internalFormat = internalFormat;
        _format = format;
        _type = type;

        CreateNewTexture();
    }

    /// <summary>接管一个已存在的 GL 纹理（所有权转移，Dispose 时会删除）。</summary>
    public Texture(string name, uint texId, GLEnum target, int width, int height, bool isUserTexture)
    {
        _textureId = texId;
        _target = target;
        _name = name;
        _width = width;
        _height = height;
        _isUserTexture = isUserTexture;
    }

    /// <summary>GL 纹理名字。</summary>
    public uint TextureId => _textureId;

    /// <summary>纹理名（通常是不带扩展名的文件名），用于 shader 里 <c>sampler_&lt;name&gt;</c> 的引用。</summary>
    public string Name => _name;

    /// <summary>GL 纹理目标，例如 <c>GL_TEXTURE_2D</c>。</summary>
    public GLEnum Target => _target;

    public int Width => _width;

    public int Height => _height;

    /// <summary>是否是外部加载的用户纹理（相对 projectM 内部生成的纹理）。</summary>
    public bool IsUserTexture => _isUserTexture;

    /// <summary>纹理尚未分配（ID 0）。</summary>
    public bool Empty => _textureId == 0;

    /// <summary>绑到指定纹理单元，可选同时绑定采样器。</summary>
    public void Bind(int slot, Sampler? sampler = null)
    {
        var gl = GlContext.Gl;
        gl.ActiveTexture((GLEnum)((int)GLEnum.Texture0 + slot));
        gl.BindTexture(_target, _textureId);

        sampler?.Bind((uint)slot);
    }

    /// <summary>从指定纹理单元上解绑。</summary>
    public void Unbind(int slot)
    {
        var gl = GlContext.Gl;
        gl.ActiveTexture((GLEnum)((int)GLEnum.Texture0 + slot));
        gl.BindTexture(_target, 0);
    }

    public void Dispose()
    {
        if (_textureId == 0)
        {
            return;
        }

        GlContext.Gl.DeleteTextures([_textureId]);
        _textureId = 0;
    }

    private unsafe void CreateNewTexture()
    {
        var gl = GlContext.Gl;
        _textureId = gl.GenTexture();
        gl.BindTexture(_target, _textureId);
        gl.TexImage2D(_target, 0, _internalFormat, (uint)_width, (uint)_height, 0, _format, _type, (void*)0);
        gl.BindTexture(_target, 0);
    }
}
