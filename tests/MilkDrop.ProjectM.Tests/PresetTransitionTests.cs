using System.Text;
using MilkDrop.ProjectM.Audio;
using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Tests.Rendering;
using Silk.NET.OpenGLES;
using Xunit;
using MilkdropPresetType = MilkDrop.ProjectM.MilkdropPreset.MilkdropPreset;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// P6：内置转场着色器与 <c>PresetTransition</c>，以及待机预设。
/// </summary>
[Collection("GL")]
public sealed class PresetTransitionTests(OffscreenGlFixture gl) : IDisposable
{
    private const string SimplePreset = """
        [preset00]
        MILKDROP_PRESET_VERSION=201
        PSVERSION_WARP=2
        PSVERSION_COMP=2
        warp_1=`shader_body { ret = tex2D(sampler_main, uv).xyz; }
        comp_1=`shader_body { ret = float3(0.2, 0.4, 0.8); }
        """;

    private readonly string _textureDir = Directory.CreateTempSubdirectory("milkdrop-trans-tex-").FullName;

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

    [Fact]
    public void TransitionShaderManagerCompilesAllSixShaders()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var manager = new TransitionShaderManager();

        Assert.Equal(6, manager.Count);
        Assert.NotNull(manager.RandomTransition());
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    [Fact]
    public void TransitionDrawsBothPresets()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);
        using MilkdropPresetType oldPreset = CreatePreset();
        using MilkdropPresetType newPreset = CreatePreset();

        RenderContext renderContext = CreateRenderContext(textureManager);
        oldPreset.Initialize(renderContext);
        newPreset.Initialize(renderContext);

        var audioData = new FrameAudioData { Bass = 1, Mid = 1, Treb = 1, BassAtt = 1, MidAtt = 1, TrebAtt = 1, Vol = 1 };

        oldPreset.RenderFrame(audioData, renderContext);
        newPreset.RenderFrame(audioData, renderContext);

        using var shaderManager = new TransitionShaderManager();
        using var transition = new PresetTransition(shaderManager.RandomTransition(), 5.0);

        Assert.False(transition.IsDone());

        transition.Draw(oldPreset, newPreset, renderContext, audioData);

        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    [Fact]
    public void ZeroDurationTransitionIsImmediatelyDone()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var manager = new TransitionShaderManager();
        using var transition = new PresetTransition(manager.RandomTransition(), 0.0);

        Assert.True(transition.IsDone());
    }

    [Fact]
    public void IdlePresetInitializesAndRenders()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);
        using MilkdropPresetType idlePreset = IdlePresets.Allocate();

        // 待机预设要用两张内嵌纹理。
        Assert.Contains("shapecode_0_image=idlem", IdlePresets.PresetText, StringComparison.Ordinal);
        Assert.Contains("shapecode_1_image=idleheadphones", IdlePresets.PresetText, StringComparison.Ordinal);

        RenderContext renderContext = CreateRenderContext(textureManager);
        idlePreset.Initialize(renderContext);

        var audioData = new FrameAudioData { Bass = 1, Mid = 1, Treb = 1, BassAtt = 1, MidAtt = 1, TrebAtt = 1, Vol = 1 };
        idlePreset.RenderFrame(audioData, renderContext);
        idlePreset.RenderFrame(audioData, renderContext);

        Assert.NotNull(idlePreset.OutputTexture());
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    private static MilkdropPresetType CreatePreset()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SimplePreset));
        return new MilkdropPresetType(stream);
    }

    private static RenderContext CreateRenderContext(TextureManager textureManager)
    {
        return new RenderContext
        {
            ViewportSizeX = 32,
            ViewportSizeY = 24,
            AspectX = 1.0f,
            AspectY = 1.0f,
            InvAspectX = 1.0f,
            InvAspectY = 1.0f,
            PerPixelMeshX = 8,
            PerPixelMeshY = 6,
            Time = 1.0f,
            Fps = 60.0f,
            TextureManager = textureManager,
        };
    }
}
