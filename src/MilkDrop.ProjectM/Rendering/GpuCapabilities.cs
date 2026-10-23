using System.Runtime.InteropServices;
using Silk.NET.OpenGLES;

namespace MilkDrop.ProjectM.Rendering;

/// <summary>
/// ANGLE/GLES 能力探测结果。启动时打印一次，用来决定 projectM 移植里几处需要降级的地方：
/// warp pass 的 <c>RG16F</c> 运动向量 UV 附件（MRT 第二路）、sampler object、3D 噪声纹理。
/// </summary>
/// <remarks>
/// 对应 C++ 的 <c>MilkdropStaticShaders</c> 里按 <c>GL_SHADING_LANGUAGE_VERSION</c> 选版本的做法
/// （<c>MilkdropStaticShaders.hpp.in:41-44</c>）、以及 <c>Framebuffer.cpp:316-322</c> 的 GLES 分支。
/// 这里多做一步"真的建一个 FBO 试试"，因为扩展串里有没有 <c>EXT_color_buffer_float</c>
/// 并不代表 ANGLE 的 D3D11 后端一定能渲染 <c>RG16F</c>。
/// </remarks>
public sealed class GpuCapabilities
{
    private GpuCapabilities()
    {
    }

    public string Vendor { get; private init; } = "?";

    public string Renderer { get; private init; } = "?";

    public string Version { get; private init; } = "?";

    public string ShadingLanguageVersion { get; private init; } = "?";

    public int MaxDrawBuffers { get; private init; }

    public int MaxColorAttachments { get; private init; }

    public int MaxTextureSize { get; private init; }

    public int MaxTextureImageUnits { get; private init; }

    public int MaxVertexAttribs { get; private init; }

    /// <summary>GLES3 的 sampler object（<c>glGenSamplers</c>）。</summary>
    public bool SamplerObjects { get; private init; }

    /// <summary><c>GL_TEXTURE_3D</c>（噪声体纹理 <c>sampler_noisevol_*</c>）。</summary>
    public bool Texture3D { get; private init; }

    /// <summary>基础测试：<c>RGBA8</c> 颜色附件。</summary>
    public bool Rgba8Renderable { get; private init; }

    /// <summary><c>RGBA16F</c> 颜色附件（浮点中间缓冲的兜底格式）。</summary>
    public bool Rgba16fRenderable { get; private init; }

    /// <summary><c>RG16F</c> 颜色附件；projectM 的运动向量 UV map 直接用它（<c>MilkdropPreset.cpp:243</c>）。</summary>
    public bool Rg16fRenderable { get; private init; }

    /// <summary><c>RGBA8 + RG16F</c> 双附件（warp pass 的 MRT）。</summary>
    public bool Rg16fMrtWithRgba8 { get; private init; }

    /// <summary>扩展串里与移植相关的条目（只挑关键字，日志不打全长）。</summary>
    public IReadOnlyList<string> ExtensionHighlights { get; private init; } = [];

    /// <summary>对应 projectM 该走哪条路径：真·GLES3 还是需要降级。</summary>
    public string Summary =>
        Rg16fMrtWithRgba8
            ? "warp MRT (RGBA8+RG16F) 可用，按 projectM 原生 GLES 路径移植"
            : Rg16fRenderable
                ? "RG16F 单附件可用但 MRT 不可用，warp UV map 需拆两遍渲染"
                : "RG16F 不可渲染，warp UV map 需降级到 RGBA16F/RGBA8（精度损失）";

    public static GpuCapabilities Query(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);

        var extensions = ReadString(gl, StringName.Extensions) ?? "";
        string[] highlights =
        [
            "EXT_color_buffer_float",
            "EXT_color_buffer_half_float",
            "OES_texture_float_linear",
            "OES_texture_half_float_linear",
            "EXT_float_blend",
            "OES_texture_3D",
            "EXT_texture_filter_anisotropic",
            "GL_OES_standard_derivatives",
            "EXT_disjoint_timer_query",
        ];

        var present = highlights.Where(e => extensions.Contains(e, StringComparison.Ordinal)).ToArray();

        return new GpuCapabilities
        {
            Vendor = ReadString(gl, StringName.Vendor) ?? "?",
            Renderer = ReadString(gl, StringName.Renderer) ?? "?",
            Version = ReadString(gl, StringName.Version) ?? "?",
            ShadingLanguageVersion = ReadString(gl, StringName.ShadingLanguageVersion) ?? "?",
            MaxDrawBuffers = SafeGetInteger(gl, GLEnum.MaxDrawBuffers),
            MaxColorAttachments = SafeGetInteger(gl, GLEnum.MaxColorAttachments),
            MaxTextureSize = SafeGetInteger(gl, GLEnum.MaxTextureSize),
            MaxTextureImageUnits = SafeGetInteger(gl, GLEnum.MaxTextureImageUnits),
            MaxVertexAttribs = SafeGetInteger(gl, GLEnum.MaxVertexAttribs),
            SamplerObjects = ProbeSamplerObjects(gl),
            Texture3D = ProbeTexture3D(gl),
            Rgba8Renderable = ProbeFramebuffer(gl, [(GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte)]),
            Rgba16fRenderable = ProbeFramebuffer(gl, [(GLEnum.Rgba16f, GLEnum.Rgba, GLEnum.HalfFloat)]),
            Rg16fRenderable = ProbeFramebuffer(gl, [(GLEnum.RG16f, GLEnum.RG, GLEnum.HalfFloat)]),
            Rg16fMrtWithRgba8 = ProbeFramebuffer(gl,
            [
                (GLEnum.Rgba8, GLEnum.Rgba, GLEnum.UnsignedByte),
                (GLEnum.RG16f, GLEnum.RG, GLEnum.HalfFloat),
            ]),
            ExtensionHighlights = present,
        };
    }

    /// <summary>多行日志文本（启动时一次性打印）。</summary>
    public string ToLogString()
    {
        var lines = new List<string>
        {
            $"GL vendor   : {Vendor}",
            $"GL renderer : {Renderer}",
            $"GL version  : {Version}",
            $"GLSL version: {ShadingLanguageVersion}",
            $"limits      : drawBuffers={MaxDrawBuffers}, colorAttachments={MaxColorAttachments}, "
                + $"textureSize={MaxTextureSize}, textureUnits={MaxTextureImageUnits}, vertexAttribs={MaxVertexAttribs}",
            $"samplers={Yes(SamplerObjects)}, texture3D={Yes(Texture3D)}",
            $"renderable  : RGBA8={Yes(Rgba8Renderable)}, RGBA16F={Yes(Rgba16fRenderable)}, "
                + $"RG16F={Yes(Rg16fRenderable)}, MRT(RGBA8+RG16F)={Yes(Rg16fMrtWithRgba8)}",
            $"extensions  : {(ExtensionHighlights.Count == 0 ? "(none of interest)" : string.Join(", ", ExtensionHighlights))}",
            $"verdict     : {Summary}",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string Yes(bool value) => value ? "yes" : "no";

    private static unsafe string? ReadString(GL gl, StringName name)
    {
        var ptr = gl.GetString(name);
        return ptr == null ? null : Marshal.PtrToStringAnsi((nint)ptr);
    }

    private static int SafeGetInteger(GL gl, GLEnum name)
    {
        try
        {
            return gl.GetInteger(name);
        }
        catch (Exception)
        {
            ClearErrors(gl);
            return 0;
        }
    }

    private static bool ProbeSamplerObjects(GL gl)
    {
        try
        {
            var sampler = gl.GenSamplers(1);
            if (sampler == 0)
            {
                ClearErrors(gl);
                return false;
            }

            gl.DeleteSamplers([sampler]);
            ClearErrors(gl);
            return true;
        }
        catch (Exception)
        {
            ClearErrors(gl);
            return false;
        }
    }

    private static unsafe bool ProbeTexture3D(GL gl)
    {
        var texture = 0u;
        try
        {
            texture = gl.GenTextures(1);
            gl.BindTexture(GLEnum.Texture3D, texture);
            gl.TexImage3D(GLEnum.Texture3D, 0, (int)GLEnum.Rgba8, 4, 4, 4, 0,
                GLEnum.Rgba, GLEnum.UnsignedByte, (void*)0);
            var ok = gl.GetError() == GLEnum.NoError;
            ClearErrors(gl);
            return ok;
        }
        catch (Exception)
        {
            ClearErrors(gl);
            return false;
        }
        finally
        {
            if (texture != 0)
            {
                gl.DeleteTextures([texture]);
            }

            gl.BindTexture(GLEnum.Texture3D, 0);
            ClearErrors(gl);
        }
    }

    /// <summary>
    /// 真建一个 FBO 验证颜色附件可渲染性。ANGLE 在 D3D11 上对 <c>RG16F</c> 的支持取决于
    /// 后端格式映射，扩展串不足以判定，必须实测。
    /// </summary>
    private static unsafe bool ProbeFramebuffer(GL gl, (GLEnum InternalFormat, GLEnum Format, GLEnum Type)[] attachments)
    {
        const int Size = 16;
        var framebuffer = 0u;
        var textures = new uint[attachments.Length];

        try
        {
            framebuffer = gl.GenFramebuffers(1);
            gl.BindFramebuffer(GLEnum.Framebuffer, framebuffer);

            for (var i = 0; i < attachments.Length; i++)
            {
                var (internalFormat, format, type) = attachments[i];
                textures[i] = gl.GenTextures(1);
                gl.BindTexture(GLEnum.Texture2D, textures[i]);
                gl.TexImage2D(GLEnum.Texture2D, 0, (int)internalFormat, Size, Size, 0, format, type, (void*)0);
                gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Nearest);
                gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Nearest);
                gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0 + i, GLEnum.Texture2D, textures[i], 0);
            }

            var buffers = new GLEnum[attachments.Length];
            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = GLEnum.ColorAttachment0 + i;
            }

            gl.DrawBuffers(buffers);

            var status = gl.CheckFramebufferStatus(GLEnum.Framebuffer);
            var complete = status == GLEnum.FramebufferComplete;
            ClearErrors(gl);
            return complete;
        }
        catch (Exception)
        {
            ClearErrors(gl);
            return false;
        }
        finally
        {
            gl.BindFramebuffer(GLEnum.Framebuffer, 0);
            if (framebuffer != 0)
            {
                gl.DeleteFramebuffers([framebuffer]);
            }

            foreach (var texture in textures)
            {
                if (texture != 0)
                {
                    gl.DeleteTextures([texture]);
                }
            }

            gl.BindTexture(GLEnum.Texture2D, 0);
            ClearErrors(gl);
        }
    }

    private static void ClearErrors(GL gl)
    {
        // GL 的错误队列可能有多个残留，清空避免影响后续判定。
        for (var i = 0; i < 8 && gl.GetError() != GLEnum.NoError; i++)
        {
        }
    }
}
