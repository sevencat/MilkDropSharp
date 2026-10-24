using System.Text;

namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// 在 JSON **原文**上做最小改写：只替换"整段字符串值恰好等于 X"的字面量。
/// </summary>
/// <remarks>
/// <para>
/// 下载后必须把 <c>/media/...</c> 这类站点路径改成 <c>textures/xxx</c>（<c>shaderToyIssue.md</c> S8-2），
/// 但又不能把整个工程反序列化再序列化（会丢未知字段、改字段顺序、二次反转义，S8-3）。
/// 折中方案：文本级替换，但只动"字符串值完全相等"的位置 —— 结构、空白、字段顺序、
/// <c>\n</c> 转义全部保持原样，改完的文件还能直接导回 shadertoy。
/// </para>
/// <para>
/// 匹配前会把 JSON 字符串解码（<c>\/</c> → <c>/</c> 等），所以真实导出里的
/// <c>"\/media\/a\/xxx.png"</c> 也能命中 <c>/media/a/xxx.png</c>。
/// </para>
/// </remarks>
public static class JsonTextRewriter
{
    /// <summary>
    /// 把 <paramref name="replacements"/> 里的"旧字符串值 → 新字符串值"应用到 JSON 原文。
    /// </summary>
    /// <param name="json">JSON 原文。</param>
    /// <param name="replacements">旧值（已解码）→ 新值。</param>
    /// <param name="replaced">实际替换了几处。</param>
    public static string ReplaceStringValues(
        string json,
        IReadOnlyDictionary<string, string> replacements,
        out int replaced)
    {
        replaced = 0;

        if (string.IsNullOrEmpty(json) || replacements.Count == 0)
        {
            return json;
        }

        var builder = new StringBuilder(json.Length + 64);
        var decoded = new StringBuilder(64);

        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] != '"')
            {
                builder.Append(json[i]);
                continue;
            }

            int start = i;
            decoded.Clear();
            int cursor = i + 1;
            bool closed = false;

            while (cursor < json.Length)
            {
                char c = json[cursor];

                if (c == '\\' && cursor + 1 < json.Length)
                {
                    DecodeEscape(json, ref cursor, decoded);
                    continue;
                }

                if (c == '"')
                {
                    closed = true;
                    break;
                }

                decoded.Append(c);
                cursor++;
            }

            if (!closed)
            {
                // 坏掉的 JSON：原样吐出去，交给解析器报错
                builder.Append(json.AsSpan(start));
                return builder.ToString();
            }

            if (replacements.TryGetValue(decoded.ToString(), out string? replacement))
            {
                builder.Append('"');
                AppendEscaped(builder, replacement);
                builder.Append('"');
                replaced++;
            }
            else
            {
                builder.Append(json, start, cursor + 1 - start);
            }

            i = cursor;
        }

        return builder.ToString();
    }

    /// <summary>解码一个 JSON 字符串转义（<paramref name="cursor"/> 指向反斜杠）。</summary>
    private static void DecodeEscape(string json, ref int cursor, StringBuilder builder)
    {
        char kind = json[cursor + 1];
        switch (kind)
        {
            case 'n':
                builder.Append('\n');
                cursor += 2;
                break;
            case 'r':
                builder.Append('\r');
                cursor += 2;
                break;
            case 't':
                builder.Append('\t');
                cursor += 2;
                break;
            case 'b':
                builder.Append('\b');
                cursor += 2;
                break;
            case 'f':
                builder.Append('\f');
                cursor += 2;
                break;
            case 'u' when cursor + 5 < json.Length &&
                          ushort.TryParse(json.AsSpan(cursor + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort code):
                builder.Append((char)code);
                cursor += 6;
                break;
            default:
                // \" \\ \/ 等：吃转义符，保留目标字符
                builder.Append(kind);
                cursor += 2;
                break;
        }
    }

    /// <summary>把新值写成 JSON 字符串内容（只转义必须转义的字符）。</summary>
    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }
    }
}
