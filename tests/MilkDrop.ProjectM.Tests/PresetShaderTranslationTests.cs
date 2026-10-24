using HlslParser;

// 公共库里也有一个 GlslVersion（GLSL ES 版本标记），这里明确用转译器的那个。
using GlslVersion = HlslParser.GlslVersion;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// 预设 HLSL → GLES3 GLSL 的转译冒烟测试。
/// </summary>
/// <remarks>
/// P0 阶段先确认 <c>HlslParser</c> 的 <see cref="GlslVersion.Version300Es"/> 通道可用（ANGLE 只吃 GLES GLSL）。
/// 完整的预设着色器装配（<c>sampler_*</c> 剔除/注入、<c>shader_body</c> 包装）在 P3 由
/// <c>MilkdropShader</c> 完成，对应 C++ <c>MilkdropShader.cpp:596-688</c>。
/// </remarks>
public sealed class PresetShaderTranslationTests(ITestOutputHelper output)
{
    private const string WarpShaderSource = """
        uniform sampler2D sampler_main;
        uniform float4 _c0;

        float4 PS(float2 uv : TEXCOORD0, float2 uv_orig : TEXCOORD1, float2 rad_ang : TEXCOORD2) : COLOR
        {
            float4 tex = tex2D(sampler_main, uv);
            tex.rgb *= 1.2;
            tex.a = 1.0;
            return tex;
        }
        """;

    [Fact]
    public void WarpShader_TranslatesToGles300()
    {
        var translator = new HlslToGlslTranslator();
        var result = translator.Translate(new TranslationRequest
        {
            Source = WarpShaderSource,
            FileName = "preset_warp.hlsl",
            EntryPoint = "PS",
            Target = GlslTarget.FragmentShader,
            Version = GlslVersion.Version300Es,
        });

        foreach (var diagnostic in result.Diagnostics)
        {
            output.WriteLine(diagnostic.ToString());
        }

        Assert.True(result.Success, "转译失败，见诊断输出");
        Assert.Contains("#version 300 es", result.Glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("tex2D(", result.Glsl, StringComparison.Ordinal);
        Assert.Contains("texture(", result.Glsl, StringComparison.Ordinal);
        Assert.Contains("sampler_main", result.Glsl, StringComparison.Ordinal);
    }
}
