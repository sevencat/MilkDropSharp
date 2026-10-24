using MilkDrop.Rendering.Gles3;
using MilkDrop.ShaderToy.Compile;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;

// Silk.NET.OpenGLES 里也有 Shader（那是 GL 句柄类型），这里一律指我们自己的封装。
using GlShader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkDrop.ShaderToy.Tests.Compile;

/// <summary>
/// P2 的验收：转译结果必须在 ANGLE（GLES3）上真编译通过。
/// </summary>
/// <remarks>机器上没有可用离屏 GL 时整个类静默跳过（与 ProjectM 的渲染测试同一套 fixture）。</remarks>
[Collection("GL")]
public sealed class TranspileCompileTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public void Corpus_CompilesOnAngle()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var failures = new List<string>();
        int compiled = 0;

        foreach ((string name, ShaderPassType type, string code) in ShaderCorpus.Cases)
        {
            var pass = new ShaderPass { Type = type, Name = name, Code = code };
            ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(pass);

            try
            {
                using var shader = new GlShader();
                shader.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);
                compiled++;
                output.WriteLine($"OK   {name,-22} ({type})");
            }
            catch (ShaderException ex)
            {
                failures.Add($"""
                    === {name} ({type}) ===
                    {ex.Message}
                    --- notes ---
                    {string.Join("\n", result.Notes)}
                    --- source ---
                    {result.Source}
                    """);
            }
        }

        output.WriteLine($"编译通过 {compiled}/{ShaderCorpus.Cases.Count}");
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    [Fact]
    public void UpstreamDefaultShader_Compiles()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.DefaultShaderJson);
        ShaderPass image = Assert.IsType<ShaderPass>(project.ImagePass);

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(image, project.BuildCommonCode());

        using var shader = new GlShader();
        shader.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);
        output.WriteLine(result.Source);
    }

    [Fact]
    public void RealExportFlGyWc_ImagePassCompiles()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        // 这份 JSON 是被硬换行弄坏过的真实导出，修完之后 GLSL 必须仍然合法
        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(TestPaths.FlGyWcJson);
        ShaderPass image = Assert.IsType<ShaderPass>(project.ImagePass);

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(image, project.BuildCommonCode());

        using var shader = new GlShader();

        try
        {
            shader.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);
        }
        catch (ShaderException ex)
        {
            // 编译失败时把带行号的源码打出来，否则只有一句 "0:31: syntax error" 没法查
            string[] lines = result.Source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                output.WriteLine($"{i + 1,4}: {lines[i].TrimEnd('\r')}");
            }

            output.WriteLine(ex.Message);
            throw;
        }

        output.WriteLine(string.Join(Environment.NewLine, result.Notes));
    }

    [Fact]
    public void TranspiledSource_ExposesBuiltinUniformsToTheDriver()
    {
        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        var pass = new ShaderPass
        {
            Type = ShaderPassType.Image,
            Name = "gradient",
            Code = """
                void mainImage(out vec4 fragColor, in vec2 fragCoord)
                {
                    fragColor = vec4(fragCoord / iResolution.xy, iTime, 1.0);
                }
                """,
        };

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(pass);

        using var shader = new GlShader();
        shader.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);
        shader.Bind();

        // uniform 名字必须和 P3 要推送的一致，否则会被静默优化掉、画面变黑
        var gl = fixture.Gl;
        Assert.True(gl.GetUniformLocation(shader.ProgramId, "iResolution") >= 0, "iResolution 没出现在 program 里");
        Assert.True(gl.GetUniformLocation(shader.ProgramId, "iTime") >= 0, "iTime 没出现在 program 里");

        shader.Bind();
        shader.SetUniformFloat3("iResolution", new System.Numerics.Vector3(64f, 64f, 1f));
        shader.SetUniformFloat("iTime", 1.5f);
        GlShader.Unbind();
    }
}
