using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Renderer;
using MilkDrop.ShaderToy.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Rendering;

/// <summary>
/// P7 的错误面板与通道重绑：编译失败要能被 UI 定位到行，改 sampler 不该重编整个工程。
/// </summary>
[Collection("GL")]
public sealed class PipelineErrorTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    private const int Size = 32;

    [Fact]
    public void BrokenPass_IsSkippedButRecordedWithLineNumber()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var project = new ShaderToyProject();
        project.Passes.Add(new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            // 隐式 int → float：ANGLE 的 ESSL3 前端必报错（S2 实测）
            Code = "void mainImage(out vec4 c, in vec2 f) { float x = 1; c = vec4(x); }",
        });

        using var pipeline = new ShaderToyPipeline(project, new PipelineOptions
        {
            Width = Size,
            Height = Size,
            HalfFloat = true,
        });

        Assert.Equal(0, pipeline.PassCount);
        Assert.Null(pipeline.Output);

        PassCompileError error = Assert.Single(pipeline.CompileErrors);
        Assert.Equal("Image", error.PassName);
        Assert.NotNull(error.Line);
        Assert.True(error.Line > 0);
        Assert.False(string.IsNullOrWhiteSpace(error.LineText));
        Assert.NotNull(error.Source);

        output.WriteLine($"{error.PassName} 第 {error.Line} 行: {error.LineText}");
        Assert.Contains(pipeline.Notes, n => n.Contains("编译失败", StringComparison.Ordinal));
    }

    [Fact]
    public void OneBrokenBuffer_DoesNotStopTheImagePass()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var project = new ShaderToyProject();

        project.Passes.Add(new ShaderPass
        {
            Type = ShaderPassType.Buffer,
            Name = "Buffer A",
            Code = "void mainImage(out vec4 c, in vec2 f) { c = vec4(nope); }",
            Outputs = { new ShaderPassOutput { Id = "bufA", Channel = 0 } },
        });

        project.Passes.Add(new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = "void mainImage(out vec4 c, in vec2 f) { c = vec4(0.25, 0.5, 0.75, 1.0); }",
        });

        using var pipeline = new ShaderToyPipeline(project, new PipelineOptions
        {
            Width = Size,
            Height = Size,
            HalfFloat = true,
        });

        Assert.Equal(1, pipeline.PassCount);          // 只剩 image
        Assert.Single(pipeline.CompileErrors);
        Assert.Equal("Buffer A", pipeline.CompileErrors[0].PassName);
        Assert.NotNull(pipeline.Output);
    }

    [Fact]
    public void RefreshBindings_RebindsWithoutDuplicatingNotes()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var project = new ShaderToyProject();
        var pass = new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "Image",
            Code = "void mainImage(out vec4 c, in vec2 f) { c = texture(iChannel0, f / iResolution.xy); }",
        };

        // 指向一个不存在的图片：绑定阶段会写一条提示
        pass.Inputs.Add(new ShaderPassInput
        {
            Channel = 0,
            Type = ShaderInputType.Texture,
            Path = "no-such-texture.png",
        });

        project.Passes.Add(pass);

        var options = new PipelineOptions { Width = Size, Height = Size, HalfFloat = true };
        options.TextureSearchPaths.Add(Path.GetTempPath());

        using var pipeline = new ShaderToyPipeline(project, options);

        int notesAfterBuild = pipeline.Notes.Count;
        Assert.True(notesAfterBuild > 0);

        pass.Inputs[0].Sampler.Filter = SamplerFilter.Nearest;
        pass.Inputs[0].Sampler.Wrap = SamplerWrap.Repeat;
        pass.Inputs[0].Sampler.VFlip = !pass.Inputs[0].Sampler.VFlip;

        pipeline.RefreshBindings();
        pipeline.RefreshBindings();

        // 重绑两次不该把提示越堆越多
        Assert.Equal(notesAfterBuild, pipeline.Notes.Count);
        Assert.Empty(pipeline.CompileErrors);

        pipeline.Render(new GpuInput { Resolution = new System.Numerics.Vector3(Size, Size, 1f) });
        Assert.NotNull(pipeline.Output);
    }
}
