using Vortice.D3DCompiler;
using Vortice.Direct3D9;

namespace MilkDrop.Classic.Render.D3D9;

public readonly record struct ShaderCompileResult(byte[]? Bytecode, string? Error)
{
    public bool Success => Bytecode is not null;
}

/// <summary>
/// 着色器编译（替代 C 版的 <c>D3DXCompileShader</c>，见 <c>cbplan.md</c> §2.2）。
/// </summary>
/// <remarks>
/// <c>d3dcompiler_47</c> 仍支持 <c>ps_2_0 / ps_2_a / ps_2_b / ps_3_0</c>，但不支持 <c>vs_1_1</c>；
/// 内置的三个直通 VS（warp_vs/comp_vs/blur_vs，零常量）改编译成 <c>vs_2_0</c> 即可。
/// </remarks>
public static class ShaderCompiler
{
    public const string DefaultVertexProfile = "vs_2_0";
    public const string DefaultWarpPixelProfile = "ps_2_0";
    public const string DefaultCompositePixelProfile = "ps_3_0";

    public static ShaderCompileResult Compile(string source, string entryPoint, string profile, string sourceName = "milkdrop.fx")
    {
        try
        {
            // EnableBackwardsCompatibility：D3DXCompileShader 的默认行为，允许 shader 改写全局变量
            // （很多 MilkDrop 预设用 `float foo;` 这种老写法，不加会报 X3025）
            var bytecode = Compiler.Compile(
                source,
                entryPoint,
                sourceName,
                profile,
                ShaderFlags.EnableBackwardsCompatibility,
                EffectFlags.None);
            return new ShaderCompileResult(bytecode.ToArray(), null);
        }
        catch (Exception ex)
        {
            return new ShaderCompileResult(null, ex.Message);
        }
    }

    public static ShaderCompileResult CompilePixelShader(string source, string entryPoint = "PS", string profile = DefaultWarpPixelProfile)
        => Compile(source, entryPoint, profile);

    /// <summary>
    /// 预设里的 <c>PSVERSION</c> → 编译 profile（对应 <c>MD2_PS_*</c>，plugin.cpp:3374-3384）。
    /// </summary>
    /// <remarks>
    /// 1 = <c>ps_2_0</c>、2 = <c>ps_2_a</c>（失败再退 <c>ps_2_b</c>）、3/4/5 = <c>ps_3_0</c>。
    /// 注意 2 不是 <c>ps_2_0</c>：很多预设的算术指令数超过 ps_2_0 的 64 槽上限。
    /// <para>
    /// 4/5 也按 <c>ps_3_0</c> 编译：D3D9 设备最高只吃 ps_3_0，用 <c>d3dcompiler</c> 编出 ps_4_0 虽然能过，
    /// 但 <c>CreatePixelShader</c> 会失败 —— 而且 ps_4_0 不做 "NaN/infinity 字面量" 检查，
    /// 会让测试误以为某些预设能在真机上跑（<c>Heartless3</c> 就是这么漏过去的）。
    /// </para>
    /// </remarks>
    public static string ProfileForVersion(int version) => version switch
    {
        2 => "ps_2_a",
        >= 3 => "ps_3_0",
        _ => "ps_2_0",
    };

    /// <summary>
    /// 按预设版本编译像素着色器，<c>ps_2_a</c> 失败时自动退到 <c>ps_2_b</c>（plugin.cpp:3676-3686）。
    /// </summary>
    /// <param name="escalate">
    /// 兼容层开关：<c>ps_2_0</c> 失败时继续尝试 <c>ps_2_a</c>/<c>ps_2_b</c>。
    /// <c>ps_2_0</c> 只有 64 个算术指令槽，稍复杂的预设就会 <c>X5608</c>；<c>ps_2_a</c> 允许 512 个。
    /// 两者都属于 "2.x"，顶点着色器仍然是 <c>vs_2_0</c>，所以不会破坏 VS/PS 配对。
    /// 默认关：原版遇到这种情况直接回退默认 shader。
    /// </param>
    public static ShaderCompileResult CompilePixelShaderForVersion(
        string source,
        int version,
        string entryPoint = "PS",
        bool escalate = false)
    {
        var profile = ProfileForVersion(version);
        var result = new ShaderCompileResult(null, null);

        foreach (var candidate in Candidates(profile, escalate))
        {
            result = Compile(source, entryPoint, candidate);
            if (result.Success)
            {
                return result;
            }
        }

        return result;
    }

    /// <summary>按顺序要尝试的 profile（前一个是预设声明的目标，后面是回退/升级）。</summary>
    private static IEnumerable<string> Candidates(string profile, bool escalate) => profile switch
    {
        "ps_2_0" when escalate => ["ps_2_0", "ps_2_a", "ps_2_b"],
        "ps_2_0" => ["ps_2_0"],
        "ps_2_a" => ["ps_2_a", "ps_2_b"],
        _ => [profile],
    };

    public static ShaderCompileResult CompileVertexShader(string source, string entryPoint = "VS", string profile = DefaultVertexProfile)
        => Compile(source, entryPoint, profile);
}

/// <summary>把编译结果落成 D3D9 着色器对象。</summary>
public static class ShaderFactory
{
    public static IDirect3DPixelShader9? TryCreatePixelShader(
        IDirect3DDevice9 device,
        string source,
        out string? error,
        string entryPoint = "PS",
        string profile = ShaderCompiler.DefaultWarpPixelProfile)
    {
        var result = ShaderCompiler.Compile(source, entryPoint, profile);
        if (!result.Success)
        {
            error = result.Error;
            return null;
        }

        error = null;
        return device.CreatePixelShader(result.Bytecode);
    }

    public static IDirect3DVertexShader9? TryCreateVertexShader(
        IDirect3DDevice9 device,
        string source,
        out string? error,
        string entryPoint = "VS",
        string profile = ShaderCompiler.DefaultVertexProfile)
    {
        var result = ShaderCompiler.Compile(source, entryPoint, profile);
        if (!result.Success)
        {
            error = result.Error;
            return null;
        }

        error = null;
        return device.CreateVertexShader(result.Bytecode);
    }
}
