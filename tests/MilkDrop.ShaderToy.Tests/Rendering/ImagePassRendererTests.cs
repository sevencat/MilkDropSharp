using System.Numerics;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;
using Silk.NET.OpenGLES;
using Xunit;
using Xunit.Abstractions;

// Silk.NET.OpenGLES 里也有 Framebuffer（GL 句柄类型），这里指向我们的封装。
using CopyTexture = MilkDrop.Rendering.Gles3.CopyTexture;
using Framebuffer = MilkDrop.Rendering.Gles3.Framebuffer;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// P3 的端到端验证：转译 → 编译 → 画到 RGBA16F → 拷到 RGBA8 → 回读像素。
/// </summary>
/// <remarks>
/// 回读比"没抛异常"强得多：它能同时证明通道 uniform 生效、视口尺寸对、Y 轴没翻错。
/// 没有可用离屏 GL 时静默跳过。
/// </remarks>
[Collection("GL")]
public sealed class ImagePassRendererTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Size = 8;

    [Fact]
    public void Render_WritesFragCoordGradientWithBottomLeftOrigin()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var renderer = new ImagePassRenderer(Size, Size);
        renderer.Compile(MakePass("""
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
            }
            """));

        renderer.Render(MakeInput());

        byte[] pixels = ReadPixels(renderer, out int width, out int height);
        Assert.Equal(Size, width);
        Assert.Equal(Size, height);

        // gl_FragCoord 原点在左下：左下角接近黑，右上角接近白。
        // 如果哪一步翻了 Y，绿色通道的角就会互换，这条断言会直接炸。
        Assert.InRange(Red(pixels, width, 0, 0), 0, 40);
        Assert.InRange(Green(pixels, width, 0, 0), 0, 40);
        Assert.InRange(Red(pixels, width, width - 1, height - 1), 200, 255);
        Assert.InRange(Green(pixels, width, width - 1, height - 1), 200, 255);

        // 横向单调：x 增大红色递增（证明不是一张纯色）
        Assert.True(Red(pixels, width, 4, 0) > Red(pixels, width, 1, 0));
    }

    [Fact]
    public void Render_PushesTimeAndMouseUniforms()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var renderer = new ImagePassRenderer(Size, Size);
        renderer.Compile(MakePass("""
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = vec4(
                    iTime * 0.5,
                    iMouse.z > 0.0 ? 1.0 : 0.0,
                    iMouse.x / iResolution.x,
                    1.0);
            }
            """));

        GpuInput pressed = MakeInput();
        pressed.Time = 1.0f;
        pressed.Mouse = new Vector4(4f, 4f, 4f, 4f);

        renderer.Render(pressed);
        byte[] down = ReadPixels(renderer, out int width, out _);

        Assert.InRange(Red(down, width, 0, 0), 120, 135);   // iTime * 0.5 = 0.5
        Assert.InRange(Green(down, width, 0, 0), 250, 255); // iMouse.z > 0
        Assert.InRange(Blue(down, width, 0, 0), 120, 135); // iMouse.x / 8 = 0.5

        GpuInput released = MakeInput();
        released.Time = 1.0f;
        released.Mouse = new Vector4(4f, 4f, -4f, -4f); // 松开后 zw 取负

        renderer.Render(released);
        byte[] up = ReadPixels(renderer, out _, out _);

        Assert.InRange(Green(up, width, 0, 0), 0, 5);
    }

    [Fact]
    public void SetSize_RebuildsTargetAndKeepsRendering()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var renderer = new ImagePassRenderer(4, 4);
        renderer.Compile(MakePass("""
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = vec4(0.0, 1.0, 0.0, 1.0);
            }
            """));

        renderer.SetSize(16, 8);

        Assert.Equal(16, renderer.Width);
        Assert.Equal(8, renderer.Height);

        renderer.Render(MakeInput(width: 16, height: 8));

        byte[] pixels = ReadPixels(renderer, out int width, out int height);
        Assert.Equal(16, width);
        Assert.Equal(8, height);
        Assert.InRange(Green(pixels, width, width - 1, height - 1), 250, 255);
    }

    [Fact]
    public void Render_WithoutProgram_IsNoOp()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var renderer = new ImagePassRenderer(4, 4);
        Assert.False(renderer.HasProgram);

        renderer.Render(MakeInput(width: 4, height: 4)); // 不能抛
    }

    private static ShaderPass MakePass(string code) => new()
    {
        Type = ShaderPassType.Image,
        Name = "test",
        Code = code,
    };

    private static GpuInput MakeInput(int width = Size, int height = Size) => new()
    {
        Resolution = new Vector3(width, height, 1f),
        Date = GpuInput.MakeDate(new DateTime(2026, 10, 24, 12, 0, 0)),
    };

    /// <summary>把离屏结果拷进一个 RGBA8 FBO 再回读（与真实"上屏"路径一致）。</summary>
    private byte[] ReadPixels(ImagePassRenderer renderer, out int width, out int height)
    {
        var gl = fixture.Gl;
        width = renderer.Width;
        height = renderer.Height;

        using var screen = new Framebuffer(1);
        screen.CreateColorAttachment(0, 0);
        screen.SetSize(width, height);

        using var copy = new CopyTexture();
        screen.Bind(0);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        copy.Draw(renderer.Target);

        var pixels = new byte[width * height * 4];
        gl.ReadPixels(0, 0, (uint)width, (uint)height, GLEnum.Rgba, GLEnum.UnsignedByte, pixels);
        Framebuffer.Unbind();
        return pixels;
    }

    private static byte Red(byte[] pixels, int width, int x, int y) => pixels[((y * width) + x) * 4];

    private static byte Green(byte[] pixels, int width, int x, int y) => pixels[((y * width) + x) * 4 + 1];

    private static byte Blue(byte[] pixels, int width, int x, int y) => pixels[((y * width) + x) * 4 + 2];
}
