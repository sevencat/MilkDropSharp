namespace MilkDrop.ProjectM.Playlist;

/// <summary>
/// 简单的文件名 glob 过滤器（对应 <c>playlist/Filter.hpp</c> 与 <c>Filter.cpp</c>）。
/// </summary>
/// <remarks>
/// 语法与 <c>projectm_playlist_set_filter()</c> 一致：<c>+</c>/<c>-</c> 前缀表示"保留/排除"，
/// 支持 <c>*</c>（不跨目录）、<c>**</c>（跨目录）、<c>?</c>；<c>/</c> 与 <c>\</c> 视为等价分隔符。
/// 规则按顺序匹配，第一条命中的规则决定去留；全部不命中则保留。
/// </remarks>
public sealed class Filter
{
    private readonly List<string> _filters = [];

    /// <summary>当前过滤器列表。</summary>
    public IReadOnlyList<string> List => _filters;

    /// <summary>设置过滤器列表。</summary>
    public void SetList(IEnumerable<string> filterList)
    {
        ArgumentNullException.ThrowIfNull(filterList);

        _filters.Clear();
        _filters.AddRange(filterList);
    }

    /// <summary>文件名是否通过过滤。</summary>
    public bool Passes(string filename)
    {
        foreach (string expression in _filters)
        {
            if (expression.Length > 0 && ApplyExpression(filename, expression))
            {
                // 默认动作是"命中即排除"，只有 '+' 前缀是保留。
                return expression[0] == '+';
            }
        }

        return true;
    }

    /// <summary>
    /// 单个 glob 表达式匹配（对应 <c>Filter::ApplyExpression</c>，带回溯的线性扫描）。
    /// </summary>
    private static bool ApplyExpression(string filename, string filterExpression)
    {
        if (filename.Length == 0 || filterExpression.Length == 0)
        {
            return false;
        }

        var fileIndex = 0;
        var filterIndex = 0;

        // 跳过保留/排除前缀。
        if (filterExpression[filterIndex] is '+' or '-')
        {
            filterIndex++;
        }

        if (filterIndex < filterExpression.Length && IsPathSeparator(filterExpression[filterIndex]))
        {
            // 以分隔符开头：从"相对路径"的起点开始比较，跳过开头的 ./ 和多余的 /。
            while (fileIndex + 1 < filename.Length && filename[fileIndex] == '.' &&
                   IsPathSeparator(filename[fileIndex + 1]))
            {
                fileIndex += 2;
            }

            while (fileIndex < filename.Length && IsPathSeparator(filename[fileIndex]))
            {
                fileIndex++;
            }

            filterIndex++;
        }
        else if (filterIndex < filterExpression.Length &&
                 !filterExpression.Contains('/') && !filterExpression.Contains('\\'))
        {
            // 没有分隔符的表达式只匹配文件名部分。
            int separatorUnix = filename.LastIndexOf('/');
            int separatorWindows = filename.LastIndexOf('\\');

            if (separatorUnix >= 0 && separatorWindows >= 0)
            {
                fileIndex = Math.Min(separatorUnix, separatorWindows) + 1;
            }
            else if (separatorUnix >= 0)
            {
                fileIndex = separatorUnix + 1;
            }
            else if (separatorWindows >= 0)
            {
                fileIndex = separatorWindows + 1;
            }
        }

        var previousFileIndex = -1;
        var previousFilterIndex = -1;
        var inPathGlob = false;

        while (fileIndex < filename.Length)
        {
            char filterChar = filterIndex < filterExpression.Length ? filterExpression[filterIndex] : '\0';

            switch (filterChar)
            {
                case '*':
                    previousFileIndex = fileIndex;
                    previousFilterIndex = filterIndex;

                    inPathGlob = false;
                    filterIndex++;

                    if (filterIndex < filterExpression.Length && filterExpression[filterIndex] == '*')
                    {
                        filterIndex++;

                        if (filterIndex >= filterExpression.Length)
                        {
                            return true;
                        }

                        if (!IsPathSeparator(filterExpression[filterIndex]))
                        {
                            return false;
                        }

                        inPathGlob = true;
                        filterIndex++;
                    }

                    continue;

                case '?':
                    if (IsPathSeparator(filename[fileIndex]))
                    {
                        break;
                    }

                    fileIndex++;
                    filterIndex++;
                    continue;

                default:
                    if (filterChar != filename[fileIndex] &&
                        !(IsPathSeparator(filterChar) && IsPathSeparator(filename[fileIndex])))
                    {
                        break;
                    }

                    fileIndex++;
                    filterIndex++;
                    continue;
            }

            // 回溯到上一个 '*'。
            if (previousFilterIndex >= 0 &&
                (inPathGlob || !IsPathSeparator(filename[previousFileIndex])))
            {
                fileIndex = ++previousFileIndex;
                filterIndex = previousFilterIndex;
                continue;
            }

            return false;
        }

        while (filterIndex < filterExpression.Length && filterExpression[filterIndex] == '*')
        {
            filterIndex++;
        }

        return filterIndex >= filterExpression.Length;
    }

    private static bool IsPathSeparator(char character) => character is '/' or '\\';
}
