namespace MilkDrop.ProjectM;

/// <summary>杂项字符串工具。</summary>
/// <remarks>对应 <c>Utils.hpp/.cpp:8-78</c>。</remarks>
public static class Utils
{
    /// <summary>转小写（不动区域性）。</summary>
    public static string ToLower(string str) => str.ToLowerInvariant();

    /// <summary>转大写（不动区域性）。</summary>
    public static string ToUpper(string str) => str.ToUpperInvariant();

    /// <summary>
    /// 把 <c>//</c> 行注释与 <c>/* */</c> 块注释替换成等长空格，保留换行与字符位置
    /// （行列号诊断因此不会错位）。
    /// </summary>
    /// <remarks>对应 <c>Utils.cpp:32-78</c>。</remarks>
    public static string StripComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = source.ToCharArray();
        var i = 0;

        while (i < result.Length)
        {
            if (i + 1 < result.Length && result[i] == '/' && result[i + 1] == '/')
            {
                while (i < result.Length && result[i] != '\n' && result[i] != '\r')
                {
                    result[i] = ' ';
                    i++;
                }
            }
            else if (i + 1 < result.Length && result[i] == '/' && result[i + 1] == '*')
            {
                result[i] = ' ';
                result[i + 1] = ' ';
                i += 2;
                while (i < result.Length)
                {
                    if (i + 1 < result.Length && result[i] == '*' && result[i + 1] == '/')
                    {
                        result[i] = ' ';
                        result[i + 1] = ' ';
                        i += 2;
                        break;
                    }

                    if (result[i] != '\n' && result[i] != '\r')
                    {
                        result[i] = ' ';
                    }

                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return new string(result);
    }
}
