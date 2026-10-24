using MilkDrop.ShaderToy.Compile;
using MilkDrop.ShaderToy.Pass;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Compile;

/// <summary>
/// <see cref="ShaderToyTranspiler"/> 的文本规则（不依赖 GPU）。真正"能不能编译"见 <see cref="TranspileCompileTests"/>。
/// </summary>
public sealed class TranspileTests(ITestOutputHelper output)
{
    private const string SimpleImageCode = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

    [Fact]
    public void Transpile_EmitsEs3HeaderAndBuiltinUniforms()
    {
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, SimpleImageCode));

        output.WriteLine(result.Source);

        // #version 必须是第一行（前面不能有空行/注释）
        Assert.StartsWith("#version 300 es\n", result.Source);
        Assert.Equal(1, CountOccurrences(result.Source, "#version"));

        Assert.Contains("precision highp float;", result.Source);
        Assert.Contains("precision highp int;", result.Source);
        Assert.Contains("precision highp sampler2D;", result.Source);

        Assert.Contains("uniform vec3 iResolution;", result.Source);
        Assert.Contains("uniform float iTime;", result.Source);
        Assert.Contains("uniform float iTimeDelta;", result.Source);
        Assert.Contains("uniform float iFrameRate;", result.Source);
        Assert.Contains("uniform int iFrame;", result.Source);
        Assert.Contains("uniform vec4 iMouse;", result.Source);
        Assert.Contains("uniform vec4 iDate;", result.Source);
        Assert.Contains("uniform float iSampleRate;", result.Source);
        Assert.Contains("uniform float iChannelTime[4];", result.Source);
        Assert.Contains("uniform vec3 iChannelResolution[4];", result.Source);

        Assert.Contains("out vec4 ds_fragColor;", result.Source);
        Assert.Contains("#define HW_PERFORMANCE 1", result.Source);
    }

    [Fact]
    public void Transpile_DeclaresChannelSamplersByJsonType()
    {
        ShaderPass pass = MakePass(
            ShaderPassType.Image,
            SimpleImageCode,
            (0, ShaderInputType.Buffer),
            (1, ShaderInputType.Cubemap),
            (2, ShaderInputType.Volume),
            (3, ShaderInputType.Keyboard));

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(pass);

        Assert.Contains("uniform sampler2D iChannel0;", result.Source);
        Assert.Contains("uniform samplerCube iChannel1;", result.Source);
        Assert.Contains("uniform sampler3D iChannel2;", result.Source);
        Assert.Contains("uniform sampler2D iChannel3;", result.Source);
    }

    [Fact]
    public void Transpile_DeclaresAllFourChannelsEvenWhenJsonOmitsThem()
    {
        // S2-4：代码可能用了 JSON 里没写的通道，四个都要有声明
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, SimpleImageCode));

        for (int channel = 0; channel < 4; channel++)
        {
            Assert.Contains($"uniform sampler2D iChannel{channel};", result.Source);
        }
    }

    [Fact]
    public void Transpile_WrapsMainImageIntoSingleOutput()
    {
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, SimpleImageCode));

        Assert.Contains("mainImage(ds_color, gl_FragCoord.xy);", result.Source);
        Assert.Contains("ds_fragColor = ds_color;", result.Source);
        Assert.Equal(1, CountOccurrences(result.Source, "void main("));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Transpile_StripsVersionExtensionAndPragmaOnce()
    {
        const string code = """
            #version 100
            #extension GL_OES_standard_derivatives : enable
            #pragma once

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = vec4(1.0);
            }
            """;

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, code));

        Assert.Equal(1, CountOccurrences(result.Source, "#version"));
        Assert.DoesNotContain("GL_OES_standard_derivatives", result.Source);
        Assert.DoesNotContain("#pragma once", result.Source);
        Assert.Contains(result.Notes, n => n.Contains("#version", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("#extension", StringComparison.Ordinal));
    }

    [Fact]
    public void Transpile_PrependsCommonCodeBeforeUserCode()
    {
        const string common = "#define PI 3.14159265359\nfloat rot(float a) { return cos(a); }";
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(
            MakePass(ShaderPassType.Buffer, SimpleImageCode),
            common);

        int commonIndex = result.Source.IndexOf("float rot(float a)", StringComparison.Ordinal);
        int userIndex = result.Source.IndexOf("void mainImage", StringComparison.Ordinal);

        Assert.True(commonIndex > 0);
        Assert.True(userIndex > commonIndex, "common 代码必须在用户代码之前");

        // 两份代码里的 #version 都要被剥掉，只留我们那一个
        Assert.Equal(1, CountOccurrences(result.Source, "#version"));
    }

    [Fact]
    public void Transpile_SkipsWrappingWhenUserDefinesMain()
    {
        const string code = """
            void main()
            {
                vec2 uv = gl_FragCoord.xy / iResolution.xy;
                gl_FragColor = vec4(uv, 0.0, 1.0);
            }
            """;

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, code));

        Assert.Equal(1, CountOccurrences(result.Source, "void main("));
        Assert.DoesNotContain("mainImage(ds_color", result.Source);
        // gl_FragColor 要被换到我们的输出变量上
        Assert.DoesNotContain("gl_FragColor", result.Source);
        Assert.Contains("ds_fragColor = vec4(uv, 0.0, 1.0);", result.Source);
        Assert.Contains(result.Notes, n => n.Contains("main()", StringComparison.Ordinal));
    }

    [Fact]
    public void Transpile_ReplacesLegacyVaryingAndAttribute()
    {
        const string code = """
            varying vec2 vUv;
            attribute vec3 aPosition;

            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = vec4(vUv, aPosition.x, 1.0);
            }
            """;

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, code));

        Assert.DoesNotContain("varying", result.Source);
        Assert.DoesNotContain("attribute", result.Source);
        Assert.Contains("in vec2 vUv;", result.Source);
        Assert.Contains("in vec3 aPosition;", result.Source);
    }

    [Fact]
    public void Transpile_LeavesKeywordsInsideCommentsAlone()
    {
        const string code = """
            // Time varying pixel color, gl_FragColor is legacy here
            /* void main() { } —— 注释里的 main 不能让入口包裹被跳过 */
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                // texture2D( 出现在注释里不算使用
                fragColor = vec4(1.0);
            }
            """;

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, code));

        // 注释原样保留（上游 default-shader.json 的注释里就有 "Time varying pixel color"）
        Assert.Contains("// Time varying pixel color, gl_FragColor is legacy here", result.Source);
        Assert.Contains("/* void main() { }", result.Source);

        // 注释不算"使用了兼容语法"，也不影响入口判断
        Assert.DoesNotContain("#define texture2D", result.Source);
        Assert.Contains("mainImage(ds_color, gl_FragCoord.xy);", result.Source);
        Assert.DoesNotContain(result.Notes, n => n.Contains("varying", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, n => n.Contains("main()", StringComparison.Ordinal));
    }

    [Fact]
    public void Transpile_AddsCompatMacrosOnlyWhenUsed()
    {
        ShaderToyTranspileResult withLegacy = ShaderToyTranspiler.Transpile(MakePass(
            ShaderPassType.Image,
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = texture2D(iChannel0, fragCoord / iResolution.xy) * textureCube(iChannel1, vec3(0.0));
            }
            """));

        Assert.Contains("#define texture2D texture", withLegacy.Source);
        Assert.Contains("#define textureCube texture", withLegacy.Source);

        ShaderToyTranspileResult modern = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, SimpleImageCode));

        Assert.DoesNotContain("#define texture2D", modern.Source);
        Assert.DoesNotContain("#define textureCube", modern.Source);
        Assert.DoesNotContain("#define iGlobalTime", modern.Source);
    }

    [Fact]
    public void Transpile_MapsIGlobalTime()
    {
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(
            ShaderPassType.Image,
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                fragColor = vec4(vec3(fract(iGlobalTime)), 1.0);
            }
            """));

        Assert.Contains("#define iGlobalTime iTime", result.Source);
    }

    [Fact]
    public void Transpile_CubemapWrapsMainCubemapWithFaceIndex()
    {
        const string code = """
            void mainCubemap(out vec4 fragColor, in vec2 fragCoord, in vec3 rayOri, in vec3 rayDir)
            {
                fragColor = vec4(rayDir * 0.5 + 0.5, 1.0);
            }
            """;

        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Cubemap, code));

        Assert.Contains("uniform int ds_faceIndex;", result.Source);
        Assert.Contains("mainCubemap(ds_color, ds_fragCoord, vec3(0.0), normalize(ds_rayDir));", result.Source);
        // 六个面都要有射线方向（顺序照上游 viewport.glsl:144-151）
        Assert.Equal(6, CountOccurrences(result.Source, "case "));
        Assert.Contains("case 0: ds_rayDir = vec3(+1.0, -ds_uv.y, -ds_uv.x)", result.Source);
        Assert.Contains("case 5: ds_rayDir = vec3(-ds_uv.x, -ds_uv.y, -1.0)", result.Source);
    }

    [Fact]
    public void Transpile_MissingMainImageStillCompilesToBlack()
    {
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(MakePass(
            ShaderPassType.Image,
            "// 什么都没有"));

        Assert.Contains("ds_fragColor = vec4(0.0);", result.Source);
        Assert.Contains(result.Notes, n => n.Contains("mainImage", StringComparison.Ordinal));
    }

    [Fact]
    public void Transpile_CanUnescapeLiteralNewlinesOnDemand()
    {
        const string raw = "void mainImage(out vec4 c, in vec2 f)\\n{\\n    c = vec4(1.0);\\n}";

        ShaderToyTranspileResult plain = ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Image, raw));
        Assert.Contains("\\n", plain.Source);

        ShaderToyTranspileResult unescaped = ShaderToyTranspiler.Transpile(
            MakePass(ShaderPassType.Image, raw),
            options: new ShaderToyTranspileOptions { UnescapeLiteralNewlines = true });

        Assert.DoesNotContain("\\n", unescaped.Source);
        Assert.Contains("void mainImage(out vec4 c, in vec2 f)\n{", unescaped.Source);
    }

    [Fact]
    public void Transpile_CanOmitBuiltinUniforms()
    {
        ShaderToyTranspileResult result = ShaderToyTranspiler.Transpile(
            MakePass(ShaderPassType.Image, SimpleImageCode),
            options: new ShaderToyTranspileOptions { DeclareBuiltinUniforms = false, DeclareChannelUniforms = false });

        Assert.DoesNotContain("uniform float iTime;", result.Source);
        Assert.DoesNotContain("uniform sampler2D iChannel0;", result.Source);
    }

    [Fact]
    public void Transpile_RejectsCommonPasses()
    {
        // sound pass 自 P6 起可以转译（见 MainSoundEntryTests）
        ShaderToyTranspileResult sound = ShaderToyTranspiler.Transpile(
            MakePass(ShaderPassType.Sound, "vec2 mainSound(float t) { return vec2(0.0); }"));
        Assert.Contains("mainSound(ds_time)", sound.Source, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            ShaderToyTranspiler.Transpile(MakePass(ShaderPassType.Common, "#define A 1")));

        Assert.Throws<ArgumentException>(() =>
            ShaderToyTranspiler.Transpile(
                MakePass(ShaderPassType.Cubemap, "void mainCubemap(out vec4 c, in vec2 f, in vec3 o, in vec3 d) { c = vec4(0.0); }"),
                options: new ShaderToyTranspileOptions { DeclareBuiltinUniforms = false }));
    }

    private static ShaderPass MakePass(
        ShaderPassType type,
        string code,
        params (int Channel, ShaderInputType Type)[] inputs)
    {
        var pass = new ShaderPass { Type = type, Name = type.ToString(), Code = code };
        foreach ((int channel, ShaderInputType inputType) in inputs)
        {
            pass.Inputs.Add(new ShaderPassInput { Channel = channel, Type = inputType });
        }

        return pass;
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
