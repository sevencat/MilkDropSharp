using System.Text.RegularExpressions;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// 着色器常量表：名字 → 常量寄存器号。
/// </summary>
/// <remarks>
/// 替代 D3DX9 的 <c>ID3DXConstantTable</c>（Silk.NET/Vortice 都不提供）。
/// 做法是<b>不依赖反射</b>：我们自己生成 shader 源码，因此可以在源码里显式写
/// <c>float4 _c0 : register(c0);</c>，再从这个注解里解析出寄存器号 —— 见 cbplan.md §2.2 / §4.2。
/// </remarks>
public sealed partial class ShaderConstantTable
{
    private readonly Dictionary<string, int> _floatRegisters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _samplers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>float4 常量：名字 → <c>c#</c>。</summary>
    public IReadOnlyDictionary<string, int> FloatRegisters => _floatRegisters;

    /// <summary>采样器：名字 → <c>s#</c>（对应纹理层号）。</summary>
    public IReadOnlyDictionary<string, int> Samplers => _samplers;

    public static ShaderConstantTable Parse(string source)
    {
        var table = new ShaderConstantTable();

        foreach (Match match in RegisterAnnotationRegex().Matches(source))
        {
            var name = match.Groups["name"].Value;
            var kind = match.Groups["kind"].Value[0];
            var index = int.Parse(match.Groups["index"].Value);

            if (kind is 's')
            {
                table._samplers[name] = index;
            }
            else
            {
                table._floatRegisters[name] = index;
            }
        }

        return table;
    }

    public bool TryGetFloatRegister(string name, out int register) => _floatRegisters.TryGetValue(name, out register);

    public bool TryGetSampler(string name, out int stage) => _samplers.TryGetValue(name, out stage);

    /// <summary>解析 <c>identifier : register(cN | sN | iN | bN)</c>。</summary>
    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*:\s*register\s*\(\s*(?<kind>[csib])\s*(?<index>\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterAnnotationRegex();
}
