using MilkDrop.Classic.Render.D3D9;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 shader 装配（plugin.cpp:3490-3653）：include 补寄存器、<c>shader_body</c> → <c>void PS(...)</c>、
/// 插入 <c>float3 ret = 0;</c> 与 <c>_return_value</c> 收尾；最后整语料跑一遍真实编译。
/// </summary>
public sealed class ShaderAssemblerTests(ITestOutputHelper output)
{
    private static string? IncludeText
    {
        get
        {
            try
            {
                return ShaderTemplates.Include;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    [Fact]
    public void PreludeAddsExplicitRegisters()
    {
        var include = IncludeText;
        if (include is null)
        {
            output.WriteLine("跳过：找不到 data/include.fx");
            return;
        }

        var prelude = ShaderPrelude.AddExplicitRegisters(include);

        Assert.Contains("float4 _c0 : register(c0);", prelude);
        Assert.Contains("float4 _c13 : register(c13);", prelude);
        Assert.Contains("float4 _qa : register(c14);", prelude);
        Assert.Contains("float4 _qh : register(c21);", prelude);
        Assert.Contains("float4x3 rot_s1 : register(c22);", prelude);
        Assert.Contains("float4x3 rot_rand4 : register(c91);", prelude);
        Assert.Contains("float4 rand_frame : register(c94);", prelude);
        Assert.Contains("float4 rand_preset : register(c95);", prelude);
        Assert.Contains("float4 texsize_noisevol_hq : register(c101);", prelude);

        // 采样器与 #define 原样保留
        Assert.Contains("sampler2D sampler_main : register(s0)", prelude);
        Assert.Contains("sampler2D sampler_blur3 : register(s13)", prelude);
        Assert.Contains("#define GetMain(uv)", prelude);

        // 常量表能从源码里解析回来
        var table = ShaderConstantTable.Parse(prelude);
        Assert.True(table.TryGetFloatRegister("_qa", out var qa));
        Assert.Equal(14, qa);
        Assert.True(table.TryGetFloatRegister("rot_vf4", out var rot));
        Assert.Equal(67, rot);   // rot_s1..s4 / d / f 各占 3 个寄存器之后
        Assert.Equal(102, ShaderPrelude.RegisterCount);
    }

    [Fact]
    public void AssembleInsertsEntryPointAndReturnValue()
    {
        var include = IncludeText;
        if (include is null)
        {
            return;
        }

        const string body = "shader_body\n{\n    // a comment\n    ret = tex2D(sampler_main, uv).xyz;\n}\n";

        var text = ShaderAssembler.Assemble(include, body, MilkShaderKind.Warp, out var error);

        Assert.Null(error);
        Assert.NotNull(text);
        Assert.DoesNotContain("shader_body", text);
        Assert.Contains("void PS( float4 _vDiffuse : COLOR, float2 _uv : TEXCOORD0, float2 _uv_orig : TEXCOORD1, float2 _rad_ang : TEXCOORD2, out float4 _return_value : COLOR0 )", text);
        Assert.Contains("float3 ret = 0;", text);
        Assert.Contains("_return_value = float4(ret.xyz, _vDiffuse.w);", text);
        Assert.Contains("#define uv_orig _uv_orig", text);

        // 注释在编译前剥掉（含 "// a comment"）
        Assert.DoesNotContain("a comment", text);

        // 正文仍然在里面
        Assert.Contains("ret = tex2D(sampler_main, uv).xyz;", text);
    }

    [Fact]
    public void CompAssemblyUsesOwnDefinesAndParams()
    {
        var include = IncludeText;
        if (include is null)
        {
            return;
        }

        var text = ShaderAssembler.Assemble(include, "shader_body\n{\n}\n", MilkShaderKind.Comp, out var error);

        Assert.Null(error);
        Assert.NotNull(text);
        Assert.Contains("#define hue_shader _vDiffuse.xyz", text);
        Assert.Contains("float4 _vDiffuse : COLOR, float2 _uv : TEXCOORD0, float2 _rad_ang : TEXCOORD2", text);
        Assert.Contains("#define uv_orig _uv", text);
    }

    [Fact]
    public void SelfReferentialDeclarationIsRewritten()
    {
        var include = IncludeText;
        if (include is null)
        {
            return;
        }

        // crystal palace 的 comp 里有 `float3 ret1 = ret1;`，新版编译器会报 X4000
        var text = ShaderAssembler.Assemble(
            include,
            "float3 ret1;\nshader_body\n{\nfloat3 ret1 = ret1;\nret = ret1;\n}\n",
            MilkShaderKind.Comp,
            out var error);

        Assert.Null(error);
        Assert.NotNull(text);
        Assert.Contains("ret1 = ret1;", text);
        Assert.DoesNotContain("float3 ret1 = ret1;", text);
    }

    [Fact]
    public void OnlyUsedSamplersGetRegisters()
    {
        var include = IncludeText;
        if (include is null)
        {
            return;
        }

        var text = ShaderAssembler.Assemble(
            include,
            "shader_body\n{\nret = tex2D(sampler_noise_lq, uv).xyz;\n}\n",
            MilkShaderKind.Warp,
            out var error);

        Assert.Null(error);
        Assert.NotNull(text);
        Assert.Contains("sampler_noise_lq : register(s0)", text);
        Assert.DoesNotContain("sampler_main : register", text);
        Assert.DoesNotContain("sampler_blur1 : register", text);
    }

    [Fact]
    public void PresetVertexShaderCompiles()
    {
        var vs2 = ShaderCompiler.Compile(ShaderAssembler.VertexShaderSource, "VS", "vs_2_0");
        var vs3 = ShaderCompiler.Compile(ShaderAssembler.VertexShaderSource, "VS", "vs_3_0");

        Assert.True(vs2.Success, vs2.Error);
        Assert.True(vs3.Success, vs3.Error);
    }

    [Fact]
    public void BlurShadersCompile()
    {
        var vs = ShaderCompiler.Compile(BlurShaderTemplates.VertexShaderSource, "VS", "vs_2_0");
        var horizontal = ShaderCompiler.Compile(BlurShaderTemplates.HorizontalShaderSource, "PS", "ps_2_0");
        var vertical = ShaderCompiler.Compile(BlurShaderTemplates.VerticalShaderSource, "PS", "ps_2_0");

        Assert.True(vs.Success, vs.Error);
        Assert.True(horizontal.Success, horizontal.Error);
        Assert.True(vertical.Success, vertical.Error);
    }

    [Fact]
    public void AssembleHandlesLinefeedControlChars()
    {
        var include = IncludeText;
        if (include is null)
        {
            return;
        }

        // 预设里存的是 0x01 换行（.milk 单行编码）
        var body = string.Join(EelPreprocessor.LinefeedControlChar, "shader_body", "{", "    ret = 1;", "}");

        var text = ShaderAssembler.Assemble(include, body, MilkShaderKind.Warp, out var error);

        Assert.Null(error);
        Assert.NotNull(text);
        Assert.Contains("ret = 1;", text);
        Assert.DoesNotContain(EelPreprocessor.LinefeedControlChar, text);
    }

    [Fact]
    public void MissingShaderBodyIsReported()
    {
        var text = ShaderAssembler.Assemble("float4 _c0 : register(c0);", "ret = 1;", MilkShaderKind.Warp, out var error);

        Assert.Null(text);
        Assert.Contains("shader_body", error);
    }

    [Fact]
    public void UnknownConstantBreaksLayoutLoudly()
    {
        const string include = "float4 something_new;\n";

        var ex = Assert.Throws<InvalidOperationException>(() => ShaderPrelude.AddExplicitRegisters(include));
        Assert.Contains("something_new", ex.Message);
    }

    [Fact]
    public void CorpusAssemblesAndCompiles()
    {
        var include = IncludeText;
        if (include is null)
        {
            output.WriteLine("跳过：找不到 data/include.fx");
            return;
        }

        string presetsDir;
        try
        {
            presetsDir = ResourceLocator.PresetsDir;
        }
        catch (Exception ex)
        {
            output.WriteLine("跳过：找不到原版资源目录 - " + ex.Message);
            return;
        }

        var defineFailures = new List<string>();
        var unexpected = new List<string>();
        var knownPresetIssues = 0;
        var compiled = 0;

        foreach (var path in Directory.EnumerateFiles(presetsDir, "*.milk", SearchOption.AllDirectories))
        {
            var state = new PresetState(new Random(1));
            if (!state.Import(path, 0f))
            {
                continue;
            }

            var name = Path.GetFileName(path);

            if (state.WarpPsVersion > 0)
            {
                Check(name, "warp", state.WarpShadersText, MilkShaderKind.Warp, state.WarpPsVersion);
            }

            if (state.CompPsVersion > 0)
            {
                Check(name, "comp", state.CompShadersText, MilkShaderKind.Comp, state.CompPsVersion);
            }
        }

        void Check(string name, string label, string shaderText, MilkShaderKind kind, int version)
        {
            var source = ShaderAssembler.Assemble(include, shaderText, kind, out var assembleError);
            if (source is null)
            {
                defineFailures.Add($"{name} {label}: {assembleError}");
                return;
            }

            var profile = ShaderCompiler.ProfileForVersion(version);
            var result = ShaderCompiler.CompilePixelShaderForVersion(source, version);
            if (result.Success)
            {
                compiled++;
                return;
            }

            var error = FirstError(result.Error);
            if (error is null)
            {
                // 只有 warning —— 编译产物照样可用
                compiled++;
                return;
            }

            if (KnownPresetIssues.Any(k => error.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                knownPresetIssues++;
                return;
            }

            unexpected.Add($"{name} {label}/{profile}: {error}");
        }

        output.WriteLine($"装配失败: {defineFailures.Count}，编译成功: {compiled}，预设自身问题: {knownPresetIssues}，意外失败: {unexpected.Count}");

        foreach (var failure in defineFailures.Take(10))
        {
            output.WriteLine("ASSEMBLE " + failure);
        }

        foreach (var failure in unexpected.Take(10))
        {
            output.WriteLine("UNEXPECTED " + failure);
        }

        Assert.Empty(defineFailures);
        Assert.True(compiled >= 620);
        Assert.Empty(unexpected);
    }

    [Fact]
    public void AssembledRegistersArePackedWithoutOverlap()
    {
        var include = IncludeText;
        if (include is null)
        {
            return;
        }

        string presetsDir;
        try
        {
            presetsDir = ResourceLocator.PresetsDir;
        }
        catch (Exception)
        {
            return;
        }

        var checkedShaders = 0;

        foreach (var path in Directory.EnumerateFiles(presetsDir, "*.milk", SearchOption.AllDirectories))
        {
            var state = new PresetState(new Random(1));
            if (!state.Import(path, 0f))
            {
                continue;
            }

            if (state.WarpPsVersion > 0)
            {
                Check(state.WarpShadersText, MilkShaderKind.Warp, Path.GetFileName(path));
            }

            if (state.CompPsVersion > 0)
            {
                Check(state.CompShadersText, MilkShaderKind.Comp, Path.GetFileName(path));
            }
        }

        void Check(string shaderText, MilkShaderKind kind, string name)
        {
            var source = ShaderAssembler.Assemble(include, shaderText, kind, out _);
            if (source is null)
            {
                return;
            }

            var table = ShaderConstantTable.Parse(source);
            var taken = new HashSet<int>();

            foreach (var (constant, register) in table.FloatRegisters)
            {
                var size = ShaderPrelude.RegisterSize(constant);
                for (var r = register; r < register + size; r++)
                {
                    Assert.True(taken.Add(r), $"{name}: 常量 {constant} 的寄存器 c{r} 与其它常量重叠");
                }
            }

            checkedShaders++;
        }

        output.WriteLine($"检查寄存器布局的 shader 数: {checkedShaders}");
        Assert.True(checkedShaders > 600);
    }

    /// <summary>
    /// 已知的"预设源码本身有问题"的情况（原版 MilkDrop3 编译这些 shader 同样会失败并回退到默认 shader）：
    /// 同名宏被局部变量覆盖、projectM 专有的 <c>mouse</c>/<c>get_fft</c>、未初始化变量、指令槽超限。
    /// </summary>
    /// <remarks>
    /// 这是**默认路径**（兼容层关闭）的白名单。打开 <c>ShaderCompatFix</c> 后这些 shader 都能编译，
    /// 见 <see cref="ShaderCompatTests"/>。
    /// </remarks>
    private static readonly string[] KnownPresetIssues =
    [
        "invalid subscript 'y'",
        "undeclared identifier 'mouse'",
        "undeclared identifier 'get_fft'",
        "used without having been completely initialized",
        "too many arithmetic instruction slots",
        "does not support double-precision floating-point",
        "NaN and infinity literals not allowed",
    ];

    private static string? FirstError(string? text)
        => text?.Split('\n').FirstOrDefault(line => line.Contains("error ", StringComparison.Ordinal));

}
