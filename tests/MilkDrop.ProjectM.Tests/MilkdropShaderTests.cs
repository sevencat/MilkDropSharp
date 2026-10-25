using HlslParser;
using MilkDrop.ProjectM.MilkdropPreset;
using MilkDrop.ProjectM.Renderer;
using MilkDrop.ProjectM.Tests.Rendering;
using Xunit;
using GLEnum = Silk.NET.OpenGLES.GLEnum;
using Sampler = MilkDrop.Rendering.Gles3.Sampler;
using Shader = MilkDrop.Rendering.Gles3.Shader;
using Texture = MilkDrop.Rendering.Gles3.Texture;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// <see cref="MilkdropShader"/> 的文本流程（不需要 GL 上下文）：预处理、采样器收集、模糊级别判定。
/// </summary>
public sealed class MilkdropShaderTextTests
{
    private const string WarpPresetCode = """
        sampler sampler_main;
        float4 texsize_main;

        shader_body
        {
            ret = tex2D(sampler_main, uv).rgb;
        }
        """;

    private const string CompPresetCode = """
        sampler sampler_main;

        shader_body
        {
            ret = tex2D(sampler_main, uv).rgb;
        }
        """;

    [Fact]
    public void LoadCode_WarpShader_InjectsEntryPointAndReturn()
    {
        var shader = new MilkdropShader(ShaderType.WarpShader);
        shader.LoadCode(WarpPresetCode);

        string code = shader.PreprocessedCode;

        // 公共头 + warp 宏 + PS 签名 + ret 声明 + 返回值。
        Assert.Contains("uniform float4   rand_frame;", code, StringComparison.Ordinal);
        Assert.Contains("#define q1 _qa.x", code, StringComparison.Ordinal);
        Assert.Contains("#define uv_orig _uv.zw", code, StringComparison.Ordinal);
        Assert.Contains("void PS(float4 _vDiffuse : COLOR,", code, StringComparison.Ordinal);
        Assert.Contains("out float4 _mv_tex_coords : COLOR1)", code, StringComparison.Ordinal);
        Assert.Contains("{\nfloat3 ret = 0;\n_mv_tex_coords.xy = _uv.xy;\n", code, StringComparison.Ordinal);
        Assert.Contains("_return_value = float4(ret.xyz, 1.0);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("shader_body", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCode_CompositeShader_UsesCompositeSignature()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode(CompPresetCode);

        string code = shader.PreprocessedCode;

        Assert.Contains("float2 _uv : TEXCOORD0", code, StringComparison.Ordinal);
        Assert.Contains("out float4 _return_value : COLOR)", code, StringComparison.Ordinal);
        Assert.Contains("#define hue_shader _vDiffuse.xyz", code, StringComparison.Ordinal);
        Assert.Contains("#define uv_orig _uv.xy", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_mv_tex_coords", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCode_InjectsMouseBuiltin()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode("""
            shader_body
            {
                ret = float3(mouse.xy, 0);
            }
            """);

        string code = shader.PreprocessedCode;

        // MilkDrop3 的 mouse 内建（原版 projectM 没有）：uniform + 宏。
        Assert.Contains("uniform float4   _mouse;", code, StringComparison.Ordinal);
        Assert.Contains("#define mouse    _mouse", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MouseBuiltin_TranspilesToFloat4Uniform()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode("""
            shader_body
            {
                ret = mouse.xyz;
            }
            """);

        // 不需要采样器，转译本身不碰 GL。
        string glsl = shader.TranspileHLSLShader([], [], []);

        Assert.Contains("uniform vec4 _mouse;", glsl, StringComparison.Ordinal);
        // 宏已展开，GLSL 里不该再出现 mouse 这个名字。
        Assert.DoesNotContain("mouse.", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCode_TruncatesTextAfterShaderFunction()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode("""
            shader_body
            {
                ret = 1;
            }

            // 这一行在 C++ 里会被截掉，不该出现在结果里。
            float4 stray = 1;
            """);

        Assert.DoesNotContain("stray", shader.PreprocessedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCode_RemovesSamplerStateBlocks()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode("""
            // sampler a = sampler_state { Filter = POINT; };
            sampler b = sampler_state { Filter = LINEAR; };

            shader_body
            {
                ret = 1;
            }
            """);

        string code = shader.PreprocessedCode;

        Assert.DoesNotContain("Filter = LINEAR", code, StringComparison.Ordinal);
        // 注释里的那个块保持原样（去注释只是把注释内容替换成空格，源码文本不变）。
        Assert.Contains("Filter = POINT", code, StringComparison.Ordinal);
        Assert.Contains("void PS(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCode_ThrowsOnMissingShaderBody()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);

        var exception = Assert.Throws<ShaderException>(() => shader.LoadCode("float4 x = 1;\n"));
        Assert.Contains("shader_body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCode_ThrowsOnEmptyCode()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);

        Assert.Throws<ShaderException>(() => shader.LoadCode(string.Empty));
    }

    [Fact]
    public void GetReferencedSamplers_CollectsSamplerAndTexSizeNames()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.GetReferencedSamplers("""
            sampler sampler_tex1;
            float4 texsize_tex1;
            uniform sampler2D sampler_tex2;
            // sampler_ignored 只在注释里
            /* texsize_ignored */
            float4 PS() { return tex2D(sampler_tex1, 0) + tex2D(sampler_tex2, 0); }
            """);

        Assert.Contains("main", shader.SamplerNames);
        Assert.Contains("tex1", shader.SamplerNames);
        Assert.Contains("tex2", shader.SamplerNames);
        Assert.DoesNotContain("ignored", shader.SamplerNames);
    }

    [Fact]
    public void GetReferencedSamplers_SkipsSamplerStateKeyword()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.GetReferencedSamplers("sampler_state { Filter = LINEAR; };");

        Assert.Equal(["main"], shader.SamplerNames);
    }

    [Fact]
    public void GetReferencedSamplers_DropsShortRandomNames()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.GetReferencedSamplers("""
            sampler sampler_rand00;
            sampler sampler_rand00_smalltiled;
            sampler sampler_rand01;
            """);

        // rand00 有长的同名前缀纹理 → 删短名；rand01 没有 → 保留。
        Assert.DoesNotContain("rand00", shader.SamplerNames);
        Assert.Contains("rand00_smalltiled", shader.SamplerNames);
        Assert.Contains("rand01", shader.SamplerNames);
    }

    [Fact]
    public void GetReferencedSamplers_DetectsBlurLevelFromMacros()
    {
        var shader = new MilkdropShader(ShaderType.WarpShader);
        shader.GetReferencedSamplers("float3 PS() { return GetBlur2(uv); }");

        Assert.Equal(BlurLevel.Blur2, shader.MaxBlurLevelRequired);
        Assert.Contains("blur1", shader.SamplerNames);
        Assert.Contains("blur2", shader.SamplerNames);
        Assert.DoesNotContain("blur3", shader.SamplerNames);
    }

    [Fact]
    public void GetReferencedSamplers_DetectsBlurLevelFromSamplerNames()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.GetReferencedSamplers("uniform sampler2D sampler_blur3;");

        Assert.Equal(BlurLevel.Blur3, shader.MaxBlurLevelRequired);
        Assert.Contains("blur1", shader.SamplerNames);
        Assert.Contains("blur2", shader.SamplerNames);
        Assert.Contains("blur3", shader.SamplerNames);
    }

    [Fact]
    public void GetReferencedSamplers_IgnoresCommentedBlurCalls()
    {
        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.GetReferencedSamplers("// GetBlur3(uv);\n/* GetBlur2(uv); */\n");

        Assert.Equal(BlurLevel.None, shader.MaxBlurLevelRequired);
    }

    [Fact]
    public void LoadCode_CollectsSamplersBeforeHeaderIsPrepended()
    {
        var shader = new MilkdropShader(ShaderType.WarpShader);
        shader.LoadCode(WarpPresetCode);

        // 公共头里的 #define sampler_FC_main 之类不算引用。
        Assert.Equal(["main"], shader.SamplerNames);
    }
}

/// <summary>
/// <see cref="MilkdropShader"/> 的转译/编译流程（需要离屏 GL 上下文）。
/// </summary>
[Collection("GL")]
public sealed class MilkdropShaderTranslationTests(OffscreenGlFixture gl)
{
    private const string WarpPresetCode = """
        sampler sampler_main;
        float4 texsize_main;

        shader_body
        {
            float3 color = tex2D(sampler_main, uv).rgb;
            ret = color * 1.5;
        }
        """;

    [Fact]
    public void WarpShader_TranspilesToGles300AndCompiles()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        var shader = new MilkdropShader(ShaderType.WarpShader);
        shader.LoadCode(WarpPresetCode);

        using var sampler = CreateSampler();
        var main = CreateDescriptor("main", "main", volume: false, sampler);

        string glsl = shader.TranspileHLSLShader([main], [], []);

        Assert.Contains("#version 300 es", glsl, StringComparison.Ordinal);
        Assert.Contains("uniform sampler2D sampler_main;", glsl, StringComparison.Ordinal);
        // 预设自带的声明要被剥掉，最终只留一份。
        Assert.Equal(1, CountOccurrences(glsl, "uniform sampler2D sampler_main;"));
        Assert.Contains("texture(", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("tex2D(", glsl, StringComparison.Ordinal);

        using var program = new Shader();
        shader.LoadTexturesAndCompile(program, [main], [], []);
    }

    [Fact]
    public void VolumeSampler_IsDeclaredAsSampler3D()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode("""
            sampler sampler_noisevol_lq;

            shader_body
            {
                ret = tex3D(sampler_noisevol_lq, uv.xyy).rgb;
            }
            """);

        using var sampler = CreateSampler();
        var main = CreateDescriptor("main", "main", volume: false, sampler);
        var noise = CreateDescriptor("noisevol_lq", "noisevol_lq", volume: true, sampler);

        string glsl = shader.TranspileHLSLShader([main], [], [noise]);
        Assert.Contains("uniform sampler3D sampler_noisevol_lq;", glsl, StringComparison.Ordinal);

        using var program = new Shader();
        shader.LoadTexturesAndCompile(program, [main], [], [noise]);
    }

    [Fact]
    public void BlurDescriptors_DoNotEmitTexSizeDeclaration()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        var shader = new MilkdropShader(ShaderType.CompositeShader);
        shader.LoadCode("""
            shader_body
            {
                ret = GetBlur1(uv);
            }
            """);

        using var sampler = CreateSampler();
        var main = CreateDescriptor("main", "main", volume: false, sampler);
        var blur1 = CreateDescriptor("blur1", string.Empty, volume: false, sampler);

        string glsl = shader.TranspileHLSLShader([main], [blur1], []);

        Assert.Contains("uniform sampler2D sampler_blur1;", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("texsize_blur1", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    /// 造一个声明用的描述符。纹理走"接管已有句柄"的构造（id 非 0、不分配 GL 资源），
    /// 只是为了满足 <see cref="TextureSamplerDescriptor"/> 的非空判定。
    /// </summary>
    private static TextureSamplerDescriptor CreateDescriptor(
        string samplerName,
        string sizeName,
        bool volume,
        Sampler sampler)
    {
        var texture = new Texture(samplerName, 1u, volume ? GLEnum.Texture3D : GLEnum.Texture2D, 4, 4, false);
        return new TextureSamplerDescriptor(texture, sampler, samplerName, sizeName);
    }

    private static Sampler CreateSampler()
        => new((int)GLEnum.ClampToEdge, (int)GLEnum.Linear);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            ++count;
            index += value.Length;
        }

        return count;
    }
}
