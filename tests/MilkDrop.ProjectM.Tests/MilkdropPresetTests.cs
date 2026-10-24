using System.Text;
using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.Renderer;
using MilkdropPresetType = MilkDrop.ProjectM.MilkdropPreset.MilkdropPreset;
using MilkDrop.ProjectM.Rendering;
using MilkDrop.ProjectM.Tests.Rendering;
using Silk.NET.OpenGLES;
using Xunit;
using Framebuffer = MilkDrop.ProjectM.Renderer.Framebuffer;
using Texture = MilkDrop.ProjectM.Renderer.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary>预设名解析（不需要 GL）。</summary>
public sealed class MilkdropPresetPathTests
{
    [Fact]
    public void ParseFilename_TakesPartAfterLastSlash()
    {
        Assert.Equal("a.milk", MilkdropPresetType.ParseFilename("/x/y/a.milk"));
        Assert.Equal("a.milk", MilkdropPresetType.ParseFilename("a.milk".Insert(0, "/")));
    }

    [Fact]
    public void ParseFilename_NoSlashReturnsEmpty()
    {
        // 上游只认 '/'，无分隔符时返回空串（MilkdropPreset.cpp:305-315）。
        Assert.Equal(string.Empty, MilkdropPresetType.ParseFilename("a.milk"));
        Assert.Equal(string.Empty, MilkdropPresetType.ParseFilename(@"C:\presets\a.milk"));
    }

    [Fact]
    public void ParseFilename_TrailingSlashReturnsEmpty()
    {
        Assert.Equal(string.Empty, MilkdropPresetType.ParseFilename("/x/y/"));
    }
}

/// <summary>
/// 预设完整管线（P4）：初始化 + 连跑几帧 + 像素回读。
/// </summary>
/// <remarks>
/// 这些测试需要离屏 GL；没有可用上下文时静默跳过（xunit 2.x 无动态 Skip）。
/// </remarks>
[Collection("GL")]
public sealed class MilkdropPresetRenderTests(OffscreenGlFixture gl) : IDisposable
{
    private const int Width = 32;

    private const int Height = 24;

    private readonly string _textureDir = Directory.CreateTempSubdirectory("milkdrop-preset-tex-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_textureDir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// 只有 composite shader、输出纯红的预设：验证 warp → 翻转 → composite → 双缓冲交换整条链路。
    /// </summary>
    [Fact]
    public void RenderFrame_CompositeShaderOutputsRed()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        const string preset = """
            [preset00]
            MILKDROP_PRESET_VERSION=201
            PSVERSION_WARP=2
            PSVERSION_COMP=2
            warp_1=`shader_body { ret = tex2D(sampler_main, uv).xyz; }
            comp_1=`shader_body { ret = float3(1.0, 0.0, 0.0); }
            """;

        using TextureManager textureManager = new([_textureDir]);
        using var presetInstance = CreatePreset(preset);
        RenderContext renderContext = CreateRenderContext(textureManager);

        presetInstance.Initialize(renderContext);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());

        RenderFrames(presetInstance, renderContext, 2);

        (byte r, byte g, byte b, byte a) = ReadPixel(presetInstance.OutputTexture(), Width / 2, Height / 2);

        Assert.True(r > 200, $"红通道过低: r={r}");
        Assert.True(g < 64, $"绿通道过高: g={g}");
        Assert.True(b < 64, $"蓝通道过高: b={b}");
        Assert.Equal(255, a);
    }

    /// <summary>
    /// 没有 composite shader 的老预设走 VideoEcho + gamma 路径，且会多翻一次 Y。
    /// </summary>
    [Fact]
    public void RenderFrame_NoCompositeShaderUsesVideoEcho()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        const string preset = """
            [preset00]
            MILKDROP_PRESET_VERSION=100
            fGammaAdj=2.0
            fVideoEchoAlpha=0
            """;

        using TextureManager textureManager = new([_textureDir]);
        using var presetInstance = CreatePreset(preset);
        RenderContext renderContext = CreateRenderContext(textureManager);

        presetInstance.Initialize(renderContext);
        RenderFrames(presetInstance, renderContext, 2);

        // 没有 warp shader 时输入是黑的，VideoEcho 采样到的也就是黑的——这里只验证
        // 老式路径能跑通、帧缓冲正常交换、GL 状态干净。
        Assert.NotNull(presetInstance.OutputTexture());
        Assert.Equal(Width, presetInstance.OutputTexture()!.Width);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    /// <summary>首帧不画运动矢量，第二帧起才画；整个过程不应产生 GL 错误。</summary>
    [Fact]
    public void RenderFrame_WithMotionVectorsKeepsGlClean()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        const string preset = """
            [preset00]
            MILKDROP_PRESET_VERSION=201
            PSVERSION_WARP=2
            PSVERSION_COMP=2
            nMotionVectorsX=12
            nMotionVectorsY=9
            mv_a=1.0
            mv_l=0.9
            mv_r=1.0
            mv_g=1.0
            mv_b=1.0
            warp_1=`shader_body { ret = tex2D(sampler_main, uv).xyz; }
            comp_1=`shader_body { ret = tex2D(sampler_main, uv).xyz; }
            """;

        using TextureManager textureManager = new([_textureDir]);
        using var presetInstance = CreatePreset(preset);
        RenderContext renderContext = CreateRenderContext(textureManager);

        presetInstance.Initialize(renderContext);
        RenderFrames(presetInstance, renderContext, 3);

        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    /// <summary>改尺寸后要重建 framebuffer 与 UV 贴图，并且下一帧重新当作"首帧"。</summary>
    [Fact]
    public void RenderFrame_ResizeRebuildsTargets()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        const string preset = """
            [preset00]
            MILKDROP_PRESET_VERSION=201
            PSVERSION_WARP=2
            PSVERSION_COMP=2
            comp_1=`shader_body { ret = float3(0.0, 1.0, 0.0); }
            """;

        using TextureManager textureManager = new([_textureDir]);
        using var presetInstance = CreatePreset(preset);
        RenderContext renderContext = CreateRenderContext(textureManager);

        presetInstance.Initialize(renderContext);
        RenderFrames(presetInstance, renderContext, 1);

        renderContext.ViewportSizeX = 48;
        renderContext.ViewportSizeY = 32;
        RenderFrames(presetInstance, renderContext, 1);

        Assert.Equal(48, presetInstance.OutputTexture()!.Width);
        Assert.Equal(32, presetInstance.OutputTexture()!.Height);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    /// <summary>自定义波形与形状（P5）能画进画面，且全程无 GL 错误。</summary>
    [Fact]
    public void RenderFrame_DrawsCustomShapeAndWaveform()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        const string preset = """
            [preset00]
            MILKDROP_PRESET_VERSION=201
            PSVERSION_WARP=2
            PSVERSION_COMP=2
            nWaveMode=1
            wave_a=1.0
            shapecode_0_enabled=1
            shapecode_0_sides=24
            shapecode_0_rad=0.3
            shapecode_0_r=1.0
            shapecode_0_g=0.0
            shapecode_0_b=0.0
            shapecode_0_a=1.0
            shapecode_0_r2=1.0
            shapecode_0_g2=0.0
            shapecode_0_b2=0.0
            shapecode_0_a2=1.0
            wavecode_0_enabled=1
            wavecode_0_samples=32
            wavecode_0_r=0.0
            wavecode_0_g=0.0
            wavecode_0_b=1.0
            wavecode_0_a=1.0
            wave_0_per_point1=`x = 0.5 + value1 * 0.5; y = 0.5 + value2 * 0.5;
            comp_1=`shader_body { ret = tex2D(sampler_main, uv).xyz; }
            """;

        using TextureManager textureManager = new([_textureDir]);
        using var presetInstance = CreatePreset(preset);
        RenderContext renderContext = CreateRenderContext(textureManager);

        presetInstance.Initialize(renderContext);
        RenderFrames(presetInstance, renderContext, 2);

        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());

        (byte r, byte g, byte b, byte _) = ReadPixel(presetInstance.OutputTexture(), Width / 2, Height / 2);
        Assert.True(r > 100, $"形状中心应为红色: {r},{g},{b}");
    }

    /// <summary>
    /// 扩展的 q33..q64 既能在 per-frame 代码里赋值，也能传到 shader 里（表现为画面颜色）。
    /// </summary>
    [Fact]
    public void RenderFrame_ExtendedQVariablesReachShader()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        const string preset = """
            [preset00]
            MILKDROP_PRESET_VERSION=201
            PSVERSION_WARP=2
            PSVERSION_COMP=2
            per_frame_1=q33 = 1.0;
            per_frame_2=q64 = 0.5;
            comp_1=`shader_body { ret = float3(q33, 0.0, q64); }
            """;

        using TextureManager textureManager = new([_textureDir]);
        using var presetInstance = CreatePreset(preset);
        RenderContext renderContext = CreateRenderContext(textureManager);

        presetInstance.Initialize(renderContext);
        RenderFrames(presetInstance, renderContext, 2);

        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());

        (byte r, byte g, byte b, byte _) = ReadPixel(presetInstance.OutputTexture(), Width / 2, Height / 2);

        Assert.True(r > 200, $"q33 应驱动红通道: r={r}");
        Assert.True(g < 32, $"绿通道应为 0: g={g}");
        Assert.InRange(b, 100, 160);
    }

    private static MilkdropPresetType CreatePreset(string presetText)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(presetText));
        return new MilkdropPresetType(stream);
    }

    private static RenderContext CreateRenderContext(TextureManager textureManager)
    {
        return new RenderContext
        {
            ViewportSizeX = Width,
            ViewportSizeY = Height,
            AspectX = 1.0f,
            AspectY = 1.0f,
            InvAspectX = 1.0f,
            InvAspectY = 1.0f,
            PerPixelMeshX = 8,
            PerPixelMeshY = 6,
            Time = 1.0f,
            Fps = 60.0f,
            Progress = 0.1f,
            TextureManager = textureManager,
        };
    }

    private static void RenderFrames(MilkdropPresetType presetInstance, RenderContext renderContext, int frames)
    {
        var audioData = new FrameAudioData
        {
            Bass = 1.0f,
            BassAtt = 1.0f,
            Mid = 1.0f,
            MidAtt = 1.0f,
            Treb = 1.0f,
            TrebAtt = 1.0f,
            Vol = 1.0f,
            VolAtt = 1.0f,
        };

        for (var frame = 0; frame < frames; frame++)
        {
            renderContext.Frame++;
            renderContext.Time += 1.0f / 60.0f;

            presetInstance.RenderFrame(audioData, renderContext);
        }
    }

    /// <summary>把输出纹理挂到临时 FBO 上回读一个像素。</summary>
    private unsafe (byte R, byte G, byte B, byte A) ReadPixel(Texture? texture, int x, int y)
    {
        Assert.NotNull(texture);

        GL glApi = gl.Gl;
        using var framebuffer = new Framebuffer(1);
        framebuffer.Bind(0);
        glApi.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0, GLEnum.Texture2D,
            texture!.TextureId, 0);

        byte* pixel = stackalloc byte[4];
        glApi.ReadPixels(x, y, 1u, 1u, GLEnum.Rgba, GLEnum.UnsignedByte, pixel);

        glApi.BindFramebuffer(GLEnum.Framebuffer, 0);

        return (pixel[0], pixel[1], pixel[2], pixel[3]);
    }
}
