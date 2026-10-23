using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>GenWarpPShaderText</c> / <c>GenCompPShaderText</c>（plugin.cpp:8782-8847）：
/// 它们把默认模板里<b>最后一个</b> <c>{</c> 之后的正文换成 MilkDrop 1 固定效果的 ps_2_0 等价实现。
/// </summary>
public sealed class ShaderTextTests(ITestOutputHelper output)
{
    private const char Lf = EelPreprocessor.LinefeedControlChar;

    [Fact]
    public void WarpTextReplacesBodyWithDecayAndSampler()
    {
        const string template = "shader_body\n{\n    ret = 0; //REPLACED\n}\n";

        var text = ShaderTextGenerator.GenWarpPShaderText(template, 0.98f, wrap: true);

        var expected = string.Concat(
            "shader_body\n{", Lf,
            "    // sample previous frame", Lf,
            "    ret = tex2D( sampler_main, uv ).xyz;", Lf,
            Lf,
            "    // darken (decay) over time", Lf,
            "    ret *= 0.98; //or try: ret -= 0.004;", Lf,
            "}", Lf);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("REPLACED", text);
    }

    [Fact]
    public void WarpTextUsesPointSamplerWhenWrapDisabled()
    {
        const string template = "shader_body{ }\n";

        var text = ShaderTextGenerator.GenWarpPShaderText(template, 0.95f, wrap: false);

        Assert.Contains("tex2D( sampler_fc_main, uv )", text);
        Assert.DoesNotContain("tex2D( sampler_main, uv )", text);
        Assert.Contains("ret *= 0.95;", text);
    }

    [Fact]
    public void CompTextEmitsVideoEchoWhenAlphaPositive()
    {
        const string template = "shader_body\n{\n}\n";

        var text = ShaderTextGenerator.GenCompPShaderText(
            template,
            brightness: 2f,
            veAlpha: 0.5f,
            veZoom: 2f,
            veOrient: 3,
            hueShader: 0f,
            brighten: false,
            darken: false,
            solarize: false,
            invert: false);

        // 1/zoom = 0.500；orient 3 → 翻 X 又翻 Y
        Assert.Contains("    float2 uv_echo = (uv - 0.5)*0.500*float2(-1,-1) + 0.5;" + Lf, text);
        Assert.Contains("    ret = lerp( tex2D(sampler_main, uv).xyz, " + Lf, text);
        Assert.Contains("                tex2D(sampler_main, uv_echo).xyz, " + Lf, text);
        Assert.Contains("                0.50 " + Lf, text);
        Assert.Contains("); //video echo" + Lf, text);
        Assert.Contains("    ret *= 2.00; //gamma" + Lf, text);
        Assert.DoesNotContain("hue_shader", text);
    }

    [Fact]
    public void CompTextSkipsEchoWhenAlphaNearZero()
    {
        const string template = "shader_body\n{\n}\n";

        var text = ShaderTextGenerator.GenCompPShaderText(template, 1f, 0f, 2f, 0, 0f, false, false, false, false);

        Assert.Contains("    ret = tex2D(sampler_main, uv).xyz;" + Lf, text);
        Assert.DoesNotContain("uv_echo", text);
    }

    [Fact]
    public void CompTextAppliesHueAndColourEffectsInOrder()
    {
        const string template = "shader_body\n{\n}\n";

        var text = ShaderTextGenerator.GenCompPShaderText(
            template,
            brightness: 1f,
            veAlpha: 0f,
            veZoom: 2f,
            veOrient: 0,
            hueShader: 0.25f,
            brighten: true,
            darken: true,
            solarize: true,
            invert: true);

        var hue = text.IndexOf("ret *= 0.75 + 0.25*hue_shader;", StringComparison.Ordinal);
        var brighten = text.IndexOf("ret = sqrt(ret);", StringComparison.Ordinal);
        var darken = text.IndexOf("ret *= ret;", StringComparison.Ordinal);
        var solarize = text.IndexOf("ret = ret*(1-ret)*4;", StringComparison.Ordinal);
        var invert = text.IndexOf("ret = 1 - ret;", StringComparison.Ordinal);

        Assert.True(hue >= 0);
        Assert.True(hue < brighten);
        Assert.True(brighten < darken);
        Assert.True(darken < solarize);
        Assert.True(solarize < invert);
    }

    [Fact]
    public void FullHueShaderMultipliesConstant()
    {
        const string template = "shader_body\n{\n}\n";

        var text = ShaderTextGenerator.GenCompPShaderText(template, 1f, 0f, 1f, 0, 1.5f, false, false, false, false);

        Assert.Contains("    ret *= hue_shader; //old hue shader effect" + Lf, text);
    }

    [Fact]
    public void TemplateWithoutBraceIsReturnedUnchanged()
    {
        const string template = "no body here";

        Assert.Equal(template, ShaderTextGenerator.GenWarpPShaderText(template, 0.5f, true));
        Assert.Equal(template, ShaderTextGenerator.GenCompPShaderText(template, 1f, 0f, 1f, 0, 0f, false, false, false, false));
    }

    [Fact]
    public void PresetWithoutShaderGetsGeneratedDefault()
    {
        if (ShaderTemplates.DefaultWarpPixelShader is null)
        {
            output.WriteLine("跳过：找不到 data/warp_ps.fx");
            return;
        }

        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "fDecay=0.9",
            "fGammaAdj=1.5");

        var state = new PresetState(new Random(1));
        Assert.True(state.Import(path, 0f));

        Assert.Contains("// sample previous frame", state.WarpShadersText);
        Assert.Contains("ret *= 0.90;", state.WarpShadersText);
        Assert.Contains("//gamma", state.CompShadersText);
        Assert.Contains("ret *= 1.50;", state.CompShadersText);
    }

    [Fact]
    public void PresetWarpShaderTextIsNotOverwritten()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "warp_1=ret = tex2D(sampler_main, uv).xyz * 2;");

        var state = new PresetState(new Random(2));
        Assert.True(state.Import(path, 0f));

        Assert.Contains("* 2;", state.WarpShadersText);
        Assert.DoesNotContain("// sample previous frame", state.WarpShadersText);
    }

    [Fact]
    public void EveryShaderPresetHasShaderText()
    {
        string root;
        try
        {
            root = ResourceLocator.PresetsDir;
        }
        catch (Exception ex)
        {
            output.WriteLine("跳过：找不到原版资源目录 - " + ex.Message);
            return;
        }

        if (!Directory.Exists(root) || ShaderTemplates.DefaultWarpPixelShader is null)
        {
            return;
        }

        var missingWarp = new List<string>();
        var missingComp = new List<string>();
        var shaderPresets = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.milk", SearchOption.AllDirectories))
        {
            var state = new PresetState(new Random(1));
            if (!state.Import(path, 0f) || state.MaxPsVersion <= 0)
            {
                continue;
            }

            shaderPresets++;

            if (string.IsNullOrWhiteSpace(state.WarpShadersText))
            {
                missingWarp.Add(Path.GetFileName(path));
            }

            if (string.IsNullOrWhiteSpace(state.CompShadersText))
            {
                missingComp.Add(Path.GetFileName(path));
            }
        }

        output.WriteLine($"带 shader 的预设: {shaderPresets}，缺 warp 正文: {missingWarp.Count}，缺 comp 正文: {missingComp.Count}");
        Assert.True(shaderPresets > 300);
        Assert.Empty(missingWarp);
        Assert.Empty(missingComp);
    }

    private static string WriteTempMilk(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"milkdrop_{Guid.NewGuid():N}.milk");
        File.WriteAllLines(path, lines);
        return path;
    }
}
