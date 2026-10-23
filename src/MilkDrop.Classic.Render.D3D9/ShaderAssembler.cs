using System.Text;
using System.Text.RegularExpressions;

namespace MilkDrop.Classic.Render.D3D9;

/// <summary>预设 shader 的种类（决定插入的 <c>#define</c> 与入口参数）。</summary>
public enum MilkShaderKind
{
    Warp,
    Comp,
}

/// <summary>
/// shader 兼容层：把原版会编译失败的预设源码改成能编译的形式。
/// </summary>
/// <remarks>
/// 全部默认关闭。这些改写都**偏离原版行为**（原版遇到同类 shader 直接编译失败并回退默认 shader），
/// 所以只在用户显式打开时才生效。类别与对应编译器错误：
/// <list type="bullet">
///   <item><see cref="MacroShadowing"/>：<c>X3018 invalid subscript</c>（宏名被局部变量遮蔽）；</item>
///   <item><see cref="PrecisionDemotion"/>：<c>X3684 does not support double-precision</c>（预设写了 <c>double3</c>）；</item>
///   <item><see cref="NonFiniteLiterals"/>：<c>X4579 NaN and infinity literals</c>（<c>1.#INF</c> / 除以零字面量）；</item>
///   <item><see cref="ProjectMBuiltins"/>：<c>X3004 undeclared identifier 'get_fft' / 'mouse'</c>（MD3 PRO 专有内建）。</item>
/// </list>
/// ps_2_0 指令槽超限（<c>X5608</c>）属于编译目标问题，见 <see cref="ShaderCompiler"/> 的 profile 升级。
/// </remarks>
[Flags]
public enum ShaderCompatFix
{
    None = 0,

    /// <summary>与 include 宏同名的局部变量改名（<c>aspect</c> → <c>aspect__md</c>）。</summary>
    MacroShadowing = 1 << 0,

    /// <summary><c>double</c>/<c>double2..4</c> 降级成 <c>float</c> 系列（D3D9 像素着色器不支持双精度）。</summary>
    PrecisionDemotion = 1 << 1,

    /// <summary><c>1.#INF</c> 之类非有限字面量换成有限大数；除零字面量换成 <c>1e-20</c>。</summary>
    NonFiniteLiterals = 1 << 2,

    /// <summary>补上 <c>get_fft</c>/<c>get_fft_hz</c>（采样 <c>sampler_fft</c>）与 <c>mouse</c>（<c>_mouse</c> 常量）。</summary>
    ProjectMBuiltins = 1 << 3,

    /// <summary>全部兼容改写。</summary>
    All = MacroShadowing | PrecisionDemotion | NonFiniteLiterals | ProjectMBuiltins,
}

/// <summary>
/// <c>CPlugin::LoadShaderFromMemory</c>（plugin.cpp:3490-3653）中"文本装配"部分的移植。
/// </summary>
/// <remarks>
/// 预设文件里存的只是正文（从 <c>shader_body</c> 开始）；真正编译前要做四件事：
/// <list type="number">
///   <item>拼上 include（并补显式寄存器号，见 <see cref="ShaderPrelude"/>）；</item>
///   <item>拼上 warp/comp 专有 <c>#define</c>（<c>uv</c> / <c>uv_orig</c> / <c>hue_shader</c> 等）；</item>
///   <item>把 <c>shader_body</c> 换成 <c>void PS( ... )</c>，并在函数体开头插 <c>float3 ret = 0;</c>；</item>
///   <item>把最后的 <c>}</c> 换成"写回 <c>_return_value</c> + 收尾括号"。</item>
/// </list>
/// 换行用 <c>\r\n</c>（预设里存的是 <see cref="LinefeedControlChar"/>）。
/// </remarks>
public static partial class ShaderAssembler
{
    public const string EntryPoint = "PS";

    /// <summary>
    /// 预设文本里的换行控制符（<c>0x01</c>，C 版 <c>LINEFEED_CONTROL_CHAR</c>）。
    /// 本层不能引用 <c>MilkDrop.Classic.Visualizer</c>（会形成循环依赖），故在此保留一份。
    /// </summary>
    internal const char LinefeedControlChar = '\u0001';

    private const string WarpDefines =
        "#define rad _rad_ang.x\n" +
        "#define ang _rad_ang.y\n" +
        "#define uv _uv\n" +
        "#define uv_orig _uv_orig\n";

    private const string CompDefines =
        "#define rad _rad_ang.x\n" +
        "#define ang _rad_ang.y\n" +
        "#define uv _uv\n" +
        "#define uv_orig _uv\n" +   // [sic] 原版注释：comp 的 uv_orig 就是 uv
        "#define hue_shader _vDiffuse.xyz\n";

    // 顶点布局是 FVF = XYZ | DIFFUSE | TEX3：TEXCOORD0=uv、TEXCOORD1=uv_orig、TEXCOORD2=rad/ang。
    private const string WarpParams =
        "float4 _vDiffuse : COLOR, float2 _uv : TEXCOORD0, float2 _uv_orig : TEXCOORD1, float2 _rad_ang : TEXCOORD2, out float4 _return_value : COLOR0";

    private const string CompParams =
        "float4 _vDiffuse : COLOR, float2 _uv : TEXCOORD0, float2 _rad_ang : TEXCOORD2, out float4 _return_value : COLOR0";

    /// <summary>
    /// 预设 pass 用的顶点着色器（对应 data/warp_vs.fx / comp_vs.fx）。
    /// </summary>
    /// <remarks>
    /// ps_3_0 不能配固定功能顶点处理（D3D9 会静默不画），所以 shader pass 必须绑 VS。
    /// 输入布局对应 <c>FVF = XYZ | DIFFUSE | TEX3</c>：
    /// TEXCOORD0=uv、TEXCOORD1=uv_orig、TEXCOORD2=rad/ang。
    /// </remarks>
    public const string VertexShaderSource = """
        void VS(float3 vPosIn : POSITION,
                float4 vDiffuseIn : COLOR,
                float2 uvIn : TEXCOORD0,
                float2 uvOrigIn : TEXCOORD1,
                float2 radAngIn : TEXCOORD2,
                out float4 vPosProj : POSITION,
                out float4 _vDiffuse : COLOR,
                out float2 _uv : TEXCOORD0,
                out float2 _uv_orig : TEXCOORD1,
                out float2 _rad_ang : TEXCOORD2)
        {
            vPosProj = float4(vPosIn.xyz, 1);
            _vDiffuse = vDiffuseIn;
            _uv = uvIn;
            _uv_orig = uvOrigIn;
            _rad_ang = radAngIn;
        }
        """;

    private const string FirstLine = "    float3 ret = 0;";
    private const string LastLine = "    _return_value = float4(ret.xyz, _vDiffuse.w);";

    /// <summary>warp/comp shader 的 4 个声明前缀（测试与常量绑定会用到）。</summary>
    public static string DefinesFor(MilkShaderKind kind) => kind == MilkShaderKind.Warp ? WarpDefines : CompDefines;

    /// <summary>
    /// 拼装出可以直接交给 <see cref="ShaderCompiler"/> 的完整 HLSL。
    /// </summary>
    /// <param name="includeText">include 文本（<c>data/include.fx</c>，换行可以是任意形式）。</param>
    /// <param name="presetShaderText">预设里的正文或默认正文（含 <c>shader_body</c>）。</param>
    /// <param name="kind">warp / comp。</param>
    /// <param name="error">失败原因（找不到 <c>shader_body</c>、<c>{</c>、<c>}</c>）。</param>
    public static string? Assemble(string includeText, string presetShaderText, MilkShaderKind kind, out string? error)
        => Assemble(includeText, presetShaderText, kind, out error, ShaderCompatFix.None, out _);

    /// <summary>
    /// 同 <see cref="Assemble(string, string, MilkShaderKind, out string?)"/>，但可以打开兼容层。
    /// </summary>
    /// <param name="fixes">要应用的兼容改写（默认 <see cref="ShaderCompatFix.None"/> = 原版行为）。</param>
    /// <param name="notes">实际做了哪些改写（形如 <c>aspect -&gt; aspect__md</c>），用于诊断。</param>
    public static string? Assemble(
        string includeText,
        string presetShaderText,
        MilkShaderKind kind,
        out string? error,
        ShaderCompatFix fixes,
        out IReadOnlyList<string> notes)
    {
        error = null;
        var applied = new List<string>();

        // 注释只在预设正文范围内剥离（include 本身已经是干净文本）
        var body = FixSelfReferentialDeclarations(StripComments(ConvertLinefeeds(presetShaderText)));

        if ((fixes & ShaderCompatFix.MacroShadowing) != 0)
        {
            body = FixMacroShadowing(body, CollectObjectMacroNames(includeText), applied);
        }

        if ((fixes & ShaderCompatFix.PrecisionDemotion) != 0)
        {
            body = DemoteDoublePrecision(body, applied);
        }

        if ((fixes & ShaderCompatFix.NonFiniteLiterals) != 0)
        {
            body = ReplaceNonFiniteLiterals(body, applied);
        }

        var builtinDefines = (fixes & ShaderCompatFix.ProjectMBuiltins) != 0
            ? BuildBuiltinDefines(body, applied)
            : string.Empty;

        notes = applied;

        string prelude;
        try
        {
            // 只声明正文真正用到的常量/sampler：ps_2_0 只有 32 个常量寄存器和 16 个采样器，
            // 全量声明会直接超限。内建宏体里也会引用 sampler_fft / _mouse，要一起参与收集。
            var scanText = builtinDefines.Length == 0 ? includeText : includeText + "\n" + builtinDefines;
            var used = ShaderPrelude.CollectUsedNames(scanText, body, out var usedSamplers);
            prelude = ShaderPrelude.AddExplicitRegisters(includeText, used, usedSamplers);

            // 兼容层用到的 _mouse / sampler_fft 不在 include.fx 里，得自己补声明。
            if (builtinDefines.Contains("_mouse", StringComparison.Ordinal))
            {
                // 排在本 shader 实际用到的常量之后，不会和已有寄存器撞车。
                prelude += $"\nfloat4 _mouse : register(c{ShaderPrelude.UsedRegisterCount(used)});\n";
            }

            if (builtinDefines.Contains("sampler_fft", StringComparison.Ordinal)
                && ShaderPrelude.TryGetSamplerStage(usedSamplers, "sampler_fft", out var fftStage))
            {
                prelude += $"\nsampler2D sampler_fft : register(s{fftStage});\n";
            }

            // 预设自己声明的纹理 sampler（sampler_fw_clouds 等）接在 include 的采样器之后编号，
            // 运行时才能按寄存器号把 textures/ 里的图片绑上去。
            body = AnnotatePresetSamplers(body, usedSamplers);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return null;
        }

        var defines = DefinesFor(kind) + builtinDefines;
        var text = prelude + defines + body;
        var shaderStart = prelude.Length + defines.Length;

        var bodyPos = text.IndexOf("shader_body", shaderStart, StringComparison.Ordinal);
        if (bodyPos < 0)
        {
            error = "shader 里找不到 shader_body";
            return null;
        }

        // 把 shader_body 换成空格（保持字符偏移，方便定位编译错误）
        text = string.Concat(text.AsSpan(0, bodyPos), new string(' ', "shader_body".Length), text.AsSpan(bodyPos + "shader_body".Length));

        var signature = $"void {EntryPoint}( {(kind == MilkShaderKind.Warp ? WarpParams : CompParams)} )\n";
        text = string.Concat(text.AsSpan(0, bodyPos), signature, text.AsSpan(bodyPos));

        var brace = text.IndexOf('{', bodyPos + signature.Length);
        if (brace < 0)
        {
            error = "shader_body 之后找不到 {";
            return null;
        }

        text = string.Concat(text.AsSpan(0, brace + 1), "\n", FirstLine, text.AsSpan(brace + 1));

        var lastBrace = text.LastIndexOf('}');
        if (lastBrace < 0 || lastBrace <= brace)
        {
            error = "shader 里找不到成对的 }";
            return null;
        }

        return string.Concat(text.AsSpan(0, lastBrace), " ", LastLine, "\n}\n");
    }

    /// <summary>
    /// 给预设自己声明的纹理 sampler（<c>sampler sampler_fw_clouds;</c>）补 <c>: register(sN)</c>。
    /// </summary>
    /// <remarks>
    /// 原版靠 <c>ID3DXConstantTable</c> 反射出每个 sampler 的寄存器号，再按名字去
    /// <c>textures\&lt;根名&gt;.jpg|png|...</c> 加载图片（<c>plugin.cpp:2949-3224</c>）。
    /// 这里没有反射，所以必须自己写死寄存器号 —— 否则运行时既不知道该绑哪一级，
    /// 也就绑不上图：comp 输出会全黑（<c>Chrome - MilkDrop2077 001s/004</c> 就是这种情况）。
    /// <para>
    /// 预设有时先给 sampler 起别名：<c>#define iChannel0 sampler_stoy_london</c> 再
    /// <c>sampler iChannel0;</c>（预处理器会展开成 <c>sampler sampler_stoy_london;</c>）。
    /// 这里把声明里的别名解析成真名，运行时才能直接按名字找纹理。
    /// </para>
    /// </remarks>
    private static string AnnotatePresetSamplers(string body, IReadOnlySet<string> usedSamplers)
    {
        if (!body.Contains("sampler", StringComparison.Ordinal))
        {
            return body;
        }

        var macros = ParseObjectMacros(body);
        var stage = ShaderPrelude.UsedSamplerCount(usedSamplers);
        var assigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return PresetSamplerDeclarationRegex().Replace(body, match =>
        {
            var name = ResolveObjectMacro(match.Groups["name"].Value, macros);

            if (ShaderSamplers.TryGet(name, out _))
            {
                // include.fx 里的 sampler 由 prelude 负责，保持原样
                return match.Value;
            }

            if (!assigned.TryGetValue(name, out var slot))
            {
                slot = assigned[name] = stage++;
            }

            return $"{match.Groups["indent"].Value}{match.Groups["type"].Value} {name} : register(s{slot});";
        });
    }

    /// <summary>正文里的对象式宏（跳过 <c>#define F(x)</c> 这种函数式宏）。</summary>
    private static Dictionary<string, string> ParseObjectMacros(string body)
    {
        var macros = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("#define", StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line["#define".Length..].TrimStart();
            var end = 0;
            while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_'))
            {
                end++;
            }

            if (end == 0 || (end < rest.Length && rest[end] == '('))
            {
                continue;
            }

            var value = rest[end..].Trim();
            if (value.Length > 0)
            {
                macros[rest[..end]] = value;
            }
        }

        return macros;
    }

    /// <summary>跟着对象式宏往下展开，直到得到一个普通标识符。</summary>
    private static string ResolveObjectMacro(string name, Dictionary<string, string> macros)
    {
        for (var guard = 0; guard < 8 && macros.TryGetValue(name, out var value); guard++)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || !IsIdentifierStart(trimmed[0]))
            {
                return name;
            }

            var end = 1;
            while (end < trimmed.Length && IsIdentifierPart(trimmed[end]))
            {
                end++;
            }

            if (end != trimmed.Length)
            {
                // 不是单个标识符（如 sampler_foo.Bar）就不敢替换
                return name;
            }

            name = trimmed;
        }

        return name;
    }

    /// <summary>正文里预设自己声明的 sampler（<c>sampler</c> / <c>sampler2D</c> / <c>sampler3D</c>）。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<indent>[ \t]*)(?<type>sampler(?:2D|3D)?)\s+(?<name>[A-Za-z_]\w*)\s*;",
        System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex PresetSamplerDeclarationRegex();

    /// <summary>
    /// 把 <c>float3 ret1 = ret1;</c> 这种自引用声明降级为赋值。
    /// </summary>
    /// <remarks>
    /// 这是预设里的常见笔误（如 crystal palace）：新声明的局部变量用自己初始化，
    /// 新版 d3dcompiler 会直接报 X4000，而旧 D3DX 编译器的流分析不会拦。
    /// </remarks>
    private static string FixSelfReferentialDeclarations(string text)
        => SelfReferentialDeclarationRegex().Replace(text, "${indent}${name} = ${name};");

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<indent>\s*)(?:float[234]?|int|bool)\s+(?<name>[A-Za-z_]\w*)\s*=\s*\k<name>\s*;",
        System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex SelfReferentialDeclarationRegex();

    // ---------------- 宏名遮蔽兼容层（默认关闭） ----------------

    private static readonly string[] TypeKeywords =
    [
        "float", "float2", "float3", "float4",
        "float2x2", "float3x3", "float4x4",
        "int", "int2", "int3", "int4",
        "uint", "bool", "half", "double",
    ];

    /// <summary>收集 include 里的对象式宏名（跳过 <c>#define F(x)</c> 这种函数式宏）。</summary>
    /// <remarks>
    /// include 文本来自 <see cref="ShaderTemplates"/>，换行是
    /// <see cref="LinefeedControlChar"/>（<c>0x01</c>），先换成 <c>\n</c>。
    /// </remarks>
    public static HashSet<string> CollectObjectMacroNames(string includeText)
    {
        includeText = includeText.Replace(LinefeedControlChar, '\n');

        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < includeText.Length)
        {
            var lineEnd = includeText.IndexOf('\n', index);
            if (lineEnd < 0)
            {
                lineEnd = includeText.Length;
            }

            var line = includeText.AsSpan(index, lineEnd - index).TrimStart();
            if (line.StartsWith("#define", StringComparison.Ordinal))
            {
                var rest = line[7..].TrimStart();
                var end = 0;
                while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_'))
                {
                    end++;
                }

                // 紧跟 '(' 的是函数式宏，不参与遮蔽判断
                if (end > 0 && (end >= rest.Length || rest[end] != '('))
                {
                    names.Add(rest[..end].ToString());
                }
            }

            index = lineEnd + 1;
        }

        return names;
    }

    /// <summary>
    /// 把与 include 宏同名的局部变量改名（<c>aspect</c> → <c>aspect__md</c>）。
    /// </summary>
    /// <remarks>
    /// include.fx 里有 <c>#define aspect _c0</c> 这类对象式宏。预设若写
    /// <c>float aspect = aspect.x/aspect.y;</c>，预处理器会把**变量名**也替换掉，变成
    /// <c>float _c0 = _c0.x/_c0.y;</c>：右侧的 <c>_c0</c> 指向刚声明的 float，
    /// 标量允许 <c>.x</c> 但不允许 <c>.y</c>，于是报 X3018。
    /// 原版（同一个 include.fx + 同一个 D3DX 编译器）同样失败并回退默认 shader。
    ///
    /// 这里只改"声明处 + 同一块作用域内的引用"，声明语句的初始化表达式保持原样（仍指向宏），
    /// 正好对应 C/HLSL 里"标识符在声明符之后才进入作用域"的语义。
    /// </remarks>
    private static string FixMacroShadowing(string body, HashSet<string> macroNames, List<string> notes)
    {
        if (macroNames.Count == 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length + 64);
        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        var registered = new List<string>();
        var scopes = new Stack<int>();
        var i = 0;

        while (i < body.Length)
        {
            var ch = body[i];

            // 预处理指令整行不动（否则会改坏 #define / #include）
            if (ch == '#' && IsLineStart(body, i))
            {
                var newline = body.IndexOf('\n', i);
                if (newline < 0)
                {
                    sb.Append(body, i, body.Length - i);
                    break;
                }

                sb.Append(body, i, newline - i + 1);
                i = newline + 1;
                continue;
            }

            if (ch == '{')
            {
                scopes.Push(registered.Count);
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == '}')
            {
                if (scopes.Count > 0)
                {
                    var mark = scopes.Pop();
                    while (registered.Count > mark)
                    {
                        active.Remove(registered[^1]);
                        registered.RemoveAt(registered.Count - 1);
                    }
                }

                sb.Append(ch);
                i++;
                continue;
            }

            if (!IsIdentifierStart(ch))
            {
                sb.Append(ch);
                i++;
                continue;
            }

            var start = i;
            while (i < body.Length && IsIdentifierPart(body[i]))
            {
                i++;
            }

            var name = body[start..i];

            if (macroNames.Contains(name) && IsDeclarationNameContext(sb, body, i))
            {
                var newName = MakeUniqueName(name + "__md", body);
                sb.Append(newName);
                notes.Add($"{name} -> {newName}");

                if (!active.ContainsKey(name))
                {
                    active[name] = newName;
                    registered.Add(name);
                }

                // 声明语句的初始化表达式里仍要指向宏，整句原样拷到分号
                var semi = body.IndexOf(';', i);
                var brace = body.IndexOf('}', i);
                if (semi >= 0 && (brace < 0 || semi < brace))
                {
                    sb.Append(body, i, semi - i + 1);
                    i = semi + 1;
                }

                continue;
            }

            if (active.TryGetValue(name, out var mapped) && !PrecededByDot(sb))
            {
                sb.Append(mapped);
                continue;
            }

            sb.Append(name);
        }

        return sb.ToString();
    }

    /// <summary>标识符前面是类型关键字（可带 const），后面是 <c>= ; , [</c> → 认为它是声明名。</summary>
    private static bool IsDeclarationNameContext(StringBuilder sb, string body, int indexAfterName)
    {
        var previous = LastIdentifier(sb);
        if (previous == "const")
        {
            previous = LastIdentifierBefore(sb, sb.Length);
        }

        if (Array.IndexOf(TypeKeywords, previous) < 0)
        {
            return false;
        }

        var next = NextNonSpace(body, indexAfterName);
        if (next is not ('=' or ';' or ',' or '['))
        {
            return false;
        }

        if (next == '=')
        {
            // "x == y" 是比较，不是声明
            var equals = body.IndexOf('=', indexAfterName);
            if (equals >= 0 && equals + 1 < body.Length && body[equals + 1] == '=')
            {
                return false;
            }
        }

        return true;
    }

    private static string LastIdentifier(StringBuilder sb) => LastIdentifierBefore(sb, sb.Length);

    private static string LastIdentifierBefore(StringBuilder sb, int limit)
    {
        var end = Math.Min(limit, sb.Length);
        while (end > 0 && char.IsWhiteSpace(sb[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && IsIdentifierPart(sb[start - 1]))
        {
            start--;
        }

        return sb.ToString(start, end - start);
    }

    private static bool PrecededByDot(StringBuilder sb)
    {
        var end = sb.Length;
        while (end > 0 && char.IsWhiteSpace(sb[end - 1]))
        {
            end--;
        }

        return end > 0 && sb[end - 1] == '.';
    }

    private static char NextNonSpace(string text, int index)
    {
        for (var k = index; k < text.Length; k++)
        {
            if (!char.IsWhiteSpace(text[k]))
            {
                return text[k];
            }
        }

        return '\0';
    }

    private static bool IsLineStart(string text, int index)
    {
        for (var k = index - 1; k >= 0; k--)
        {
            if (text[k] == '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(text[k]))
            {
                return false;
            }
        }

        return true;
    }

    private static string MakeUniqueName(string candidate, string body)
    {
        var name = candidate;
        while (body.Contains(name, StringComparison.Ordinal))
        {
            name += "_";
        }

        return name;
    }

    // ---------------- 其它兼容改写（默认关闭） ----------------

    /// <summary>
    /// <c>get_fft(pos)</c>：<paramref name="pos"/> 是 0..1 的归一化频谱位置，返回 0..1 的幅度。
    /// </summary>
    /// <remarks>
    /// MD3 PRO 专有内建（原版开源版 include.fx 里没有，所以这些预设在原版也编译失败）。
    /// 这里映射到 <c>sampler_fft</c>：512×1 的频谱纹理，由 <c>Visualizer</c> 每帧上传。
    /// 0.9980469 = 511/512、0.00097656 = 1/1024，作用是把 0..1 映射到 512 个纹素的中心。
    /// </remarks>
    private const string GetFftDefine =
        "#define get_fft(pos) (tex2D(sampler_fft, float2(clamp(pos, 0.0, 1.0) * 0.9980469 + 0.00097656, 0.5)).x)\n";

    /// <summary><c>get_fft_hz(freq)</c>：按频率（Hz，按 22050 的 Nyquist 归一化）取频谱幅度。</summary>
    private const string GetFftHzDefine =
        "#define get_fft_hz(freq) (tex2D(sampler_fft, float2(clamp(freq * (1.0 / 22050.0), 0.0, 1.0) * 0.9980469 + 0.00097656, 0.5)).x)\n";

    /// <summary><c>mouse</c>：<c>float4(x, y, 0, 0)</c>，xy 是归一化到 -1..1 的光标位置。</summary>
    private const string MouseDefine = "#define mouse _mouse\n";

    /// <summary>按正文是否真的用到，生成内建宏定义（避免给不相关的 shader 塞宏）。</summary>
    private static string BuildBuiltinDefines(string body, List<string> notes)
    {
        var builder = new StringBuilder(192);

        if (UsesIdentifier(body, "mouse"))
        {
            builder.Append(MouseDefine);
            notes.Add("mouse -> _mouse");
        }

        if (UsesIdentifier(body, "get_fft_hz"))
        {
            builder.Append(GetFftHzDefine);
            notes.Add("get_fft_hz -> sampler_fft");
        }

        if (UsesIdentifier(body, "get_fft"))
        {
            builder.Append(GetFftDefine);
            notes.Add("get_fft -> sampler_fft");
        }

        return builder.ToString();
    }

    /// <summary>标识符是否作为独立 token 出现（跳过 <c>q.mouse</c> 这种成员访问与更长的名字）。</summary>
    private static bool UsesIdentifier(string body, string name)
    {
        var index = 0;
        while (index < body.Length)
        {
            var found = body.IndexOf(name, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            var before = found == 0 ? '\0' : body[found - 1];
            var after = found + name.Length < body.Length ? body[found + name.Length] : '\0';
            if (before != '.' && !IsIdentifierPart(before) && !IsIdentifierPart(after))
            {
                return true;
            }

            index = found + name.Length;
        }

        return false;
    }

    /// <summary>
    /// <c>double</c>/<c>double2..4</c> → <c>float</c> 系列（<c>X3684</c>：D3D9 像素着色器不支持双精度）。
    /// </summary>
    /// <remarks>
    /// 语义无损：ps_2_0/ps_3_0 下所有运算本来就是单精度，改成 <c>float</c> 只是让它编译得过。
    /// </remarks>
    private static string DemoteDoublePrecision(string body, List<string> notes)
    {
        if (!body.Contains("double", StringComparison.Ordinal))
        {
            return body;
        }

        var replaced = DoubleTypeRegex().Replace(body, "float${rank}");
        if (!string.Equals(replaced, body, StringComparison.Ordinal))
        {
            notes.Add("double -> float");
        }

        return replaced;
    }

    /// <summary>
    /// 非有限字面量（<c>1.#INF</c> 等）换成有限大数；除零字面量换成 <c>1e-20</c>。
    /// </summary>
    /// <remarks>
    /// <c>X4579 NaN and infinity literals not allowed by shader model</c>。
    /// 典型来源是预设写 <c>ret /= float3(0.0,-3.0,-1.6)</c>：编译期折叠出 inf，新版编译器直接报错
    /// （旧 D3DX 只是 warning）。换成 1e-20 后结果仍然是"极大值"，输出被 clamp 后与原意一致。
    /// </remarks>
    private static string ReplaceNonFiniteLiterals(string body, List<string> notes)
    {
        var text = body;

        if (text.Contains(".#", StringComparison.Ordinal))
        {
            var replaced = NonFiniteLiteralRegex().Replace(text, "${sign}3.402823466e38");
            if (!string.Equals(replaced, text, StringComparison.Ordinal))
            {
                notes.Add("非有限字面量 -> 3.402823466e38");
                text = replaced;
            }
        }

        if (text.Contains('/'))
        {
            var vector = DivisionByVectorZeroRegex().Replace(text, RewriteZeroVectorDivisor);
            var scalar = DivisionByZeroRegex().Replace(vector, "${op}1e-20");

            if (!string.Equals(scalar, text, StringComparison.Ordinal))
            {
                notes.Add("除零字面量 -> 1e-20");
            }

            text = scalar;
        }

        return FixDegenerateSmoothstep(text, notes);
    }

    /// <summary>
    /// <c>smoothstep(e0, e0, x)</c>（两个边界是同一个字面量）→ 第二边界加 <c>1e-6</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 编译期会把 <c>1/(e1-e0)</c> 折成 inf，然后触发 <c>X4579: NaN and infinity literals</c>。
    /// 加上 1e-6 之后是"极窄台阶"，而原版（d3dx9，没有这个检查）本来就把它算成
    /// <c>t = saturate(x/0)</c> —— 正数 1、负数 0，视觉上也是台阶，所以这个改写不改变观感。
    /// </para>
    /// <para>
    /// shadertoy 转译过来的预设常给 smoothstep 起别名（<c>#define S(a,b,t) smoothstep(a,b,t)</c>），
    /// 所以先从正文的 <c>#define</c> 里找出这类别名，再连别名一起改写。
    /// 实例：<c>BigWings + IkeC - MilkDrop2077 - Heartless3.milk</c> 的 <c>story = S(0, 0, T*2);</c>。
    /// </para>
    /// </remarks>
    private static string FixDegenerateSmoothstep(string body, List<string> notes)
    {
        if (!body.Contains("smoothstep", StringComparison.Ordinal))
        {
            return body;
        }

        var aliases = new List<string> { "smoothstep" };

        foreach (Match define in SmoothstepAliasRegex().Matches(body))
        {
            var name = define.Groups["name"].Value;
            if (!aliases.Contains(name, StringComparer.Ordinal))
            {
                aliases.Add(name);
            }
        }

        var text = body;
        var changed = false;

        foreach (var alias in aliases)
        {
            var pattern = new Regex(
                $@"(?<call>\b{Regex.Escape(alias)}\s*\()\s*(?<edge>-?\d+(?:\.\d+)?)[fF]?\s*,\s*\k<edge>[fF]?\s*,",
                RegexOptions.CultureInvariant);

            text = pattern.Replace(text, match =>
            {
                changed = true;
                var edge = match.Groups["edge"].Value;
                return $"{match.Groups["call"].Value}{edge}, {edge} + 1e-6,";
            });
        }

        if (changed)
        {
            notes.Add("退化 smoothstep 边界 -> +1e-6");
        }

        return text;
    }

    private static string RewriteZeroVectorDivisor(Match match)
    {
        var args = match.Groups["args"].Value;
        if (!args.Contains('0', StringComparison.Ordinal))
        {
            return match.Value;
        }

        var parts = args.Split(',');
        var changed = false;

        for (var i = 0; i < parts.Length; i++)
        {
            if (IsZeroLiteral(parts[i]))
            {
                parts[i] = "1e-20";
                changed = true;
            }
        }

        return changed
            ? $"{match.Groups["op"].Value}float{match.Groups["n"].Value}({string.Join(',', parts)})"
            : match.Value;
    }

    private static bool IsZeroLiteral(string text)
    {
        var value = text.Trim().TrimStart('-').TrimEnd('f', 'F').Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var hasZero = false;
        foreach (var ch in value)
        {
            if (ch == '0')
            {
                hasZero = true;
            }
            else if (ch != '.')
            {
                return false;
            }
        }

        return hasZero;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\bdouble(?<rank>[234])?\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DoubleTypeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<sign>-)?\s*1\.#(?:INF|IND|QNAN|SNAN|NAN)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex NonFiniteLiteralRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<op>/=?\s*)float(?<n>[234])\s*\((?<args>[^()]*)\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DivisionByVectorZeroRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<op>/=?\s*)-?0+(?:\.0*)?[fF]?(?![\w.])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DivisionByZeroRegex();

    /// <summary>正文里把 <c>smoothstep</c> 起了别名的函数式宏（如 <c>#define S(a,b,t) smoothstep(a,b,t)</c>）。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"#define\s+(?<name>[A-Za-z_]\w*)\s*\([^)\n]*\)[^\n]*\bsmoothstep\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SmoothstepAliasRegex();

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_';

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    /// <summary>预设里的 <c>0x01</c> 换行控制符还原成 CRLF。</summary>
    private static string ConvertLinefeeds(string text)
    {
        if (text.IndexOf(LinefeedControlChar) < 0)
        {
            return text;
        }

        return text.Replace("\u0001", "\r\n");
    }

    /// <summary>
    /// 去掉 <c>//</c> 与 <c>/* */</c> 注释（保留换行，便于编译错误定位到原行）。
    /// </summary>
    private static string StripComments(ReadOnlySpan<char> text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];

            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                    {
                        result.Append('\n');
                    }

                    i++;
                }

                i = Math.Min(i + 2, text.Length);
            }
            else
            {
                result.Append(ch);
                i++;
            }
        }

        return result.ToString();
    }
}
