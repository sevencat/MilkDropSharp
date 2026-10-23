using System.Text;
using System.Text.RegularExpressions;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>
/// 给 <c>data/include.fx</c> 的常量补显式寄存器号（<c>: register(cN)</c>），并按实际使用情况紧凑分配。
/// </summary>
/// <remarks>
/// C 版靠 <c>ID3DXConstantTable</c> 按名字查寄存器（未用到的常量还会被编译器裁掉）；Vortice 没有这个
/// 接口，所以我们在生成源码时就写死寄存器号，再用 <see cref="ShaderConstantTable"/> 从源码里解析回来
/// （cbplan.md §2.2 / §4.2）。
/// <para>
/// <b>必须按使用情况分配</b>：ps_2_0 只有 c0..c31 共 32 个 float4，如果照 include.fx 全量声明
/// （102 个寄存器），ps_2_0 的 shader 会直接报 <c>X5200 Invalid register number</c>。
/// 因此这里先扫一遍正文，找出真正引用的常量（包括通过 <c>#define time _c2.x</c> 这类别名间接引用），
/// 再按固定顺序紧凑编排 —— 与 D3DX 的"按需分配"效果一致。
/// </para>
/// <para>寄存器编号只要求与写入方（后续 <c>ApplyShaderParams</c> 移植）协商一致，布局见 <see cref="RegisterLayout"/>。</para>
/// </remarks>
public static class ShaderPrelude
{
    private enum ConstantKind
    {
        Float4,
        Float4x3,
    }

    private sealed record ConstantInfo(string Name, ConstantKind Kind, int Size);

    /// <summary>规范顺序的常量表（决定寄存器分配顺序）。</summary>
    private static readonly ConstantInfo[] Constants = BuildConstants();

    /// <summary>常量名 → 起始寄存器号（仅在"全部声明"时有意义；实际分配依使用情况而定）。</summary>
    public static IReadOnlyDictionary<string, int> RegisterLayout { get; } = BuildFullLayout();

    /// <summary>全量声明时占用的寄存器数（c0..c101）。</summary>
    public const int RegisterCount = 102;

    private static readonly Dictionary<string, ConstantInfo> ConstantsByName =
        Constants.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

    private static readonly Regex DeclarationRegex = new(
        @"^(?<indent>\s*)(?<type>float4x3|float4)\s+(?<names>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*;\s*(?<comment>//.*)?$",
        RegexOptions.Compiled);

    private static readonly Regex SamplerRegex = new(
        @"^(?<indent>\s*)(?<type>sampler(?:2D|3D))\s+(?<name>[A-Za-z_]\w*)(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex DefineRegex = new(
        @"^#define\s+(?<name>[A-Za-z_]\w*)(?<params>\s*\([^)]*\))?\s+(?<body>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex TokenRegex = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    /// 把 include 文本里的 <c>float4</c>/<c>float4x3</c> 声明改写成带显式寄存器的形式。
    /// </summary>
    /// <param name="includeText">include 文本（换行可以是 <c>0x01</c>、CRLF 或 LF）。</param>
    /// <param name="usedNames">
    /// 需要声明的常量名；<c>null</c> 表示全部声明（按 <see cref="RegisterLayout"/> 的固定布局）。
    /// </param>
    /// <exception cref="InvalidOperationException">出现布局表里没有的常量名（说明 include.fx 变了）。</exception>
    public static string AddExplicitRegisters(
        string includeText,
        IReadOnlySet<string>? usedNames = null,
        IReadOnlySet<string>? usedSamplers = null)
    {
        var normalized = Normalize(includeText);
        var registers = Allocate(usedNames);
        var samplers = AllocateSamplers(usedSamplers);
        var output = new List<string>(normalized.Length / 32 + 16);

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var samplerMatch = SamplerRegex.Match(line);

            if (samplerMatch.Success)
            {
                var name = samplerMatch.Groups["name"].Value;
                if (!samplers.TryGetValue(name, out var stage))
                {
                    // 没用到的 sampler 直接丢掉，给预设自己声明的纹理 sampler 留寄存器。
                    continue;
                }

                var rest = samplerMatch.Groups["rest"].Value.TrimStart();
                var annotated = $"{samplerMatch.Groups["indent"].Value}{samplerMatch.Groups["type"].Value} {name} : register(s{stage})";
                output.Add(rest.Length == 0 ? annotated : annotated + " " + rest);
                continue;
            }

            var match = DeclarationRegex.Match(line);

            if (!match.Success)
            {
                output.Add(line);
                continue;
            }

            var indent = match.Groups["indent"].Value;
            var comment = match.Groups["comment"].Success ? " " + match.Groups["comment"].Value : "";

            foreach (var rawName in match.Groups["names"].Value.Split(','))
            {
                var name = rawName.Trim();
                if (!ConstantsByName.TryGetValue(name, out var info))
                {
                    throw new InvalidOperationException($"include.fx 里的常量 {name} 没有分配寄存器号");
                }

                if (!registers.TryGetValue(name, out var register))
                {
                    // 没用到的常量直接丢掉，省寄存器
                    continue;
                }

                var type = info.Kind == ConstantKind.Float4x3 ? "float4x3" : "float4";
                output.Add($"{indent}{type} {name} : register(c{register});{comment}");
            }
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// 扫描 shader 正文，返回它真正引用到的常量名（含 <c>#define</c> 别名，如 <c>time</c> → <c>_c2</c>）。
    /// </summary>
    public static HashSet<string> CollectUsedConstants(string includeText, string shaderBody)
        => CollectUsedNames(includeText, shaderBody, out _);

    /// <summary>
    /// 同时收集正文（含宏展开）里用到的常量与 sampler。
    /// </summary>
    /// <remarks>
    /// 注意宏体里也会引用它们：<c>#define GetBlur1(uv) (tex2D(sampler_blur1,uv).xyz*_c5.x + _c5.y)</c>，
    /// 只扫正文 token 会漏掉 <c>_c5</c> 和 <c>sampler_blur1</c>。
    /// </remarks>
    public static HashSet<string> CollectUsedNames(
        string includeText,
        string shaderBody,
        out HashSet<string> usedSamplers)
    {
        var defines = ParseDefines(includeText);
        var usedConstants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        usedSamplers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();

        foreach (Match token in TokenRegex.Matches(shaderBody))
        {
            pending.Push(token.Value);
        }

        while (pending.Count > 0)
        {
            var token = pending.Pop();
            if (!visited.Add(token))
            {
                continue;
            }

            if (ConstantsByName.ContainsKey(token))
            {
                usedConstants.Add(token);
                continue;
            }

            if (ShaderSamplers.TryGet(token, out _))
            {
                usedSamplers.Add(token);
                continue;
            }

            if (defines.TryGetValue(token, out var replacement))
            {
                foreach (Match inner in TokenRegex.Matches(replacement))
                {
                    pending.Push(inner.Value);
                }
            }
        }

        return usedConstants;
    }

    /// <summary>收集 include 里所有 <c>#define</c>（含函数式宏）的替换文本。</summary>
    private static Dictionary<string, string> ParseDefines(string includeText)
    {
        var defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in Normalize(includeText).Split('\n'))
        {
            var match = DefineRegex.Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                defines[match.Groups["name"].Value] = match.Groups["body"].Value;
            }
        }

        return defines;
    }

    /// <summary>某个常量占用的寄存器个数（<c>float4x3</c> 是 3，其余 1）。</summary>
    public static int RegisterSize(string name)
        => ConstantsByName.TryGetValue(name, out var info) ? info.Size : 1;

    /// <summary>
    /// 某个 shader 实际用到的常量一共占多少个寄存器。
    /// </summary>
    /// <remarks>
    /// include 之外的常量（兼容层的 <c>_mouse</c>）要接在它们后面编号，才不会和已有寄存器撞车。
    /// <paramref name="usedNames"/> 必须与传给 <see cref="AddExplicitRegisters"/> 的是同一个集合
    /// （大小写不敏感），否则算出来的编号会和实际分配不一致。
    /// </remarks>
    public static int UsedRegisterCount(IReadOnlySet<string>? usedNames)
    {
        var total = 0;

        foreach (var constant in Constants)
        {
            if (usedNames is null || usedNames.Contains(constant.Name))
            {
                total += constant.Size;
            }
        }

        return total;
    }

    /// <summary>按规范顺序给"用到的常量"紧凑分配寄存器。</summary>
    private static Dictionary<string, int> Allocate(IReadOnlySet<string>? usedNames)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var register = 0;

        foreach (var constant in Constants)
        {
            if (usedNames is not null && !usedNames.Contains(constant.Name))
            {
                continue;
            }

            map[constant.Name] = register;
            register += constant.Size;
        }

        return map;
    }

    /// <summary>
    /// 查某个 sampler 在这个 shader 里拿到的阶段号（与 <see cref="AddExplicitRegisters"/> 的分配规则一致）。
    /// </summary>
    /// <remarks>include 之外的 sampler（兼容层的 <c>sampler_fft</c>）要靠它自己补声明。</remarks>
    public static bool TryGetSamplerStage(IReadOnlySet<string>? usedSamplers, string name, out int stage)
    {
        stage = 0;

        foreach (var sampler in ShaderSamplers.All)
        {
            if (usedSamplers is not null && !usedSamplers.Contains(sampler.Name))
            {
                continue;
            }

            if (string.Equals(sampler.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            stage++;
        }

        return false;
    }

    /// <summary>
    /// 这个 shader 实际用到的 include sampler 一共占多少个采样器寄存器（s0..sN-1）。
    /// </summary>
    /// <remarks>
    /// 预设自己声明的纹理 sampler 要接在这些之后编号，才不会和 include 里的撞车。
    /// </remarks>
    public static int UsedSamplerCount(IReadOnlySet<string>? usedSamplers)
    {
        var count = 0;

        foreach (var sampler in ShaderSamplers.All)
        {
            if (usedSamplers is null || usedSamplers.Contains(sampler.Name))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>按 <see cref="ShaderSamplers.All"/> 的顺序给用到（或全部）的 sampler 紧凑分配 s0..sN。</summary>
    private static Dictionary<string, int> AllocateSamplers(IReadOnlySet<string>? usedSamplers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stage = 0;

        foreach (var sampler in ShaderSamplers.All)
        {
            if (usedSamplers is not null && !usedSamplers.Contains(sampler.Name))
            {
                continue;
            }

            map[sampler.Name] = stage++;
        }

        return map;
    }

    private static ConstantInfo[] BuildConstants()
    {
        var list = new List<ConstantInfo>(110);

        // _c0.._c13：aspect / 时间声音 / blur 比例等（include.fx:7-17）
        for (var i = 0; i <= 13; i++)
        {
            list.Add(new ConstantInfo($"_c{i}", ConstantKind.Float4, 1));
        }

        // q1..q32 打包成 8 个 float4（include.fx:18-25）
        for (var i = 0; i < 8; i++)
        {
            list.Add(new ConstantInfo($"_q{(char)('a' + i)}", ConstantKind.Float4, 1));
        }

        // 24 个 float4x3 旋转矩阵，各占 3 个寄存器（include.fx:28-53）
        foreach (var prefix in (string[])["rot_s", "rot_d", "rot_f", "rot_vf", "rot_uf", "rot_rand"])
        {
            for (var i = 1; i <= 4; i++)
            {
                list.Add(new ConstantInfo(prefix + i, ConstantKind.Float4x3, 3));
            }
        }

        list.Add(new ConstantInfo("rand_frame", ConstantKind.Float4, 1));
        list.Add(new ConstantInfo("rand_preset", ConstantKind.Float4, 1));

        // 噪声纹理尺寸（include.fx:145-150）
        foreach (var name in (string[])
                 [
                     "texsize_noise_lq", "texsize_noise_lq_lite", "texsize_noise_mq",
                     "texsize_noise_hq", "texsize_noisevol_lq", "texsize_noisevol_hq",
                 ])
        {
            list.Add(new ConstantInfo(name, ConstantKind.Float4, 1));
        }

        return [.. list];
    }

    private static Dictionary<string, int> BuildFullLayout()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var register = 0;

        foreach (var constant in Constants)
        {
            map[constant.Name] = register;
            register += constant.Size;
        }

        return map;
    }

    private static string Normalize(string text) => text.Replace(ShaderAssembler.LinefeedControlChar, '\n');
}
