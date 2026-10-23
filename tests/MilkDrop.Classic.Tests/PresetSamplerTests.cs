using MilkDrop.Classic.Render.D3D9;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 预设自己声明的纹理 sampler（<c>sampler sampler_fw_clouds;</c> → <c>textures/clouds.jpg</c>）。
/// </summary>
/// <remarks>
/// 锁死两件事：装配时必须给它写死 <c>: register(sN)</c>（运行时才知道绑到哪一级），
/// 名字解析必须与 <c>plugin.cpp:2953-2994</c> 一致（<c>sampler_</c> + <c>XY_</c> 前缀规则）。
/// 之前缺这一步时，comp 采样未绑定阶段 → 整个屏幕全黑
/// （<c>Chrome - MilkDrop2077 001s/004</c>）。
/// </remarks>
public sealed class PresetSamplerTests
{
    private static string? Assemble(string body, out IReadOnlyList<string> notes, out string? error)
        => ShaderAssembler.Assemble(
            ShaderTemplates.Include!,
            body,
            MilkShaderKind.Comp,
            out error,
            ShaderCompatFix.All,
            out notes);

    [Fact]
    public void CustomSamplerGetsExplicitRegister()
    {
        const string body = """
            sampler sampler_fw_clouds;

            shader_body
            {
                ret = tex2D(sampler_fw_clouds, uv).xyz;
            }
            """;

        var source = Assemble(body, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("sampler sampler_fw_clouds : register(s0);", source, StringComparison.Ordinal);

        var table = ShaderConstantTable.Parse(source);
        Assert.True(table.TryGetSampler("sampler_fw_clouds", out var stage));
        Assert.Equal(0, stage);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void CustomSamplerComesAfterIncludeSamplers()
    {
        // include 里的 sampler 先按用法紧凑分配（sampler_main = s0），纹理 sampler 接在后面
        const string body = """
            sampler sampler_MilkDrop3_002b;

            shader_body
            {
                float3 col = tex2D(sampler_main, uv).xyz;
                ret = lerp(col, tex2D(sampler_MilkDrop3_002b, uv).xyz, 0.5);
            }
            """;

        var source = Assemble(body, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);

        var table = ShaderConstantTable.Parse(source);
        Assert.True(table.TryGetSampler("sampler_main", out var mainStage));
        Assert.True(table.TryGetSampler("sampler_MilkDrop3_002b", out var textureStage));
        Assert.Equal(mainStage + 1, textureStage);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void AliasedSamplerDeclarationIsResolved()
    {
        // 预处理器会把 `sampler iChannel0;` 展开成 `sampler sampler_stoy_london;`，
        // 所以声明里的别名必须换成真名，运行时才能按名字找 textures/stoy_london.jpg
        const string body = """
            #define iChannel0 sampler_stoy_london
            sampler iChannel0;

            shader_body
            {
                ret = tex2Dlod(iChannel0, float4(uv, 0, 0)).xyz;
            }
            """;

        var source = Assemble(body, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("sampler sampler_stoy_london : register(s0);", source, StringComparison.Ordinal);

        var table = ShaderConstantTable.Parse(source);
        Assert.True(table.TryGetSampler("sampler_stoy_london", out _));

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Theory]
    [InlineData("sampler_fw_clouds", "clouds", true, true)]           // FW：双线性 + wrap
    [InlineData("sampler_FC_cells", "cells", false, true)]            // FC：双线性 + clamp
    [InlineData("sampler_pw_noise_lq", "noise_lq", true, false)]      // PW：point + wrap
    [InlineData("sampler_cp_foo", "foo", false, false)]               // CP（反写）：point + clamp
    [InlineData("sampler_MilkDrop3_002b", "MilkDrop3_002b", true, true)]
    public void TextureSamplerNameIsParsed(string name, string expectedRoot, bool wrap, bool bilinear)
    {
        Assert.True(ShaderSamplers.TryResolveTextureSampler(name, out var info));
        Assert.Equal(expectedRoot, info.RootName);
        Assert.Equal(wrap, info.Wrap);
        Assert.Equal(bilinear, info.Bilinear);
    }

    [Theory]
    [InlineData("Evet + Geiss - Chrome - MilkDrop2077 001s.milk", "sampler_MilkDrop3_002b", "MilkDrop3_002b")]
    [InlineData("Evet + Geiss - Chrome - MilkDrop2077 004.milk", "sampler_fw_clouds", "clouds")]
    public void BlackScreenPresetsBindTheirTextureSampler(string fileName, string samplerName, string rootName)
    {
        var path = Path.Combine(ResourceLocator.PresetsDir, fileName);
        if (!File.Exists(path))
        {
            return;   // 没有原版资源目录时跳过
        }

        var state = new PresetState(new Random(1));
        Assert.True(state.Import(path, 0f));

        var source = ShaderAssembler.Assemble(
            ShaderTemplates.Include!,
            state.CompShadersText,
            MilkShaderKind.Comp,
            out var assembleError,
            ShaderCompatFix.All,
            out _);

        Assert.Null(assembleError);
        Assert.NotNull(source);

        // 装配结果里必须能查到阶段号，否则运行时会漏绑 → comp 输出全黑
        var table = ShaderConstantTable.Parse(source);
        Assert.True(table.TryGetSampler(samplerName, out _), samplerName + " 没有拿到 register(sN)");

        // 而且 textures/ 里确实有对应的图
        Assert.NotNull(PresetTextureCache.FindFile(ResourceLocator.TexturesDir, ResourceLocator.PresetsDir, rootName));

        var compiled = ShaderCompiler.CompilePixelShaderForVersion(source, state.CompPsVersion);
        Assert.True(compiled.Success, compiled.Error);
    }
}
