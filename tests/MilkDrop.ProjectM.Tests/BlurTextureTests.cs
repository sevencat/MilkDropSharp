using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using GLEnum = Silk.NET.OpenGLES.GLEnum;
using Texture = MilkDrop.Rendering.Gles3.Texture;
using TextureSamplerDescriptor = MilkDrop.ProjectM.Renderer.TextureSamplerDescriptor;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="BlurTexture.GetSafeBlurMinMaxValues"/> 的区间处理（不需要 GL）。</summary>
public sealed class BlurTextureRangeTests
{
    private static PerFrameContext CreateContext(out PresetState state)
    {
        state = new PresetState();
        var context = new PerFrameContext(state.GlobalRegisters);
        context.RegisterBuiltinVariables();
        return context;
    }

    [Fact]
    public void GetSafeBlurMinMaxValues_PassesThroughValidRanges()
    {
        PerFrameContext context = CreateContext(out _);
        context.Blur1Min.Value = 0.0;
        context.Blur1Max.Value = 1.0;
        context.Blur2Min.Value = 0.0;
        context.Blur2Max.Value = 0.5;
        context.Blur3Min.Value = 0.0;
        context.Blur3Max.Value = 0.25;

        Span<float> min = stackalloc float[3];
        Span<float> max = stackalloc float[3];
        BlurTexture.GetSafeBlurMinMaxValues(context, min, max);

        Assert.Equal([0f, 0f, 0f], min.ToArray());
        Assert.Equal(1f, max[0]);
        Assert.Equal(0.5f, max[1]);
        Assert.Equal(0.25f, max[2]);
    }

    [Fact]
    public void GetSafeBlurMinMaxValues_ClampsToPreviousLevel()
    {
        PerFrameContext context = CreateContext(out _);
        context.Blur1Min.Value = 0.2;
        context.Blur1Max.Value = 0.8;
        context.Blur2Min.Value = 0.1; // 比上一级更宽 → 收到 0.2
        context.Blur2Max.Value = 0.9; // 比上一级更宽 → 收到 0.8
        context.Blur3Min.Value = 0.0;
        context.Blur3Max.Value = 1.0;

        Span<float> min = stackalloc float[3];
        Span<float> max = stackalloc float[3];
        BlurTexture.GetSafeBlurMinMaxValues(context, min, max);

        Assert.Equal(0.2f, min[1], 6);
        Assert.Equal(0.8f, max[1], 6);
        Assert.Equal(0.2f, min[2], 6);
        Assert.Equal(0.8f, max[2], 6);
    }

    [Fact]
    public void GetSafeBlurMinMaxValues_WidensTooNarrowRange()
    {
        PerFrameContext context = CreateContext(out _);
        context.Blur1Min.Value = 0.5;
        context.Blur1Max.Value = 0.5;

        Span<float> min = stackalloc float[3];
        Span<float> max = stackalloc float[3];
        BlurTexture.GetSafeBlurMinMaxValues(context, min, max);

        // 4.1.8 的写法：avg - 0.05（不是 avg ± 0.05），见 projectMIssue.md I-27。
        Assert.Equal(0.45f, min[0], 6);
        Assert.Equal(0.45f, max[0], 6);
    }
}

/// <summary><see cref="BlurTexture"/> 的 GL 行为（需要离屏 GL 上下文）。</summary>
[Collection("GL")]
public sealed class BlurTextureTests(OffscreenGlFixture gl)
{
    [Fact]
    public void GetDescriptorsForBlurLevel_ReturnsVisibleTexturesOnly()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var blurTexture = new BlurTexture();
        blurTexture.SetRequiredBlurLevel(BlurLevel.Blur3);

        List<TextureSamplerDescriptor> descriptors = blurTexture.GetDescriptorsForBlurLevel(BlurLevel.Blur3);

        Assert.Equal(["blur1", "blur2", "blur3"], descriptors.Select(d => d.SamplerName));

        // 模糊纹理没有 texsize_* 。
        Assert.All(descriptors, d => Assert.Equal(string.Empty, d.TexSizeDeclaration()));

        Assert.Equal(2, blurTexture.GetDescriptorsForBlurLevel(BlurLevel.Blur2).Count);
        Assert.Single(blurTexture.GetDescriptorsForBlurLevel(BlurLevel.Blur1));
        Assert.Empty(blurTexture.GetDescriptorsForBlurLevel(BlurLevel.None));
    }

    [Fact]
    public void SetRequiredBlurLevel_NeverDecreases()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var blurTexture = new BlurTexture();
        blurTexture.SetRequiredBlurLevel(BlurLevel.Blur3);
        blurTexture.SetRequiredBlurLevel(BlurLevel.Blur1);

        Assert.Equal(BlurLevel.Blur3, blurTexture.CurrentBlurLevel);
    }

    [Fact]
    public void Update_AllocatesHalvedChainAndBlursContent()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        var glApi = GlContext.Gl;

        byte[] white = new byte[512 * 512 * 4];
        Array.Fill(white, (byte)255);

        using var source = new Texture("source", 512, 512, false);
        source.Bind(0);
        unsafe
        {
            fixed (byte* whitePointer = white)
            {
                glApi.TexSubImage2D(GLEnum.Texture2D, 0, 0, 0, 512, 512, GLEnum.Rgba, GLEnum.UnsignedByte, whitePointer);
            }
        }
        source.Unbind(0);

        using var blurTexture = new BlurTexture();
        blurTexture.SetRequiredBlurLevel(BlurLevel.Blur3);

        var state = new PresetState();
        var context = new PerFrameContext(state.GlobalRegisters);
        context.RegisterBuiltinVariables();
        context.Blur1EdgeDarken.Value = 0.25;
        context.Blur1Min.Value = 0.0;
        context.Blur1Max.Value = 1.0;
        context.Blur2Min.Value = 0.0;
        context.Blur2Max.Value = 1.0;
        context.Blur3Min.Value = 0.0;
        context.Blur3Max.Value = 1.0;

        blurTexture.Update(source, context);

        List<TextureSamplerDescriptor> descriptors = blurTexture.GetDescriptorsForBlurLevel(BlurLevel.Blur3);

        // 512 的主纹理 → blur0=256、blur1=128、blur2=64、blur3=32。
        Assert.Equal(128, descriptors[0].Texture!.Width);
        Assert.Equal(64, descriptors[1].Texture!.Width);
        Assert.Equal(32, descriptors[2].Texture!.Width);
        Assert.Equal(0, (int)glApi.GetError());

        // 全白源纹理，模糊后中心仍应是亮的。
        byte centerPixel = ReadCenterRedChannel(glApi, descriptors[0].Texture!);
        Assert.True(centerPixel > 200, "blur1 中心像素过暗: " + centerPixel);
    }

    /// <summary>把纹理挂到临时 FBO 上读回中心像素（<c>GetTexImage</c> 在 GLES 绑定里没有）。</summary>
    private static byte ReadCenterRedChannel(Silk.NET.OpenGLES.GL glApi, Texture texture)
    {
        uint framebuffer = glApi.GenFramebuffer();
        glApi.BindFramebuffer(GLEnum.Framebuffer, framebuffer);
        glApi.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D, texture.TextureId, 0);
        glApi.ReadBuffer(GLEnum.ColorAttachment0);

        var pixels = new byte[texture.Width * texture.Height * 4];
        unsafe
        {
            fixed (byte* pointer = pixels)
            {
                glApi.ReadPixels(0, 0, (uint)texture.Width, (uint)texture.Height, GLEnum.Rgba, GLEnum.UnsignedByte, pointer);
            }
        }

        glApi.BindFramebuffer(GLEnum.Framebuffer, 0);
        glApi.DeleteFramebuffer(framebuffer);

        int index = (texture.Height / 2 * texture.Width + texture.Width / 2) * 4;
        return pixels[index];
    }
}
