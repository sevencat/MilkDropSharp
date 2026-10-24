using System.Text;

namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// 从 shadertoy.com 的作品页面里抠出内嵌的工程 JSON。
/// </summary>
/// <remarks>
/// <para>
/// 页面里把它塞在一个脚本变量里（<c>var gShaderToy = {...};</c> 或
/// <c>gShaderToy = JSON.parse('...')</c>），不是干净的接口响应（<c>shaderToyIssue.md</c> S8-1）。
/// 所以这里做两件事：先定位变量，再按"括号配对 / 引号配对"截取 —— 不能用"截到 <c>&lt;/script&gt;</c>"
/// 这类硬切，GLSL 代码里什么都可能出现。
/// </para>
/// <para>
/// 返回的 JSON 文本**不做格式化/重排**，原样交给下载器做最小改写（S8-3）。
/// </para>
/// </remarks>
public static class ShaderToyPage
{
    private const string VariableName = "gShaderToy";

    /// <summary>
    /// 从页面 HTML 里取出工程 JSON 文本。
    /// </summary>
    /// <param name="html">页面源码。</param>
    /// <param name="json">截取出的 JSON（原样文本）。</param>
    /// <param name="error">失败原因（给人看的）。</param>
    public static bool TryExtractProjectJson(string html, out string json, out string? error)
    {
        json = string.Empty;

        if (string.IsNullOrWhiteSpace(html))
        {
            error = "页面内容为空。";
            return false;
        }

        // 先按原始页面找；找不到再退回"HTML 实体反转义后的页面"（部分镜像/抓取工具会把引号写成 &quot;）
        if (TryExtract(html, out json, out string? firstError))
        {
            error = null;
            return true;
        }

        string unescaped = UnescapeHtmlEntities(html);
        if (!ReferenceEquals(unescaped, html) && TryExtract(unescaped, out json, out _))
        {
            error = null;
            return true;
        }

        error = firstError;
        return false;
    }

    /// <summary>
    /// 从页面里找缩略图地址（<c>&lt;meta property="og:image"&gt;</c>）。
    /// </summary>
    public static string? TryExtractThumbnailUrl(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        int index = html.IndexOf("og:image", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        // og:image 的标签里还有 content="..."；两者顺序不固定（property 在前是常态）
        int contentIndex = html.IndexOf("content=", index, StringComparison.OrdinalIgnoreCase);
        if (contentIndex < 0 || contentIndex - index > 400)
        {
            return null;
        }

        return ReadQuotedValue(html, contentIndex + "content=".Length);
    }

    /// <summary>把 <c>&amp;quot;</c> / <c>&amp;amp;</c> 之类还原（<c>&amp;amp;</c> 放最后）。</summary>
    public static string UnescapeHtmlEntities(string text) =>
        text
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#34;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    private static bool TryExtract(string html, out string json, out string? error)
    {
        json = string.Empty;

        string? lastError = null;
        int searchFrom = 0;

        while (searchFrom < html.Length)
        {
            int index = html.IndexOf(VariableName, searchFrom, StringComparison.Ordinal);
            if (index < 0)
            {
                error = lastError
                        ?? $"页面里没有找到 {VariableName}（可能是作品不存在、被删除或页面格式变了）。";
                return false;
            }

            int cursor = index + VariableName.Length;
            cursor = SkipWhitespace(html, cursor);

            // 跳过变量名后面可能紧跟的属性访问/方法（gShaderToy.foo），只认赋值
            if (cursor < html.Length && html[cursor] == '=')
            {
                cursor = SkipWhitespace(html, cursor + 1);

                // 形式一：JSON.parse('...')
                if (StartsWith(html, cursor, "JSON.parse"))
                {
                    if (TryExtractFromParseCall(html, cursor + "JSON.parse".Length, out json, out string? parseError))
                    {
                        error = null;
                        return true;
                    }

                    lastError = parseError;
                    searchFrom = index + VariableName.Length;
                    continue;
                }

                // 形式二：对象/数组字面量
                if (cursor < html.Length && (html[cursor] == '{' || html[cursor] == '['))
                {
                    if (TryExtractBalanced(html, cursor, out json, out string? balancedError))
                    {
                        json = UnescapeHtmlEntities(json);
                        error = null;
                        return true;
                    }

                    lastError = balancedError;
                    searchFrom = index + VariableName.Length;
                    continue;
                }
            }

            searchFrom = index + VariableName.Length;
        }

        error = lastError
                ?? $"页面里的 {VariableName} 不是可识别的工程数据（既不是字面量也不是 JSON.parse）。";
        return false;
    }

    /// <summary>从 <c>JSON.parse(</c> 之后开始，取引号里的 JS 字符串并解码转义。</summary>
    private static bool TryExtractFromParseCall(string html, int cursor, out string json, out string? error)
    {
        json = string.Empty;
        error = null;

        cursor = SkipWhitespace(html, cursor);
        if (cursor >= html.Length || html[cursor] != '(')
        {
            error = "JSON.parse 后面不是左括号。";
            return false;
        }

        cursor = SkipWhitespace(html, cursor + 1);
        if (cursor >= html.Length || (html[cursor] != '\'' && html[cursor] != '"'))
        {
            error = "JSON.parse 的参数不是字符串字面量。";
            return false;
        }

        char quote = html[cursor];
        cursor++;

        var builder = new StringBuilder(1 << 16);
        while (cursor < html.Length)
        {
            char c = html[cursor];

            if (c == '\\' && cursor + 1 < html.Length)
            {
                DecodeJsEscape(html, ref cursor, builder);
                continue;
            }

            if (c == quote)
            {
                json = UnescapeHtmlEntities(builder.ToString());

                if (json.Length == 0)
                {
                    error = "JSON.parse 的字符串是空的。";
                    return false;
                }

                return true;
            }

            builder.Append(c);
            cursor++;
        }

        error = "JSON.parse 的字符串没有结束引号。";
        return false;
    }

    /// <summary>从 <c>{</c>/<c>[</c> 开始做括号配对（尊重字符串与转义）。</summary>
    private static bool TryExtractBalanced(string html, int start, out string json, out string? error)
    {
        json = string.Empty;
        error = null;

        var stack = new Stack<char>();
        bool inString = false;

        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];

            if (inString)
            {
                if (c == '\\')
                {
                    i++; // 跳过被转义的字符（\" 与 \\ 都只看作一个字面量）
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;

                case '{':
                    stack.Push('}');
                    break;

                case '[':
                    stack.Push(']');
                    break;

                case '}' or ']':
                    if (stack.Count == 0 || stack.Pop() != c)
                    {
                        error = "工程 JSON 的括号不配对（页面可能被截断）。";
                        return false;
                    }

                    if (stack.Count == 0)
                    {
                        json = html[start..(i + 1)];
                        return true;
                    }

                    break;
            }
        }

        error = "工程 JSON 没有结束（页面可能被截断）。";
        return false;
    }

    /// <summary>解一个 JS 字符串转义（<c>cursor</c> 指向反斜杠）。</summary>
    private static void DecodeJsEscape(string text, ref int cursor, StringBuilder builder)
    {
        char kind = text[cursor + 1];
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
            case 'u' when cursor + 5 < text.Length &&
                          ushort.TryParse(text.AsSpan(cursor + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort code):
                builder.Append((char)code);
                cursor += 6;
                break;
            case '\r' or '\n':
                // JS 的行连接：反斜杠 + 换行 = 什么都没有
                cursor += 2;
                if (kind == '\r' && cursor < text.Length && text[cursor] == '\n')
                {
                    cursor++;
                }

                break;
            default:
                builder.Append(kind);
                cursor += 2;
                break;
        }
    }

    private static string? ReadQuotedValue(string text, int cursor)
    {
        cursor = SkipWhitespace(text, cursor);
        if (cursor >= text.Length || (text[cursor] != '"' && text[cursor] != '\''))
        {
            return null;
        }

        char quote = text[cursor];
        int end = cursor + 1;

        while (end < text.Length && text[end] != quote)
        {
            end++;
        }

        return end < text.Length
            ? UnescapeHtmlEntities(text[(cursor + 1)..end])
            : null;
    }

    private static int SkipWhitespace(string text, int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static bool StartsWith(string text, int cursor, string value) =>
        cursor + value.Length <= text.Length &&
        text.AsSpan(cursor, value.Length).Equals(value, StringComparison.Ordinal);
}
