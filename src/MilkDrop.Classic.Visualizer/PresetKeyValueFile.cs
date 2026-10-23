using System.Globalization;

namespace MilkDrop.Classic.Visualizer;

/// <summary>
/// <c>.milk</c> / <c>.wave</c> / <c>.shape</c> 的键值读取器（对应 C 版 <c>GetFastString</c>/<c>GetString</c>）。
/// </summary>
/// <remarks>
/// 注意 <c>.milk</c> 其实只有一个 <c>[preset00]</c> 段（<c>state.cpp:894</c>），
/// 但文件解析器会把以 <c>//</c> 开头的行当作注释跳过 —— 所以代码行自身以 <c>//</c> 开头时，
/// 写出端会加一个反引号转义（<c>state.cpp:1100-1102</c>）。
/// </remarks>
public sealed class PresetKeyValueFile
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>区分大小写的索引（C 版 <c>_GetLineByName</c> 用 <c>strcmp</c>），同名键取第一个。</summary>
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);

    private PresetKeyValueFile()
    {
    }

    public static PresetKeyValueFile Parse(IEnumerable<string> lines)
    {
        var file = new PresetKeyValueFile();

        foreach (var raw in lines)
        {
            var line = raw;
            var len = line.Length;
            if (len == 0)
            {
                continue;
            }

            // 文件级注释
            if (len >= 2 && line[0] == '/' && line[1] == '/')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];

            // 与 C 版一致：同名键取第一个
            file._values.TryAdd(key, value);
            file._exact.TryAdd(key, value);
        }

        return file;
    }

    public static PresetKeyValueFile Load(string path)
        => Parse(File.ReadAllLines(path));

    public IReadOnlyDictionary<string, string> Values => _values;

    public bool TryGetString(string key, out string value) => _values.TryGetValue(key, out value!);

    public string? GetString(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public float GetFloat(string key, float fallback = 0f)
        => _values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? f
            : fallback;

    public int GetInt(string key, int fallback = 0)
        => _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : fallback;

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out var v) ? v.Trim() is not ("0" or "") : fallback;

    /// <summary>区分大小写的查找（对应 C 版 <c>_GetLineByName</c> 的 <c>strcmp</c> 语义）。</summary>
    public bool TryGetExact(string key, out string value) => _exact.TryGetValue(key, out value!);

    /// <summary>
    /// 对应 <c>GetFastFloat</c>（state.cpp:190-201）：找不到键或解析失败时返回 <paramref name="fallback"/>。
    /// </summary>
    /// <remarks>
    /// C 版用 <c>_sscanf_l("%f")</c>，只解析行首的数字前缀、忽略尾随垃圾，所以这里也用前缀解析
    /// 而不是"整串必须合法"。
    /// </remarks>
    public float GetFloatExact(string key, float fallback)
        => TryGetExact(key, out var text) && TryParseFloatPrefix(text, out var value) ? value : fallback;

    /// <summary>对应 <c>GetFastInt</c>（state.cpp:179-188）。</summary>
    public int GetIntExact(string key, int fallback)
        => TryGetExact(key, out var text) && TryParseIntPrefix(text, out var value) ? value : fallback;

    /// <summary>
    /// 读取 <c>{prefix}1={prefix}2=...</c> 连续代码行，**保留** <see cref="EelPreprocessor.LinefeedControlChar"/>
    /// （C 版 <c>ReadCode</c> 存下来的是带控制符的原文，剥离发生在编译前）。
    /// </summary>
    public string ReadCodeRaw(string prefix)
        => EelPreprocessor.JoinContiguousLines(prefix, key => TryGetExact(key, out var value) ? value : null);

    /// <summary>读取 <c>{prefix}1={prefix}2=...</c> 连续代码行并返回合并预处理的 EEL 代码。</summary>
    public string ReadCode(string prefix)
        => EelPreprocessor.Prepare(prefix, _values);

    /// <summary>读取 <c>{prefix}1..N</c> 的原始行（不预处理）。</summary>
    public IReadOnlyList<string> ReadRawLines(string prefix)
    {
        var list = new List<string>();
        for (var n = 1; ; n++)
        {
            if (!_values.TryGetValue(prefix + n, out var line))
            {
                break;
            }

            list.Add(line);
        }

        return list;
    }

    /// <summary>模拟 <c>_sscanf_l("%f")</c>：跳过前导空白，解析尽可能长的合法前缀（失败则逐字缩短）。</summary>
    private static bool TryParseFloatPrefix(string text, out float value)
    {
        var span = text.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] is '+' or '-' or '.' or 'e' or 'E'))
        {
            end++;
        }

        for (var len = end; len > 0; len--)
        {
            if (float.TryParse(span[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0f;
        return false;
    }

    /// <summary>模拟 <c>sscanf("%d")</c>。</summary>
    private static bool TryParseIntPrefix(string text, out int value)
    {
        var span = text.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] is '+' or '-'))
        {
            end++;
        }

        for (var len = end; len > 0; len--)
        {
            if (int.TryParse(span[..len], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
