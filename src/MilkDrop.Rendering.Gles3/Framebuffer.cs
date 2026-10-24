using Silk.NET.OpenGLES;

namespace MilkDrop.Rendering.Gles3;

/// <summary>
/// 一组帧缓冲对象（FBO），同时充当渲染目标和采样源。
/// </summary>
/// <remarks>
/// <para>对应 <c>Renderer/Framebuffer.hpp:36-241</c> 与 <c>Framebuffer.cpp:6-400</c>。</para>
/// <para>
/// 每个 FBO 可以有多个颜色附件 + 一个深度 / 模板 / 深度模板附件，尺寸共享。
/// draw buffer 顺序固定为：颜色附件升序 → 深度 → 模板 → 深度模板。
/// </para>
/// <para>
/// GLES 3.0 没有 <c>glColorMaski</c>，<see cref="MaskDrawBuffer"/> 一律用 <c>glColorMask</c>
/// 全屏蔽（与 <c>Framebuffer.cpp:316-322</c> 的 <c>USE_GLES</c> 分支一致）。
/// </para>
/// </remarks>
public sealed class Framebuffer : IDisposable
{
    /// <summary>每个 FBO 的附件表：GL 附件枚举 → 附件。</summary>
    private readonly SortedDictionary<int, TextureAttachment>[] _attachments;

    private readonly uint[] _framebufferIds;
    private int _width;
    private int _height;
    private int _readFramebuffer;
    private int _drawFramebuffer;

    /// <summary>创建含 1 个 FBO 的实例。</summary>
    public Framebuffer()
        : this(1)
    {
    }

    /// <summary>创建含 <paramref name="framebufferCount"/> 个 FBO 的实例。</summary>
    public Framebuffer(int framebufferCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framebufferCount, 1);

        _framebufferIds = new uint[framebufferCount];
        _attachments = new SortedDictionary<int, TextureAttachment>[framebufferCount];
        for (var i = 0; i < framebufferCount; i++)
        {
            _attachments[i] = new SortedDictionary<int, TextureAttachment>();
        }

        GlContext.Gl.GenFramebuffers(_framebufferIds.AsSpan());
    }

    /// <summary>FBO 数量。</summary>
    public int Count => _framebufferIds.Length;

    public int Width => _width;

    public int Height => _height;

    /// <summary>把指定索引的 FBO 同时绑成读写目标。</summary>
    public void Bind(int framebufferIndex)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        GlContext.Gl.BindFramebuffer(GLEnum.Framebuffer, _framebufferIds[framebufferIndex]);
        _readFramebuffer = _drawFramebuffer = framebufferIndex;
    }

    /// <summary>绑成读目标。</summary>
    public void BindRead(int framebufferIndex)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        GlContext.Gl.BindFramebuffer(GLEnum.ReadFramebuffer, _framebufferIds[framebufferIndex]);
        _readFramebuffer = framebufferIndex;
    }

    /// <summary>绑成写目标。</summary>
    public void BindDraw(int framebufferIndex)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        GlContext.Gl.BindFramebuffer(GLEnum.DrawFramebuffer, _framebufferIds[framebufferIndex]);
        _drawFramebuffer = framebufferIndex;
    }

    /// <summary>解绑到默认帧缓冲（与 C++ 一样只解绑 draw）。</summary>
    public static void Unbind() => GlContext.Gl.BindFramebuffer(GLEnum.DrawFramebuffer, 0);

    /// <summary>改尺寸并重建所有附件纹理。</summary>
    /// <returns>尺寸真的变了返回 true。</returns>
    public bool SetSize(int width, int height)
    {
        if (width == 0 || height == 0 || (width == _width && height == _height))
        {
            return false;
        }

        _width = width;
        _height = height;

        var gl = GlContext.Gl;
        for (var index = 0; index < _attachments.Length; index++)
        {
            Bind(index);

            foreach (var (attachmentPoint, attachment) in _attachments[index])
            {
                attachment.SetSize(width, height);
                gl.FramebufferTexture2D(GLEnum.Framebuffer, (GLEnum)attachmentPoint, GLEnum.Texture2D,
                    attachment.Texture.TextureId, 0);
            }
        }

        gl.BindFramebuffer(GLEnum.Framebuffer, 0);

        return true;
    }

    /// <summary>取某个附件。</summary>
    public TextureAttachment? GetAttachment(int framebufferIndex, AttachmentType type, int attachmentIndex = 0)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return null;
        }

        var attachmentPoint = AttachmentPoint(type, attachmentIndex);
        return _attachments[framebufferIndex].GetValueOrDefault(attachmentPoint);
    }

    /// <summary>把附件挂到指定槽位。</summary>
    public void SetAttachment(int framebufferIndex, int attachmentIndex, TextureAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        var attachmentPoint = AttachmentPoint(attachment.Type, attachmentIndex);

        // C++ 用的是 std::map::insert：槽位已被占用时不会覆盖，新附件当场析构。
        if (!_attachments[framebufferIndex].TryAdd(attachmentPoint, attachment))
        {
            attachment.Dispose();
            return;
        }

        var gl = GlContext.Gl;
        gl.BindFramebuffer(GLEnum.Framebuffer, _framebufferIds[framebufferIndex]);

        if (_width > 0 && _height > 0)
        {
            gl.FramebufferTexture2D(GLEnum.Framebuffer, (GLEnum)attachmentPoint, GLEnum.Texture2D,
                attachment.Texture.TextureId, 0);
        }

        UpdateDrawBuffers(framebufferIndex);

        // 恢复到之前绑定的读写 FBO。
        gl.BindFramebuffer(GLEnum.ReadFramebuffer, _framebufferIds[_readFramebuffer]);
        gl.BindFramebuffer(GLEnum.DrawFramebuffer, _framebufferIds[_drawFramebuffer]);
    }

    /// <summary>添加一个 RGBA8 颜色附件。</summary>
    public void CreateColorAttachment(int framebufferIndex, int attachmentIndex)
        => CreateColorAttachment(framebufferIndex, attachmentIndex, (int)GLEnum.Rgba, GLEnum.Rgba, GLEnum.UnsignedByte);

    /// <summary>添加一个指定格式的颜色附件。</summary>
    public void CreateColorAttachment(int framebufferIndex, int attachmentIndex, int internalFormat, GLEnum format, GLEnum type)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        var textureAttachment = new TextureAttachment(internalFormat, format, type, _width, _height);
        var attachmentPoint = (int)GLEnum.ColorAttachment0 + attachmentIndex;

        // 与 C++ 的 std::map::insert 语义一致：槽位已占用则丢弃新附件。
        if (!_attachments[framebufferIndex].TryAdd(attachmentPoint, textureAttachment))
        {
            textureAttachment.Dispose();
            return;
        }

        var gl = GlContext.Gl;
        Bind(framebufferIndex);
        if (_width > 0 && _height > 0)
        {
            gl.FramebufferTexture2D(GLEnum.Framebuffer, (GLEnum)attachmentPoint, GLEnum.Texture2D,
                textureAttachment.Texture.TextureId, 0);
        }

        UpdateDrawBuffers(framebufferIndex);
        gl.BindFramebuffer(GLEnum.Framebuffer, 0);
    }

    /// <summary>移除颜色附件。</summary>
    public void RemoveColorAttachment(int framebufferIndex, int attachmentIndex)
        => RemoveAttachment(framebufferIndex, (int)GLEnum.ColorAttachment0 + attachmentIndex);

    /// <summary>取颜色附件的纹理。</summary>
    public Texture? GetColorAttachmentTexture(int framebufferIndex, int attachmentIndex)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return null;
        }

        return _attachments[framebufferIndex].GetValueOrDefault((int)GLEnum.ColorAttachment0 + attachmentIndex)?.Texture;
    }

    public void CreateDepthAttachment(int framebufferIndex)
        => CreateAttachmentOfType(framebufferIndex, AttachmentType.Depth, GLEnum.DepthAttachment);

    public void RemoveDepthAttachment(int framebufferIndex)
        => RemoveAttachment(framebufferIndex, (int)GLEnum.DepthAttachment);

    public void CreateStencilAttachment(int framebufferIndex)
        => CreateAttachmentOfType(framebufferIndex, AttachmentType.Stencil, GLEnum.StencilAttachment);

    public void RemoveStencilAttachment(int framebufferIndex)
        => RemoveAttachment(framebufferIndex, (int)GLEnum.StencilAttachment);

    public void CreateDepthStencilAttachment(int framebufferIndex)
        => CreateAttachmentOfType(framebufferIndex, AttachmentType.DepthStencil, GLEnum.DepthStencilAttachment);

    public void RemoveDepthStencilAttachment(int framebufferIndex)
        => RemoveAttachment(framebufferIndex, (int)GLEnum.DepthStencilAttachment);

    /// <summary>屏蔽/放开某个 draw buffer 的写入。</summary>
    /// <remarks>
    /// GLES 3.0 没有 <c>glColorMaski</c>，只能整体屏蔽（<c>Framebuffer.cpp:314-323</c> 的 GLES 分支）。
    /// </remarks>
    public void MaskDrawBuffer(int bufferIndex, bool masked)
    {
        // 语义取反：masked=true 表示"不写"。
        var write = masked ? 0 : 1;
        GlContext.Gl.ColorMask(write != 0, write != 0, write != 0, write != 0);
    }

    public void Dispose()
    {
        if (_framebufferIds.Length == 0)
        {
            return;
        }

        // 先删附件纹理，再删 FBO（与 C++ 析构顺序一致）。
        foreach (var attachments in _attachments)
        {
            foreach (var attachment in attachments.Values)
            {
                attachment.Dispose();
            }

            attachments.Clear();
        }

        if (GlContext.IsInitialized)
        {
            GlContext.Gl.DeleteFramebuffers(_framebufferIds.AsSpan());
        }

        Array.Clear(_framebufferIds);
    }

    /// <summary>附件类型 + 索引 → GL 附件枚举。</summary>
    private static int AttachmentPoint(AttachmentType type, int attachmentIndex) => type switch
    {
        AttachmentType.Color => (int)GLEnum.ColorAttachment0 + attachmentIndex,
        AttachmentType.Depth => (int)GLEnum.DepthAttachment,
        AttachmentType.Stencil => (int)GLEnum.StencilAttachment,
        AttachmentType.DepthStencil => (int)GLEnum.DepthStencilAttachment,
        _ => (int)GLEnum.ColorAttachment0,
    };

    /// <remarks>对应 <c>Framebuffer.cpp:239-257</c>。</remarks>
    private void CreateAttachmentOfType(int framebufferIndex, AttachmentType type, GLEnum attachmentPoint)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        var textureAttachment = new TextureAttachment(type, _width, _height);
        _attachments[framebufferIndex][(int)attachmentPoint] = textureAttachment;

        var gl = GlContext.Gl;
        Bind(framebufferIndex);
        if (_width > 0 && _height > 0)
        {
            gl.FramebufferTexture2D(GLEnum.Framebuffer, attachmentPoint, GLEnum.Texture2D,
                textureAttachment.Texture.TextureId, 0);
        }

        UpdateDrawBuffers(framebufferIndex);
        gl.BindFramebuffer(GLEnum.Framebuffer, 0);
    }

    /// <remarks>
    /// 对应 <c>Framebuffer.cpp:325-381</c>。颜色附件按枚举升序在前，其余按深度 → 模板 → 深度模板。
    /// </remarks>
    private void UpdateDrawBuffers(int framebufferIndex)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return;
        }

        var buffers = new List<GLEnum>(_attachments[framebufferIndex].Count);

        var hasDepthAttachment = false;
        var hasStencilAttachment = false;
        var hasDepthStencilAttachment = false;

        foreach (var attachmentPoint in _attachments[framebufferIndex].Keys)
        {
            if (attachmentPoint != (int)GLEnum.DepthAttachment
                && attachmentPoint != (int)GLEnum.StencilAttachment
                && attachmentPoint != (int)GLEnum.DepthStencilAttachment)
            {
                buffers.Add((GLEnum)attachmentPoint);
            }
            else if (attachmentPoint == (int)GLEnum.DepthAttachment)
            {
                hasDepthAttachment = true;
            }
            else if (attachmentPoint == (int)GLEnum.StencilAttachment)
            {
                hasStencilAttachment = true;
            }
            else
            {
                hasDepthStencilAttachment = true;
            }
        }

        if (hasDepthAttachment)
        {
            buffers.Add(GLEnum.DepthAttachment);
        }

        if (hasStencilAttachment)
        {
            buffers.Add(GLEnum.StencilAttachment);
        }

        if (hasDepthStencilAttachment)
        {
            buffers.Add(GLEnum.DepthStencilAttachment);
        }

        GlContext.Gl.DrawBuffers(buffers.ToArray());
    }

    /// <remarks>
    /// 对应 <c>Framebuffer.cpp:383-400</c>。
    /// 注意：**这里不销毁附件**。C++ 用 shared_ptr，移除只断开引用，调用方通常马上把它重挂到另一个
    /// FBO 上（<see cref="CopyTexture"/> 的翻页），销毁会变 use-after-free。真要丢弃由调用方 Dispose。
    /// </remarks>
    private TextureAttachment? RemoveAttachment(int framebufferIndex, int attachmentPoint)
    {
        if (framebufferIndex < 0 || framebufferIndex >= _framebufferIds.Length)
        {
            return null;
        }

        var gl = GlContext.Gl;
        gl.BindFramebuffer(GLEnum.Framebuffer, _framebufferIds[framebufferIndex]);
        gl.FramebufferTexture2D(GLEnum.Framebuffer, (GLEnum)attachmentPoint, GLEnum.Texture2D, 0, 0);
        UpdateDrawBuffers(framebufferIndex);

        _attachments[framebufferIndex].Remove(attachmentPoint, out var removed);

        gl.BindFramebuffer(GLEnum.ReadFramebuffer, _framebufferIds[_readFramebuffer]);
        gl.BindFramebuffer(GLEnum.DrawFramebuffer, _framebufferIds[_drawFramebuffer]);

        return removed;
    }
}
