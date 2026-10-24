using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy.Compile;
using MilkDrop.ShaderToy.Pass;
using Silk.NET.OpenGLES;

// Silk.NET.OpenGLES 里也有 Texture/Framebuffer/Shader（那是 GL 句柄类型），这里全部指向我们的封装。
using Framebuffer = MilkDrop.Rendering.Gles3.Framebuffer;
using Shader = MilkDrop.Rendering.Gles3.Shader;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ShaderToy.Renderer;

/// <summary>
/// 一个 image pass 的离屏渲染器：RGBA16F 目标 + 转译后的 shader + 全屏三角形。
/// </summary>
/// <remarks>
/// <para>
/// 与上游的差异：上游把 image pass 直接画到 swapchain（<c>viewport.cpp</c> 的 <c>render_image</c>）；
/// 这里先画到 <c>RGBA16F</c>，再由调用方用 <see cref="CopyTexture"/> 拷到默认帧缓冲 —— 与计划 P3 一致，
/// 也和后面 buffer pass 的"统一离屏 + 最后合成"路径对齐。
/// </para>
/// <para>
/// <c>RGBA16F</c> 不可渲染时（缺 <c>EXT_color_buffer_half_float</c>）自动退到 <c>RGBA8</c> 并记 notes，
/// 不静默失败。
/// </para>
/// </remarks>
public sealed class ImagePassRenderer : IDisposable
{
    private readonly FullscreenQuad _quad;
    private Framebuffer _target;
    private Shader? _shader;
    private int _width;
    private int _height;
    private bool _halfFloat;

    /// <param name="width">绘制缓冲宽（像素）。</param>
    /// <param name="height">绘制缓冲高（像素）。</param>
    /// <param name="preferHalfFloat">是否优先用 <c>RGBA16F</c>（缺失时自动退化）。</param>
    public ImagePassRenderer(int width, int height, bool preferHalfFloat = true)
    {
        _quad = new FullscreenQuad();
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _halfFloat = preferHalfFloat;

        _target = CreateTarget(_halfFloat, out bool complete);
        if (!complete && _halfFloat)
        {
            Notes.Add("RGBA16F 颜色附件不可渲染，已退到 RGBA8（HDR 作品会有截断）。");
            _target.Dispose();
            _halfFloat = false;
            _target = CreateTarget(false, out _);
        }
    }

    /// <summary>渲染目标的纹理（给 <see cref="CopyTexture"/> 采样）。</summary>
    public Texture? Target => _target.GetColorAttachmentTexture(0, 0);

    /// <summary>目标是否用 <c>RGBA16F</c>。</summary>
    public bool IsHalfFloat => _halfFloat;

    public int Width => _width;

    public int Height => _height;

    /// <summary>是否已经有可用的 program。</summary>
    public bool HasProgram => _shader is not null;

    /// <summary>转译产物的 notes（降级/替换说明）。</summary>
    public List<string> Notes { get; } = [];

    /// <summary>最近一次转译出的 GLSL 源码（出错面板要显示它）。</summary>
    public string TranspiledSource { get; private set; } = string.Empty;

    /// <summary>改尺寸（保留目标纹理格式，内容会丢）。</summary>
    public void SetSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        _target.SetSize(width, height);

        if (!IsComplete(_target))
        {
            Notes.Add($"目标 {width}x{height} 帧缓冲不完整（{_target.Width}x{_target.Height}）。");
        }
    }

    /// <summary>转译并编译一个 image pass。旧的 program 只在成功后替换。</summary>
    /// <exception cref="ShaderException">编译/链接失败。</exception>
    /// <exception cref="NotSupportedException">传了 sound pass（P6 才有）。</exception>
    public ShaderToyTranspileResult Compile(ShaderPass pass, string? commonCode = null)
    {
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(pass, commonCode);

        var shader = new Shader();
        shader.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);

        _shader?.Dispose();
        _shader = shader;
        TranspiledSource = result.Source;

        Notes.Clear();
        Notes.AddRange(result.Notes);

        return result;
    }

    /// <summary>渲染一帧（结果留在 <see cref="Target"/> 里，帧缓冲已解绑）。</summary>
    public void Render(GpuInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_shader is null)
        {
            return;
        }

        var gl = GlContext.Gl;

        _target.Bind(0);
        gl.Viewport(0, 0, (uint)_width, (uint)_height);

        // ImGui 会留下 scissor/blend/colorMask，进 pass 前一律恢复成"干净写颜色"。
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.ColorMask(true, true, true, true);

        _shader.Bind();
        input.PushTo(_shader);
        _quad.Draw();
        Shader.Unbind();

        Framebuffer.Unbind();
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        _quad.Dispose();
        _target.Dispose();
    }

    private Framebuffer CreateTarget(bool halfFloat, out bool complete)
    {
        var framebuffer = new Framebuffer(1);

        if (halfFloat)
        {
            framebuffer.CreateColorAttachment(0, 0, (int)GLEnum.Rgba16f, GLEnum.Rgba, GLEnum.HalfFloat);
        }
        else
        {
            framebuffer.CreateColorAttachment(0, 0);
        }

        framebuffer.SetSize(_width, _height);
        complete = IsComplete(framebuffer);
        return framebuffer;
    }

    private static bool IsComplete(Framebuffer framebuffer)
    {
        var gl = GlContext.Gl;
        framebuffer.Bind(0);
        bool complete = gl.CheckFramebufferStatus(GLEnum.Framebuffer) == GLEnum.FramebufferComplete;
        Framebuffer.Unbind();
        return complete;
    }
}
