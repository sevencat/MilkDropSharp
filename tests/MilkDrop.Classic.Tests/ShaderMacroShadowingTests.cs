using MilkDrop.Classic.Render.D3D9;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// "宏名被局部变量遮蔽"的兼容层（默认关闭）。
/// include.fx 里有 <c>#define aspect _c0</c>，预设若声明 <c>float aspect = ...;</c>，
/// 预处理会把变量名也替换成 <c>_c0</c>，导致 <c>X3018: invalid subscript</c>。
/// </summary>
public sealed class ShaderMacroShadowingTests
{
    private static string? Assemble(string body, bool fix, out IReadOnlyList<string> renames, out string? error)
        => ShaderAssembler.Assemble(
            ShaderTemplates.Include!,
            body,
            MilkShaderKind.Warp,
            out error,
            fix ? ShaderCompatFix.MacroShadowing : ShaderCompatFix.None,
            out renames);

    [Fact]
    public void OffByDefaultKeepsSourceUntouched()
    {
        const string body = """
            shader_body
            {
                float aspect = aspect.x/aspect.y;
                float2 p = uv.xy * float2(aspect, 1.0);
                ret = float3(p, 1);
            }
            """;

        var source = Assemble(body, fix: false, out var renames, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Empty(renames);
        Assert.Contains("float aspect = aspect.x/aspect.y;", source, StringComparison.Ordinal);
        Assert.Contains("float2(aspect, 1.0)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledRenamesLocalVariableAndKeepsInitializer()
    {
        const string body = """
            shader_body
            {
                float aspect = aspect.x/aspect.y;
                float2 p = uv.xy * float2(aspect, 1.0);
                ret = float3(p, 1);
            }
            """;

        var source = Assemble(body, fix: true, out var renames, out var error);

        Assert.Null(error);
        Assert.NotNull(source);

        // 声明名改了，初始化表达式仍指向宏（= 作者的意图）
        Assert.Contains("float aspect__md = aspect.x/aspect.y;", source, StringComparison.Ordinal);
        Assert.Contains("float2(aspect__md, 1.0)", source, StringComparison.Ordinal);
        Assert.Equal(["aspect -> aspect__md"], renames);
    }

    [Fact]
    public void RenameStopsAtEndOfBlock()
    {
        const string body = """
            shader_body
            {
                float a = 1;
                {
                    float aspect = aspect.x;
                    float b = aspect;
                }
                float c = aspect.y;
                ret = float3(a, 0, c);
            }
            """;

        var source = Assemble(body, fix: true, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("float aspect__md = aspect.x;", source, StringComparison.Ordinal);
        Assert.Contains("float b = aspect__md;", source, StringComparison.Ordinal);
        Assert.Contains("float c = aspect.y;", source, StringComparison.Ordinal);   // 块外仍是宏
    }

    [Fact]
    public void MemberAccessIsNotRenamed()
    {
        const string body = """
            shader_body
            {
                float aspect = 2.0;
                float4 q = float4(1, 1, 1, 1);
                float k = q.aspect;
                ret = float3(aspect, k, 0);
            }
            """;

        var source = Assemble(body, fix: true, out _, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("float aspect__md = 2.0;", source, StringComparison.Ordinal);
        Assert.Contains("q.aspect", source, StringComparison.Ordinal);
        Assert.Contains("float3(aspect__md, k, 0)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonIsNotMistakenForDeclaration()
    {
        const string body = """
            shader_body
            {
                float aspect = 1.0;
                float k = (aspect == 1.0) ? 1.0 : 0.0;
                ret = float3(k, 0, 0);
            }
            """;

        var source = Assemble(body, fix: true, out var renames, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Single(renames);   // 只改了声明，没有把比较里的 aspect 当成第二个声明
        Assert.Contains("aspect__md == 1.0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionStyleMacrosAreIgnored()
    {
        var names = ShaderAssembler.CollectObjectMacroNames("""
            #define aspect _c0
            #define saturate(x) clamp(x,0,1)
            #define NUM_TEX 3
            """);

        Assert.Contains("aspect", names);
        Assert.Contains("NUM_TEX", names);
        Assert.DoesNotContain("saturate", names);
    }

    /// <summary>
    /// 兼容层不能把原本能装配的预设改坏：整语料分别按"关/开"装配，失败数必须相同。
    /// </summary>
    [Fact]
    public void AllPresetsAssembleNoWorseWithFixEnabled()
    {
        var include = ShaderTemplates.Include!;
        var offFailures = 0;
        var onFailures = 0;
        var renameCount = 0;

        foreach (var path in ResourceLocator.EnumeratePresets())
        {
            PresetKeyValueFile file;
            try
            {
                file = PresetKeyValueFile.Load(path);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var (kind, prefix) in new[] { (MilkShaderKind.Warp, "warp_"), (MilkShaderKind.Comp, "comp_") })
            {
                var text = file.ReadCode(prefix);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                _ = Assemble(text, fix: false, out _, out var offError);
                if (offError is not null)
                {
                    offFailures++;
                }

                _ = Assemble(text, fix: true, out var renames, out var onError);
                if (onError is not null)
                {
                    onFailures++;
                }

                renameCount += renames.Count;
            }
        }

        Assert.Equal(offFailures, onFailures);
        Assert.True(renameCount > 0, "整语料里应该至少有一处宏名遮蔽（circuit network9）");
    }

    /// <summary>真实预设：<c>circuit network9</c> 的 warp 就是这个写法（原版同样编译失败）。</summary>
    [Fact]
    public void RealPresetIsFixed()
    {
        var path = Path.Combine(ResourceLocator.PresetsDir, "Altunenes - MilkDrop2077 - circuit network9.milk");
        var file = PresetKeyValueFile.Load(path);
        var text = file.ReadCode("warp_");

        var source = Assemble(text, fix: true, out var renames, out var error);

        Assert.Null(error);
        Assert.NotNull(source);
        Assert.Contains("aspect__md", source, StringComparison.Ordinal);
        Assert.DoesNotContain("float aspect = aspect", source, StringComparison.Ordinal);
        Assert.Contains(renames, r => r.StartsWith("aspect -> ", StringComparison.Ordinal));
    }
}
