namespace MilkDrop.Classic.Visualizer;

/// <summary>
/// 复刻 MilkDrop 的表达式预处理（C 版证据：
/// <c>state.cpp:1071 ReadCode</c> + <c>state.cpp:1525 StripLinefeedCharsAndComments</c>）。
/// </summary>
/// <remarks>
/// 两条关键规则，写错会让预设表达式大面积编译失败：
/// <list type="number">
/// <item>多个 <c>per_frame_N=</c> 行用 <see cref="LinefeedControlChar"/> 连接；</item>
/// <item>预处理时该控制符被<b>直接删除</b>（不是替换成空格）。预设会故意把标识符拆到两行，
/// 例如 <c>martin - liquid gold.milk</c> 的 <c>per_frame_22=k1 =  is_</c> +
/// <c>per_frame_23=beat*equal(index%2,0);</c> 拼成 <c>is_beat</c>。</item>
/// </list>
/// </remarks>
public static class EelPreprocessor
{
    /// <summary>行分隔控制符（C 版的 LINEFEED_CONTROL_CHAR）。</summary>
    public const char LinefeedControlChar = '\u0001';

    /// <summary>键缺失时 C 版使用的哨兵串。</summary>
    public const string MissingKeySentinel = "~!@#$";

    /// <summary>
    /// 把 <c>{prefix}{1..N}</c> 这些键按序拼成一条代码串（对应 C 版 ReadCode）。
    /// 遇到缺失的键立即停止；行首反引号会被去掉。
    /// </summary>
    public static string JoinContiguousLines(string prefix, IReadOnlyDictionary<string, string> keys)
        => JoinContiguousLines(prefix, key => keys.TryGetValue(key, out var value) ? value : null);

    /// <summary>
    /// 同 <see cref="JoinContiguousLines(string, IReadOnlyDictionary{string, string})"/>，
    /// 但查找交给调用方 —— 预设导入需要区分大小写（C 版用 <c>strcmp</c>）。
    /// </summary>
    public static string JoinContiguousLines(string prefix, Func<string, string?> tryGet)
    {
        var sb = new System.Text.StringBuilder();
        for (var n = 1; ; n++)
        {
            var line = tryGet(prefix + n);
            if (line is null)
            {
                break;
            }

            sb.Append(line.Length > 0 && line[0] == '`' ? line[1..] : line);
            sb.Append(LinefeedControlChar);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 删除行分隔控制符，并剥离 <c>//</c> 与 <c>\\</c> 注释（注释到行尾，即到控制符为止）。
    /// </summary>
    public static string StripLinefeedsAndComments(string source)
    {
        var buffer = new System.Text.StringBuilder(source.Length);
        var inComment = false;

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (i + 1 < source.Length &&
                ((c == '/' && source[i + 1] == '/') || (c == '\\' && source[i + 1] == '\\')))
            {
                inComment = true;
            }

            if (c == LinefeedControlChar)
            {
                inComment = false;
            }

            if (!inComment && c != LinefeedControlChar)
            {
                buffer.Append(c);
            }
        }

        return buffer.ToString();
    }

    /// <summary>ReadCode + StripLinefeedCharsAndComments 的组合。</summary>
    public static string Prepare(string prefix, IReadOnlyDictionary<string, string> keys)
        => StripLinefeedsAndComments(JoinContiguousLines(prefix, keys));
}
