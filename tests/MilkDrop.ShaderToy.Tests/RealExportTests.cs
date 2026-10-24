using MilkDrop.ShaderToy.Compile;
using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;
using MilkDrop.ShaderToy.Tests.Rendering;
using Xunit;
using Xunit.Abstractions;

using GlShader = MilkDrop.Rendering.Gles3.Shader;

namespace MilkDrop.ShaderToy.Tests;

/// <summary>
/// 对 <c>res/shadertoy/stpreset/*.json</c> 里所有真实导出做回归。
/// </summary>
/// <remarks>
/// 用 MemberData 动态枚举目录：往目录里丢新的 JSON，重新跑测试就会自动带上，
/// 不需要改代码。每个作品至少要做到"能导入 + image pass 能编译"，坏掉即报错。
/// </remarks>
[Collection("GL")]
public sealed class RealExportTests(OffscreenGlFixture fixture, ITestOutputHelper output)
{
    public static TheoryData<string, string> ExportedProjects()
    {
        var data = new TheoryData<string, string>();

        if (!Directory.Exists(TestPaths.ShaderToyPresetDir))
        {
            return data;
        }

        foreach (string file in Directory.EnumerateFiles(TestPaths.ShaderToyPresetDir, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file), file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExportedProjects))]
    public void ExportedProject_ImportsAndImagePassCompiles(string name, string path)
    {
        ShaderToyProject project = ShaderToyJsonImporter.LoadFile(path);

        output.WriteLine($"{name}: {project.Metadata.Name} / id={project.Metadata.Id}");
        output.WriteLine($"passes: {string.Join(", ", project.Passes.Select(p => $"{p.Type}:{p.Name}"))}");

        foreach (string warning in project.Warnings)
        {
            output.WriteLine("提示: " + warning);
        }

        Assert.NotEmpty(project.Passes);

        if (project.ImagePass is null)
        {
            output.WriteLine("没有 image pass（sound-only 作品），P3 阶段跳过编译。");
            return;
        }

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(project.ImagePass, project.BuildCommonCode());

        if (!fixture.TryMakeCurrent())
        {
            output.WriteLine("跳过 GL 编译：没有可用的离屏 GL - " + fixture.SkipReason);
            return;
        }

        using var shader = new GlShader();

        try
        {
            shader.CompileProgram(ShaderToyTranspiler.FullscreenVertexSource, result.Source);
        }
        catch (MilkDrop.Rendering.Gles3.ShaderException ex)
        {
            string[] lines = result.Source.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                output.WriteLine($"{i + 1,4}: {lines[i].TrimEnd('\r')}");
            }

            output.WriteLine(ex.Message);
            throw;
        }

        output.WriteLine("编译通过");
    }
}
