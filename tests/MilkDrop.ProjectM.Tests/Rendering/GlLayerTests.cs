using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Rendering;
using Silk.NET.OpenGLES;
using Xunit;
using Xunit.Abstractions;

// Silk.NET.OpenGLES 里也有 Framebuffer/Shader/Texture 这些名字（它是 GL 句柄类型），
// 这里全部指向我们自己的封装类。
using Framebuffer = MilkDrop.ProjectM.Renderer.Framebuffer;
using Shader = MilkDrop.ProjectM.Renderer.Shader;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.Tests.Rendering;

/// <summary>
/// GL 封装层（P2）的离屏渲染测试：纹理、FBO/MRT、shader、CopyTexture 翻转。
/// </summary>
/// <remarks>
/// 全部跑在 ANGLE 的 pbuffer 上下文里，不需要窗口；机器上没有可用 GL 时整个类静默跳过。
/// 断言基于像素回读，所以能真正验证"画出来的东西对不对"，而不只是"没抛异常"。
/// </remarks>
[Collection("GL")]
public sealed class GlLayerTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Size = 16;

    private const string FullscreenVertexShader = """
        #version 300 es
        precision highp float;

        void main()
        {
            // 无顶点缓冲的全屏三角形（GLES3 支持 gl_VertexID）。
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    [Fact]
    public void Shader_CompilesAndReportsGlslVersion()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        using var shader = new Shader();
        shader.CompileProgram(FullscreenVertexShader, """
            #version 300 es
            precision mediump float;
            out vec4 color;
            void main() { color = vec4(1.0); }
            """);

        Assert.NotEqual(0u, shader.ProgramId);
        shader.Bind();
        shader.SetUniformFloat("missing_uniform", 1f); // 找不到就静默忽略，不能崩
        Shader.Unbind();

        var version = Shader.GetShaderLanguageVersion();
        output.WriteLine($"GLSL {version.Major}.{version.Minor}");
        Assert.Equal(3, version.Major);
    }

    [Fact]
    public void Shader_CompileErrorThrows()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        using var shader = new Shader();
        var error = Assert.Throws<ShaderException>(() => shader.CompileProgram(FullscreenVertexShader, """
            #version 300 es
            void main() { this_is_not_glsl; }
            """));

        Assert.Contains("Error compiling shader", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Texture_SizedTextureIsNotConsideredEmpty()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        using var empty = new Texture();
        Assert.True(empty.Empty);

        using var texture = new Texture("test", 8, 4, isUserTexture: true);
        Assert.False(texture.Empty);
        Assert.Equal(8, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.True(texture.IsUserTexture);
        Assert.Equal(GLEnum.Texture2D, texture.Target);
    }

    [Fact]
    public void Framebuffer_ColorAttachmentRendersAndReadsBack()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        using var framebuffer = new Framebuffer(1);
        Assert.True(framebuffer.SetSize(Size, Size));
        Assert.False(framebuffer.SetSize(Size, Size)); // 尺寸没变返回 false
        framebuffer.CreateColorAttachment(0, 0);

        Assert.Equal(GLEnum.FramebufferComplete, CheckStatus(framebuffer));

        framebuffer.Bind(0);
        GlContext.Gl.Viewport(0, 0, Size, Size);
        GlContext.Gl.ClearColor(0.25f, 0.5f, 0.75f, 1f);
        GlContext.Gl.Clear((uint)GLEnum.ColorBufferBit);
        Framebuffer.Unbind();

        var pixels = ReadPixels(framebuffer, Size, Size, 0);
        Assert.InRange(pixels[0], 63, 65);
        Assert.InRange(pixels[1], 127, 128);
        Assert.InRange(pixels[2], 190, 192);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void Framebuffer_MrtSecondAttachmentReceivesData()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        // warp pass 的关键能力：颜色附件 0 = RGBA8，附件 1 = RG16F 存运动向量 UV。
        using var framebuffer = new Framebuffer(1);
        framebuffer.SetSize(Size, Size);
        framebuffer.CreateColorAttachment(0, 0);
        framebuffer.CreateColorAttachment(0, 1, (int)GLEnum.RG16f, GLEnum.RG, GLEnum.HalfFloat);

        Assert.NotNull(framebuffer.GetColorAttachmentTexture(0, 1));
        Assert.Equal(GLEnum.FramebufferComplete, CheckStatus(framebuffer));

        using var shader = new Shader();
        shader.CompileProgram(FullscreenVertexShader, """
            #version 300 es
            precision mediump float;
            layout(location = 0) out vec4 colorOut;
            layout(location = 1) out vec4 uvOut;
            void main()
            {
                colorOut = vec4(1.0, 0.0, 0.0, 1.0);
                uvOut = vec4(0.5, 0.25, 0.0, 1.0);
            }
            """);

        framebuffer.Bind(0);
        GlContext.Gl.Viewport(0, 0, Size, Size);
        DrawFullscreenTriangle(shader);
        Framebuffer.Unbind();

        // RG16F 不能直接按 RGBA8 回读，用 CopyTexture 转成 RGBA8 再校验。
        using var readback = new Framebuffer(1);
        readback.SetSize(Size, Size);
        readback.CreateColorAttachment(0, 0);

        using var copy = new CopyTexture();
        copy.Draw(framebuffer.GetColorAttachmentTexture(0, 1), readback, 0);

        var pixels = ReadPixels(readback, Size, Size, 0);
        Assert.InRange(pixels[0], 126, 129); // 0.5
        Assert.InRange(pixels[1], 62, 65);   // 0.25
    }

    [Fact]
    public void CopyTexture_SwapOverloadFlipsVertically()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        using var framebuffer = new Framebuffer(1);
        framebuffer.SetSize(Size, Size);
        framebuffer.CreateColorAttachment(0, 0);

        using var shader = new Shader();
        shader.CompileProgram(FullscreenVertexShader, """
            #version 300 es
            precision mediump float;
            uniform float textureHeight;
            out vec4 color;
            void main() { color = vec4(gl_FragCoord.y / textureHeight, 0.0, 0.0, 1.0); }
            """);

        framebuffer.Bind(0);
        GlContext.Gl.Viewport(0, 0, Size, Size);
        shader.Bind();
        shader.SetUniformFloat("textureHeight", Size);
        DrawFullscreenTriangle();
        Shader.Unbind();
        Framebuffer.Unbind();

        var before = ReadPixels(framebuffer, Size, Size, 0);
        var bottomBefore = before[0];

        using var copy = new CopyTexture();
        copy.Draw(framebuffer.GetColorAttachmentTexture(0, 0), framebuffer, 0, flipVertical: true, flipHorizontal: false);

        var after = ReadPixels(framebuffer, Size, Size, 0);
        var bottomAfter = after[0];

        Assert.InRange(bottomBefore, 1, 20);
        Assert.InRange(bottomAfter, 235, 255);
    }

    [Fact]
    public void Framebuffer_MaskDrawBufferBlocksWrites()
    {
        if (!fixture.TryMakeCurrent())
        {
            return;
        }

        using var framebuffer = new Framebuffer(1);
        framebuffer.SetSize(Size, Size);
        framebuffer.CreateColorAttachment(0, 0);

        framebuffer.Bind(0);
        GlContext.Gl.Viewport(0, 0, Size, Size);
        GlContext.Gl.ClearColor(0f, 0f, 0f, 1f);
        GlContext.Gl.Clear((uint)GLEnum.ColorBufferBit);

        framebuffer.MaskDrawBuffer(0, masked: true);
        GlContext.Gl.ClearColor(1f, 1f, 1f, 1f);
        GlContext.Gl.Clear((uint)GLEnum.ColorBufferBit);
        framebuffer.MaskDrawBuffer(0, masked: false);
        Framebuffer.Unbind();

        var pixels = ReadPixels(framebuffer, Size, Size, 0);

        // GLES 3.0 的 glColorMask 是全局的，屏蔽期间写入应当无效。
        Assert.Equal(0, pixels[0]);
    }

    [Fact]
    public void FileScanner_FindsMatchingExtensionsRecursively()
    {
        var root = Path.Combine(Path.GetTempPath(), "milkdrop-scan-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "sub");
        Directory.CreateDirectory(nested);

        try
        {
            File.WriteAllText(Path.Combine(root, "a.JPG"), "x");
            File.WriteAllText(Path.Combine(nested, "b.jpg"), "x");
            File.WriteAllText(Path.Combine(nested, "c.png"), "x");

            var found = new List<string>();
            new FileScanner([root], [".jpg"]).Scan((path, basename) => found.Add(basename));

            Assert.Equal(2, found.Count);
            Assert.Contains("a", found);
            Assert.Contains("b", found);
            Assert.DoesNotContain("c", found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GLEnum CheckStatus(Framebuffer framebuffer)
    {
        framebuffer.Bind(0);
        var status = GlContext.Gl.CheckFramebufferStatus(GLEnum.Framebuffer);
        Framebuffer.Unbind();
        return status;
    }

    private static void DrawFullscreenTriangle(Shader shader)
    {
        shader.Bind();
        DrawFullscreenTriangle();
        Shader.Unbind();
    }

    private static void DrawFullscreenTriangle()
    {
        var gl = GlContext.Gl;
        var vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.DrawArrays(GLEnum.Triangles, 0, 3);
        gl.BindVertexArray(0);
        gl.DeleteVertexArray(vao);
    }

    private static unsafe byte[] ReadPixels(Framebuffer framebuffer, int width, int height, int framebufferIndex)
    {
        var pixels = new byte[width * height * 4];

        framebuffer.Bind(framebufferIndex);
        fixed (byte* ptr = pixels)
        {
            GlContext.Gl.ReadPixels(0, 0, (uint)width, (uint)height, GLEnum.Rgba, GLEnum.UnsignedByte, ptr);
        }

        Framebuffer.Unbind();
        return pixels;
    }
}
