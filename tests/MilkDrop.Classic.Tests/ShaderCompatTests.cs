using MilkDrop.Classic.Render.D3D9;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// shader 兼容层（默认关闭）：原版会编译失败的预设，打开后应该都能编译。
/// </summary>
/// <remarks>
/// 对照 <see cref="ShaderAssemblerTests"/> 里的 <c>KnownPresetIssues</c>：
/// 默认路径保持"原版行为"（失败并回退默认 shader），这里锁的是打开兼容层后的行为。
/// 每类对应一个编译器错误码：
/// <c>X3018</c> 宏名遮蔽、<c>X3684</c> 双精度、<c>X4579</c> 非有限字面量、
/// <c>X3004</c> 缺 <c>get_fft</c>/<c>mouse</c>、<c>X5608</c> 指令槽超限。
/// </remarks>
public sealed class ShaderCompatTests(ITestOutputHelper output)
{
    private static string? Assemble(
        string body,
        ShaderCompatFix fixes,
        out IReadOnlyList<string> notes,
        out string? error)
        => ShaderAssembler.Assemble(ShaderTemplates.Include!, body, MilkShaderKind.Warp, out error, fixes, out notes);

    [Fact]
    public void OffByDefaultLeavesSourceAlone()
    {
        const string body = """
            shader_body
            {
                double3 crisp = tex2D(sampler_main, uv).xyz;
                ret = crisp * get_fft(0.5) * mouse.x;
            }
            """;

        var source = Assemble(body, ShaderCompatFix.None, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Empty(notes);
        Assert.Contains("double3 crisp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sampler_fft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_mouse", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DoublePrecisionIsDemoted()
    {
        const string body = """
            shader_body
            {
                double3 crisp = tex2D(sampler_main, uv).xyz;
                double k = 2.0;
                ret = crisp * k;
            }
            """;

        var source = Assemble(body, ShaderCompatFix.PrecisionDemotion, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("float3 crisp", source, StringComparison.Ordinal);
        Assert.Contains("float k = 2.0;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("double", source, StringComparison.Ordinal);
        Assert.Contains("double -> float", notes);
    }

    [Fact]
    public void IdentifierContainingDoubleIsUntouched()
    {
        const string body = """
            shader_body
            {
                float doubleBuffer = 1.0;
                ret = doubleBuffer;
            }
            """;

        var source = Assemble(body, ShaderCompatFix.PrecisionDemotion, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("doubleBuffer", source, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    [Fact]
    public void NonFiniteLiteralBecomesFinite()
    {
        const string body = """
            shader_body
            {
                ret = 1.#INF;
            }
            """;

        var source = Assemble(body, ShaderCompatFix.NonFiniteLiterals, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("3.402823466e38", source, StringComparison.Ordinal);
        Assert.DoesNotContain("#INF", source, StringComparison.Ordinal);
        Assert.Contains("非有限字面量 -> 3.402823466e38", notes);
    }

    [Fact]
    public void DivisionByZeroLiteralIsRewritten()
    {
        // mrange - flaming experiment1d 的 comp：编译期折叠出 inf，新版编译器直接报 X4579
        const string body = """
            shader_body
            {
                ret /= float3(0.0,-3.0,-1.6);
                ret = abs(tex2D(sampler_main, uv).xyz - ret) * 2;
            }
            """;

        var source = Assemble(body, ShaderCompatFix.NonFiniteLiterals, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("ret /= float3(1e-20,-3.0,-1.6);", source, StringComparison.Ordinal);
        Assert.Contains("除零字面量 -> 1e-20", notes);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void DegenerateSmoothstepIsNudged()
    {
        // Heartless3：shadertoy 转译把 smoothstep 起了别名，并且两个边界都是 0
        const string body = """
            #define S(a, b, t) smoothstep(a, b, t)

            shader_body
            {
                float story = S(0, 0, time*2);
                ret = float3(story, 0, 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.NonFiniteLiterals, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("S(0, 0 + 1e-6, time*2)", source, StringComparison.Ordinal);
        Assert.Contains("退化 smoothstep 边界 -> +1e-6", notes);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void DirectSmoothstepIsNudged()
    {
        const string body = """
            shader_body
            {
                ret = float3(smoothstep(1.0, 1.0, uv.x), 0, 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.NonFiniteLiterals, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("smoothstep(1.0, 1.0 + 1e-6, uv.x)", source, StringComparison.Ordinal);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void HealthySmoothstepIsUntouched()
    {
        const string body = """
            #define S(a, b, t) smoothstep(a, b, t)

            shader_body
            {
                ret = float3(S(0.0, 0.29, uv.x), S(0.1, 0.9, uv.y), 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.NonFiniteLiterals, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("S(0.0, 0.29, uv.x)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("1e-6", source, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    [Fact]
    public void GetFftSamplesSpectrumTexture()
    {
        const string body = """
            shader_body
            {
                float fft_val = get_fft(0.25);
                ret = float3(fft_val, 0, 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.ProjectMBuiltins, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("#define get_fft(pos)", source, StringComparison.Ordinal);
        Assert.Contains("sampler2D sampler_fft : register(s", source, StringComparison.Ordinal);
        Assert.Contains("get_fft -> sampler_fft", notes);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void GetFftHzIsNotConfusedWithGetFft()
    {
        const string body = """
            shader_body
            {
                ret = float3(get_fft_hz(1000.0), 0, 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.ProjectMBuiltins, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("#define get_fft_hz(freq)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("#define get_fft(pos)", source, StringComparison.Ordinal);
        Assert.Contains("get_fft_hz -> sampler_fft", notes);
    }

    [Fact]
    public void MouseBecomesConstant()
    {
        // lara - Flexi Romanesco [MOUSE].milk：用 mouse.x / mouse.y / mouse.z
        const string body = """
            shader_body
            {
                float2 p = uv + mouse.xy;
                p += mouse.z;
                ret = float3(p, 1);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.ProjectMBuiltins, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("#define mouse _mouse", source, StringComparison.Ordinal);

        // 正文没用到 include 里的常量，所以 _mouse 落在 c0
        Assert.Contains("float4 _mouse : register(c0);", source, StringComparison.Ordinal);
        Assert.Contains("mouse -> _mouse", notes);

        var compiled = ShaderCompiler.CompilePixelShader(source, "PS", "ps_3_0");
        Assert.True(compiled.Success, compiled.Error);
    }

    [Fact]
    public void MouseRegisterDoesNotOverlap()
    {
        // 正文用到 time（= _c2），_mouse 必须排在它后面
        const string body = """
            shader_body
            {
                float k = time * mouse.x;
                ret = float3(k, 0, 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.ProjectMBuiltins, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);

        var table = ShaderConstantTable.Parse(source);
        Assert.True(table.TryGetFloatRegister("_mouse", out var mouseRegister));
        Assert.True(table.TryGetFloatRegister("_c2", out var timeRegister));
        Assert.NotEqual(timeRegister, mouseRegister);
    }

    [Fact]
    public void BuiltinsAreOnlyInjectedWhenUsed()
    {
        const string body = """
            shader_body
            {
                ret = tex2D(sampler_main, uv).xyz;
            }
            """;

        var source = Assemble(body, ShaderCompatFix.ProjectMBuiltins, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Empty(notes);
        Assert.DoesNotContain("sampler_fft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_mouse", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberAccessIsNotRewrittenToConstant()
    {
        const string body = """
            shader_body
            {
                float4 q = float4(1, 1, 1, 1);
                ret = float3(q.mouse, 0, 0);
            }
            """;

        var source = Assemble(body, ShaderCompatFix.ProjectMBuiltins, out var notes, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.DoesNotContain("_mouse", source, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    [Fact]
    public void InstructionSlotOverflowFallsBackToWiderProfile()
    {
        // ps_2_0 只有 64 个算术指令槽，ps_2_a 有 512 个。
        // 用一个真实的复杂 shader（Black hole4 的 comp，约 290 条算术指令）按 PSVERSION=1 去编，
        // 模拟"老预设声明了 ps_2_0 但内容放不下"的情况。
        var state = new PresetState(new Random(1));
        var path = Path.Combine(ResourceLocator.PresetsDir, "XorDev - MilkDrop2077 - Black hole4.milk");
        if (!File.Exists(path))
        {
            output.WriteLine("跳过：找不到 Black hole4 预设");
            return;
        }

        Assert.True(state.Import(path, 0f));
        var source = ShaderAssembler.Assemble(
            ShaderTemplates.Include!,
            state.CompShadersText,
            MilkShaderKind.Comp,
            out var error);

        Assert.Null(error);
        Assert.NotNull(source);

        var strict = ShaderCompiler.CompilePixelShaderForVersion(source, 1);
        Assert.False(strict.Success);
        Assert.Contains("X5608", strict.Error, StringComparison.Ordinal);

        var escalated = ShaderCompiler.CompilePixelShaderForVersion(source, 1, escalate: true);
        Assert.True(escalated.Success, escalated.Error);
    }

    /// <summary>
    /// 整语料：默认路径仍有失败（原版行为），打开兼容层后必须 0 失败。
    /// </summary>
    [Fact]
    public void WholeCorpusCompilesWithCompat()
    {
        var include = ShaderTemplates.Include;
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

        var failures = new List<string>();
        var offFailures = 0;
        var compiled = 0;
        var rewritten = 0;

        foreach (var path in Directory.EnumerateFiles(presetsDir, "*.milk", SearchOption.AllDirectories))
        {
            var state = new PresetState(new Random(1));
            if (!state.Import(path, 0f))
            {
                continue;
            }

            if (state.WarpPsVersion > 0)
            {
                Check(Path.GetFileName(path), "warp", state.WarpShadersText, MilkShaderKind.Warp, state.WarpPsVersion);
            }

            if (state.CompPsVersion > 0)
            {
                Check(Path.GetFileName(path), "comp", state.CompShadersText, MilkShaderKind.Comp, state.CompPsVersion);
            }
        }

        void Check(string name, string label, string text, MilkShaderKind kind, int version)
        {
            var off = ShaderAssembler.Assemble(include, text, kind, out _);
            if (off is not null && !ShaderCompiler.CompilePixelShaderForVersion(off, version).Success)
            {
                offFailures++;
            }

            var source = ShaderAssembler.Assemble(include, text, kind, out var assembleError, ShaderCompatFix.All, out var notes);
            if (source is null)
            {
                failures.Add($"{name} {label}: 装配失败 {assembleError}");
                return;
            }

            if (notes.Count > 0)
            {
                rewritten++;
            }

            var result = ShaderCompiler.CompilePixelShaderForVersion(source, version, escalate: true);
            if (result.Success)
            {
                compiled++;
                return;
            }

            var first = result.Error?.Split('\n').FirstOrDefault(line => line.Contains("error ", StringComparison.Ordinal));
            if (first is null)
            {
                compiled++;   // 只有 warning
                return;
            }

            failures.Add($"{name} {label}: {first.Trim()}");
        }

        output.WriteLine($"编译成功 {compiled}，兼容层改写 {rewritten}，默认路径失败 {offFailures}");

        foreach (var failure in failures.Take(10))
        {
            output.WriteLine("FAIL " + failure);
        }

        Assert.True(offFailures > 0, "默认路径应该仍有编译失败（否则这条测试没意义）");
        Assert.Empty(failures);
    }
}
