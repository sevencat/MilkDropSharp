using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.Rendering.Gles3;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// <see cref="MilkdropShader"/> 与 <see cref="PresetState"/>/<see cref="TextureManager"/> 的接线
/// （需要离屏 GL）。
/// </summary>
[Collection("GL")]
public sealed class MilkdropShaderPresetStateTests(OffscreenGlFixture gl) : IDisposable
{
    private const string WarpCode = """
        shader_body
        {
            float3 col = tex2D(sampler_main, uv).rgb;
            col += tex2D(sampler_noise_lq, uv_orig).rgb * 0.1;
            col += GetBlur1(uv).rgb * 0.05;
            ret = col * 0.98;
        }
        """;

    private readonly string _textureDir = Directory.CreateTempSubdirectory("milkdrop-preset-").FullName;

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
    public void LoadTexturesAndCompile_ResolvesMainBlurAndNoiseTextures()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);
        using var state = CreateState(textureManager);
        using var shader = new MilkdropShader(ShaderType.WarpShader);

        shader.LoadCode(WarpCode);
        shader.LoadTexturesAndCompile(state);

        // 代码里用了 GetBlur1 → 模糊级别 1，并写回 PresetState。
        Assert.Equal(BlurLevel.Blur1, shader.MaxBlurLevelRequired);
        Assert.Equal(BlurLevel.Blur1, state.BlurTexture.CurrentBlurLevel);
        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }

    [Fact]
    public void LoadVariables_SetsUniformsAndBindsTextures()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);
        using var state = CreateState(textureManager);
        using var shader = new MilkdropShader(ShaderType.WarpShader);
        var frame = new PerFrameContext(state.GlobalRegisters);
        frame.RegisterBuiltinVariables();

        shader.LoadCode(WarpCode);
        shader.LoadTexturesAndCompile(state);

        state.RenderContext.Time = 12.5f;
        state.RenderContext.Fps = 60;
        state.RenderContext.Frame = 100;
        state.RenderContext.Progress = 0.25f;
        state.AudioData.Bass = 0.5f;
        state.AudioData.Vol = 1.5f;
        state.FrameQVariables[0] = 0.125;
        state.FrameQVariables[31] = 2.0;

        frame.Decay.Value = 0.97;
        frame.Wrap.Value = 1;
        frame.Blur1Min.Value = 0;
        frame.Blur1Max.Value = 1;

        shader.LoadVariables(state, frame);

        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }

    [Fact]
    public void LoadTexturesAndCompile_ThrowsWithoutTextureManager()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var state = CreateState(textureManager: null);
        using var shader = new MilkdropShader(ShaderType.WarpShader);

        shader.LoadCode(WarpCode);

        Assert.Throws<InvalidOperationException>(() => shader.LoadTexturesAndCompile(state));
    }

    [Fact]
    public void PerPixelMesh_DrawsWithPresetWarpShader()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);
        using var state = CreateState(textureManager);
        state.WarpShaderVersion = 2;
        state.WarpShader = WarpCode;

        var frame = new PerFrameContext(state.GlobalRegisters);
        frame.RegisterBuiltinVariables();
        var pixel = new PerPixelContext(state.GlobalRegisters);
        pixel.RegisterBuiltinVariables();
        pixel.LoadStateReadOnlyVariables(state, frame);

        using var mesh = new PerPixelMesh();
        mesh.LoadWarpShader(state);
        mesh.CompileWarpShader(state);

        Assert.NotNull(mesh.CurrentWarpShader);

        mesh.Draw(state, frame, pixel);

        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }

    [Fact]
    public void PerPixelMesh_FallsBackToDefaultPipelineOnBrokenWarpShader()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using TextureManager textureManager = new([_textureDir]);
        using var state = CreateState(textureManager);
        state.WarpShaderVersion = 2;
        state.WarpShader = "shader_body { ret = unknown_symbol; }";

        var frame = new PerFrameContext(state.GlobalRegisters);
        frame.RegisterBuiltinVariables();
        var pixel = new PerPixelContext(state.GlobalRegisters);
        pixel.RegisterBuiltinVariables();

        using var mesh = new PerPixelMesh();
        mesh.LoadWarpShader(state);
        mesh.CompileWarpShader(state);

        Assert.Null(mesh.CurrentWarpShader);

        mesh.Draw(state, frame, pixel);

        Assert.Equal(0, (int)GlContext.Gl.GetError());
    }

    private static PresetState CreateState(TextureManager? textureManager)
    {
        var state = new PresetState();
        state.RenderContext.TextureManager = textureManager;
        state.RenderContext.ViewportSizeX = 640;
        state.RenderContext.ViewportSizeY = 480;
        state.RenderContext.PerPixelMeshX = 16;
        state.RenderContext.PerPixelMeshY = 12;
        state.RenderContext.AspectX = 4f / 3f;
        state.RenderContext.AspectY = 1f;
        state.RenderContext.InvAspectX = 0.75f;
        state.RenderContext.InvAspectY = 1f;
        state.MainTexture = new Texture("main", 64, 64, false);
        return state;
    }
}
