using System.Text;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// 把"与公共头里的对象式宏同名"的局部变量改名，绕开宏替换。
/// </summary>
/// <remarks>
/// <para>
/// 公共头里有 <c>#define aspect _c0</c> 这类对象式宏。预设只要写
/// <c>float aspect = aspect.x/aspect.y;</c>，预处理器会把**变量名**也替换掉，变成
/// <c>float(_c0) =(_c0).x/(_c0).y;</c>，于是 "Syntax error: expected identifier near '('"。
/// 同一个头 + 同一个 projectM 预处理器在原版里同样失败（退回默认管线 → 黑屏），
/// 只有 MilkDrop3 能跑（它改了自己的 include）。这里做兼容：把声明名改成
/// <c>aspect__md</c>，并把它在同一块作用域内的后续引用一起改名。
/// </para>
/// <para>
/// 声明语句的初始化表达式保持原样（仍指向宏），对应 C/HLSL 里"标识符在声明符之后
/// 才进入作用域"的语义。
/// </para>
/// <para>
/// 与 <c>MilkDrop.Classic.Render.D3D9.ShaderAssembler.FixMacroShadowing</c> 同源，
/// 那套实现已经在 D3D9 分支上验证过。
/// </para>
/// </remarks>
internal static class MacroShadowingFixer
{
    /// <summary>改名后缀。</summary>
    private const string NameSuffix = "__md";

    /// <summary>可以作为"声明名"前一个 token 的类型关键字。</summary>
    private static readonly string[] TypeKeywords =
    [
        "float", "float2", "float3", "float4",
        "float2x2", "float3x3", "float4x4",
        "float2x3", "float2x4", "float3x2", "float3x4", "float4x2", "float4x3",
        "int", "int2", "int3", "int4",
        "uint", "bool", "half", "double",
    ];

    /// <summary>收集头文件里的对象式宏名（跳过 <c>#define F(x)</c> 这种函数式宏）。</summary>
    public static HashSet<string> CollectObjectMacroNames(string header)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;

        while (index < header.Length)
        {
            int lineEnd = header.IndexOf('\n', index);
            if (lineEnd < 0)
            {
                lineEnd = header.Length;
            }

            ReadOnlySpan<char> line = header.AsSpan(index, lineEnd - index).TrimStart();
            if (line.StartsWith("#define", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> rest = line[7..].TrimStart();
                int end = 0;
                while (end < rest.Length && IsIdentifierPart(rest[end]))
                {
                    end++;
                }

                // 紧跟 '(' 的是函数式宏，不参与遮蔽判断。
                if (end > 0 && (end >= rest.Length || rest[end] != '('))
                {
                    names.Add(rest[..end].ToString());
                }
            }

            index = lineEnd + 1;
        }

        return names;
    }

    /// <summary>把与宏同名的局部变量改名；没有命中时原样返回。</summary>
    public static string Apply(string body, IReadOnlyCollection<string> macroNames)
    {
        if (macroNames.Count == 0 || body.Length == 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length + 64);

        // 当前作用域里已改名的变量：宏名 → 新名字。
        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        var registered = new List<string>();
        var scopeMarks = new Stack<int>();
        int i = 0;

        while (i < body.Length)
        {
            char ch = body[i];

            // 预处理指令整行不动（否则会改坏 #define）。
            if (ch == '#' && IsLineStart(body, i))
            {
                int newline = body.IndexOf('\n', i);
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
                scopeMarks.Push(registered.Count);
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == '}')
            {
                if (scopeMarks.Count > 0)
                {
                    int mark = scopeMarks.Pop();
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

            int start = i;
            while (i < body.Length && IsIdentifierPart(body[i]))
            {
                i++;
            }

            string name = body[start..i];

            if (Contains(macroNames, name) && IsDeclarationNameContext(sb, body, i))
            {
                string newName = MakeUniqueName(name + NameSuffix, body);
                sb.Append(newName);

                if (!active.ContainsKey(name))
                {
                    active[name] = newName;
                    registered.Add(name);
                }

                // 声明语句的初始化表达式里仍要指向宏，整句原样拷到分号。
                int semi = body.IndexOf(';', i);
                int brace = body.IndexOf('}', i);
                if (semi >= 0 && (brace < 0 || semi < brace))
                {
                    sb.Append(body, i, semi - i + 1);
                    i = semi + 1;
                }

                continue;
            }

            if (active.TryGetValue(name, out string? mapped) && !PrecededByDot(sb))
            {
                sb.Append(mapped);
                continue;
            }

            sb.Append(name);
        }

        return sb.ToString();
    }

    /// <summary>标识符前面是类型关键字（可带 const），后面是 <c>= ; , [</c>，认为它是声明名。</summary>
    private static bool IsDeclarationNameContext(StringBuilder sb, string body, int indexAfterName)
    {
        string previous = LastIdentifier(sb);
        if (previous == "const")
        {
            previous = LastIdentifierBefore(sb, sb.Length);
        }

        if (Array.IndexOf(TypeKeywords, previous) < 0)
        {
            return false;
        }

        char next = NextNonSpace(body, indexAfterName);
        if (next is not ('=' or ';' or ',' or '['))
        {
            return false;
        }

        if (next == '=')
        {
            // "x == y" 是比较，不是声明。
            int equals = body.IndexOf('=', indexAfterName);
            if (equals >= 0 && equals + 1 < body.Length && body[equals + 1] == '=')
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(IReadOnlyCollection<string> names, string name)
    {
        foreach (string candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string LastIdentifier(StringBuilder sb) => LastIdentifierBefore(sb, sb.Length);

    private static string LastIdentifierBefore(StringBuilder sb, int limit)
    {
        int end = Math.Min(limit, sb.Length);
        while (end > 0 && char.IsWhiteSpace(sb[end - 1]))
        {
            end--;
        }

        int start = end;
        while (start > 0 && IsIdentifierPart(sb[start - 1]))
        {
            start--;
        }

        return sb.ToString(start, end - start);
    }

    private static bool PrecededByDot(StringBuilder sb)
    {
        int end = sb.Length;
        while (end > 0 && char.IsWhiteSpace(sb[end - 1]))
        {
            end--;
        }

        return end > 0 && sb[end - 1] == '.';
    }

    private static char NextNonSpace(string text, int index)
    {
        for (int k = index; k < text.Length; k++)
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
        for (int k = index - 1; k >= 0; k--)
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
        string name = candidate;
        while (body.Contains(name, StringComparison.Ordinal))
        {
            name += "_";
        }

        return name;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
