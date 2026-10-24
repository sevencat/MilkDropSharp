using System.Numerics;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// P4/P5 的端到端验证：pass 图执行顺序、buffer ping-pong、通道绑定、buffer 分辨率。
/// </summary>
/// <remarks>
/// 全部走"渲染 → 拷到 RGBA8 → 回读像素"，因为这样才能证明通道真的绑对了、pass 真的按顺序跑了。
/// 没有可用离屏 GL 时静默跳过。
/// </remarks>
[Collection("GL")]
public sealed class PipelineTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Size = 8;

    [Fact]
    public void BufferPass_FeedsImagePass()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        ShaderToyProject project = Build(
            MakePass(
                ShaderPassType.Buffer,
                "Buffer A",
                "A",
                "void mainImage(out vec4 c, in vec2 f) { c = vec4(0.25, 0.5, 0.75, 1.0); }"),
            MakePass(
                ShaderPassType.Image,
                "Image",
                "img",
                "void mainImage(out vec4 c, in vec2 f) { c = texture(iChannel0, f / iResolution.xy); }",
                Input(0, ShaderInputType.Buffer, "A")));

        using var pipeline = new ShaderToyPipeline(project, Options());
        pipeline.Render(MakeInput());

        byte[] pixels = GlReadback.Read(fixture, pipeline.Output, Size, Size);

        Assert.InRange(GlReadback.Red(pixels, Size, 3, 3), 60, 68);   // 0.25
        Assert.InRange(GlReadback.Green(pixels, Size, 3, 3), 124, 131); // 0.5
        Assert.InRange(GlReadback.Blue(pixels, Size, 3, 3), 187, 195);  // 0.75
        output.WriteLine(string.Join(Environment.NewLine, pipeline.Notes));
    }

    [Fact]
    public void BufferPass_PingPongAccumulatesAcrossFrames()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // 自己读自己 + 累加：只有 ping-pong 正确时才可能得到 0.1 / 0.2 / 0.3
        ShaderToyProject project = Build(
            MakePass(
                ShaderPassType.Buffer,
                "Buffer A",
                "A",
                """
                void mainImage(out vec4 c, in vec2 f)
                {
                    c = texture(iChannel0, f / iResolution.xy) + vec4(0.1, 0.0, 0.0, 0.0);
                }
                """,
                Input(0, ShaderInputType.Buffer, "A")),
            MakePass(
                ShaderPassType.Image,
                "Image",
                "img",
                "void mainImage(out vec4 c, in vec2 f) { c = texture(iChannel0, f / iResolution.xy); }",
                Input(0, ShaderInputType.Buffer, "A")));

        using var pipeline = new ShaderToyPipeline(project, Options());

        // 每加一次 0.1，红色就多 25.5
        foreach (int frame in new[] { 1, 2, 3 })
        {
            pipeline.Render(MakeInput());
            byte[] pixels = GlReadback.Read(fixture, pipeline.Output, Size, Size);
            byte red = GlReadback.Red(pixels, Size, 3, 3);

            output.WriteLine($"frame {frame}: red={red}");
            Assert.InRange(red, (frame * 25) - 3, (frame * 26) + 2);
        }
    }

    [Fact]
    public void BufferResolution_FixedAndViewportReachTheShader()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // buffer 把自己的 iResolution 写出来；image 再把自己的 iResolution.x 塞进 alpha
        ShaderToyProject project = Build(
            MakePass(
                ShaderPassType.Buffer,
                "Buffer A",
                "A",
                "void mainImage(out vec4 c, in vec2 f) { c = vec4(iResolution.xy / 256.0, 0.0, 1.0); }"),
            MakePass(
                ShaderPassType.Image,
                "Image",
                "img",
                """
                void mainImage(out vec4 c, in vec2 f)
                {
                    vec4 buffer = texture(iChannel0, vec2(0.5));
                    c = vec4(buffer.rgb, iResolution.x / 256.0);
                }
                """,
                Input(0, ShaderInputType.Buffer, "A")));

        var options = Options();
        options.BufferResolution = new BufferResolution
        {
            Mode = BufferResolutionMode.Fixed,
            FixedWidth = 64,
            FixedHeight = 16,
        };

        using var pipeline = new ShaderToyPipeline(project, options);
        pipeline.Render(MakeInput());

        byte[] pixels = GlReadback.Read(fixture, pipeline.Output, Size, Size);

        Assert.InRange(GlReadback.Red(pixels, Size, 3, 3), 61, 67);   // 64/256 = 0.25
        Assert.InRange(GlReadback.Green(pixels, Size, 3, 3), 13, 19); // 16/256 = 0.0625
        Assert.InRange(GlReadback.Alpha(pixels, Size, 3, 3), 5, 11);  // image 自己仍是 8x8 → 8/256
        output.WriteLine(pipeline.BufferResolutionDescription);
    }

    [Fact]
    public void KeyboardChannel_IsBoundAndUpdates()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        ShaderToyProject project = Build(
            MakePass(
                ShaderPassType.Image,
                "Image",
                "img",
                """
                void mainImage(out vec4 c, in vec2 f)
                {
                    // 键盘纹理是 256x3，第 0 行 = 按住
                    c = vec4(texture(iChannel0, vec2((65.0 + 0.5) / 256.0, 0.5 / 3.0)).x, 0.0, 0.0, 1.0);
                }
                """,
                Input(0, ShaderInputType.Keyboard)));

        using var pipeline = new ShaderToyPipeline(project, Options());

        pipeline.Keyboard.SetKey(65, true); // 'A'
        pipeline.Render(MakeInput());
        byte[] pressed = GlReadback.Read(fixture, pipeline.Output, Size, Size);
        Assert.InRange(GlReadback.Red(pressed, Size, 3, 3), 250, 255);

        pipeline.Keyboard.SetKey(65, false);
        pipeline.Render(MakeInput());
        byte[] released = GlReadback.Read(fixture, pipeline.Output, Size, Size);
        Assert.InRange(GlReadback.Red(released, Size, 3, 3), 0, 5);
    }

    [Fact]
    public void SoundPass_IsCompiledButNotPartOfTheImageGraph()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.FlGyWcJson);

        using var pipeline = new ShaderToyPipeline(project, Options());
        pipeline.Render(MakeInput());

        Assert.Equal(1, pipeline.PassCount); // image 在 pass 图里，sound 不在
        Assert.Equal(0, pipeline.BufferCount);
        Assert.True(pipeline.ProjectHasSoundPass);
        Assert.NotNull(pipeline.Output);

        Assert.NotNull(pipeline.Sound);
        Assert.Contains(pipeline.Notes, n => n.Contains("mainSound", StringComparison.Ordinal));
        output.WriteLine(string.Join(Environment.NewLine, pipeline.Notes));
    }

    [Fact]
    public void MusicChannel_BindsTheAudioTexture()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // v = 0.75 → 第 1 行（波形）；没有音频时波形是 0 → 0.5 灰
        ShaderToyProject project = Build(
            MakePass(
                ShaderPassType.Image,
                "Image",
                "img",
                "void mainImage(out vec4 c, in vec2 f) { c = texture(iChannel0, vec2(0.5, 0.75)); }",
                Input(0, ShaderInputType.Music)));

        using var pipeline = new ShaderToyPipeline(project, Options());
        pipeline.Render(MakeInput());

        byte[] pixels = GlReadback.Read(fixture, pipeline.Output, Size, Size);

        // 黑占位会得到 0；绑到 music 纹理才可能是 0.5 灰
        Assert.InRange(GlReadback.Red(pixels, Size, 3, 3), 122, 133);
        Assert.Equal(512, pipeline.Music.Texture.Width);
    }

    [Fact]
    public void RealExport73ySRh_RunsThreeFrames()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // 这个作品是 Image + Common + Buffer A，P4 之后才真正能出图
        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.ShaderToyPresetDir + "\\73ySRh.json");

        using var pipeline = new ShaderToyPipeline(project, Options(width: 32, height: 32));

        foreach (string note in pipeline.Notes)
        {
            output.WriteLine("note: " + note);
        }

        Assert.Equal(1, pipeline.BufferCount);

        for (var frame = 0; frame < 3; frame++)
        {
            pipeline.Render(MakeInput(32));
        }

        byte[] pixels = GlReadback.Read(fixture, pipeline.Output, 32, 32);
        Assert.Equal(32 * 32 * 4, pixels.Length);

        // 画面不能全黑：能画出东西才说明 buffer A 的结果真的进了 image pass
        int maxChannel = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            maxChannel = Math.Max(maxChannel, Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])));
        }

        output.WriteLine($"最大通道值: {maxChannel}");
        Assert.True(maxChannel > 4, "画面全黑，buffer pass 的结果没进 image pass");

        output.WriteLine(string.Join(Environment.NewLine, pipeline.Notes));
    }

    private static ShaderToyProject Build(params ShaderPass[] passes)
    {
        var project = new ShaderToyProject
        {
            Version = "0.1",
            Metadata = new ShaderToyMetadata { Id = "test", Name = "pipeline" },
        };

        project.Passes.AddRange(passes);
        return project;
    }

    private static ShaderPass MakePass(
        ShaderPassType type,
        string name,
        string id,
        string code,
        params ShaderPassInput[] inputs)
    {
        var pass = new ShaderPass { Type = type, Name = name, Code = code };
        pass.Outputs.Add(new ShaderPassOutput { Id = id, Channel = 0 });
        pass.Inputs.AddRange(inputs);
        return pass;
    }

    private static ShaderPassInput Input(
        int channel,
        ShaderInputType type,
        string? id = null,
        SamplerFilter filter = SamplerFilter.Linear) => new()
        {
            Channel = channel,
            Type = type,
            Id = id,
            Sampler = new ShaderInputSampler { Filter = filter, Wrap = SamplerWrap.Clamp },
        };

    private static PipelineOptions Options(int width = Size, int height = Size) => new()
    {
        Width = width,
        Height = height,
        HalfFloat = true,
        Texture3DSupported = true,
    };

    private static GpuInput MakeInput(int size = Size) => new()
    {
        Resolution = new Vector3(size, size, 1f),
        Time = 1f,
        Date = GpuInput.MakeDate(new DateTime(2026, 10, 24, 12, 0, 0)),
    };
}
