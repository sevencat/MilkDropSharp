using System.Globalization;
using System.Text;

namespace MilkDrop.ProjectM.MilkdropPreset;

/// <summary>
/// <c>.milk</c> 预设文件解析器：读成 key/value 表，key 与 value 之间用等号或空格分隔，
/// 不匹配的行（例如 <c>[preset00]</c> 段名）直接忽略。
/// </summary>
/// <remarks>
/// <para>对应 <c>MilkdropPreset/PresetFileParser.hpp:18-122</c> 与 <c>PresetFileParser.cpp:12-179</c>。</para>
/// <para>
/// 数值解析要点（与原版一致，不能用 .NET 的 <c>int.Parse</c>）：
/// <c>std::stoi</c>/<c>std::stof</c> 只吃前缀，<c>"1.5"</c> 用 stoi 得到 1、<c>"3abc"</c> 得到 3、
/// 解析失败抛 <c>std::logic_error</c>（包含 <c>out_of_range</c>）后返回默认值。
/// </para>
/// <para>
/// 头文件注释说会剥掉 <c>//</c> 与 <c>\\\\</c> 注释，但 <c>GetCode</c> 实现里并没有做
/// （剥注释在 shader 装配阶段由 <c>Utils::StripComments</c> 负责），这里按实现来。
/// </para>
/// </remarks>
public sealed class PresetFileParser
{
    /// <summary>预设文件大小上限（1 MiB），超过视为非法文件。</summary>
    public const int MaxFileSize = 0x100000;

    private readonly SortedDictionary<string, string> _presetValues = new(StringComparer.Ordinal);

    /// <summary>解析后的键值表（按字节序排序，等价于 <c>std::map</c>）。</summary>
    public IReadOnlyDictionary<string, string> PresetValues => _presetValues;

    /// <summary>读取预设文件。</summary>
    /// <returns>成功解析出至少一对键值才返回 true。</returns>
    public bool ReadFile(string presetFile)
    {
        ArgumentNullException.ThrowIfNull(presetFile);

        var info = new FileInfo(presetFile);
        if (!info.Exists || info.Length > MaxFileSize)
        {
            return false;
        }

        var contents = File.ReadAllBytes(presetFile);
        return ReadBytes(contents);
    }

    /// <summary>解析内存中的预设内容。</summary>
    /// <returns>成功解析出至少一对键值才返回 true。</returns>
    /// <remarks>对应 <c>PresetFileParser.cpp:18-76</c>。</remarks>
    public bool ReadBytes(ReadOnlySpan<byte> contents)
    {
        if (contents.Length > MaxFileSize)
        {
            return false;
        }

        var startPos = 0;
        var pos = 0;

        while (pos < contents.Length)
        {
            switch (contents[pos])
            {
                case (byte)'\r':
                case (byte)'\n':
                    // 行结束；CRLF 的第二个字符会在下一轮命中，此时 pos == startPos，不会重复解析。
                    ParseLineIfDataAvailable(contents, startPos, pos);
                    startPos = pos + 1;
                    break;

                case 0:
                    // 出现 NUL 说明不是文本预设（可能是随机二进制）。
                    return false;
            }

            pos++;
        }

        ParseLineIfDataAvailable(contents, startPos, pos);

        return _presetValues.Count > 0;
    }

    /// <summary>
    /// 取一段编号代码（<c>warp_1</c>, <c>warp_2</c>, …）。每行开头的反引号会被去掉，行尾补 <c>\n</c>。
    /// </summary>
    /// <param name="keyPrefix">键前缀，例如 <c>warp_</c>、<c>comp_</c>、<c>per_frame_</c>。</param>
    /// <remarks>
    /// 编号从 1 开始，遇到第一个缺失的编号就停止（与 Milkdrop 一致：中间断号等于截断后面所有代码）。
    /// 对应 <c>PresetFileParser.cpp:78-106</c>。
    /// </remarks>
    public string GetCode(string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(keyPrefix);

        var code = new StringBuilder();

        for (var index = 1; index <= 99999; index++)
        {
            var key = keyPrefix + index.ToString(CultureInfo.InvariantCulture);
            if (!_presetValues.TryGetValue(key, out var line))
            {
                break;
            }

            // shader 代码每行以反引号开头，解析时去掉。
            if (line.Length > 0 && line[0] == '`')
            {
                line = line[1..];
            }

            code.Append(line).Append('\n');
        }

        return code.ToString();
    }

    /// <summary>取整数（<c>std::stoi</c> 语义：允许前缀匹配，失败返回默认值）。</summary>
    public int GetInt(string key, int defaultValue)
    {
        if (_presetValues.TryGetValue(key, out var value) && TryParseLeadingInt(value, out var result))
        {
            return result;
        }

        return defaultValue;
    }

    /// <summary>取浮点数（<c>std::stof</c> 语义：允许前缀匹配，失败返回默认值）。</summary>
    public float GetFloat(string key, float defaultValue)
    {
        if (_presetValues.TryGetValue(key, out var value) && TryParseLeadingDouble(value, out var result))
        {
            return (float)result;
        }

        return defaultValue;
    }

    /// <summary>取布尔值（<c>GetInt(...) &gt; 0</c>）。</summary>
    public bool GetBool(string key, bool defaultValue) => GetInt(key, defaultValue ? 1 : 0) > 0;

    /// <summary>取字符串。</summary>
    public string GetString(string key, string defaultValue)
        => _presetValues.TryGetValue(key, out var value) ? value : defaultValue;

    /// <remarks>对应 <c>PresetFileParser.cpp:160-179</c>。</remarks>
    private void ParseLineIfDataAvailable(ReadOnlySpan<byte> contents, int startPos, int pos)
    {
        if (pos <= startPos)
        {
            return;
        }

        ParseLine(Encoding.Latin1.GetString(contents[startPos..pos]));
    }

    /// <remarks>对应 <c>PresetFileParser.cpp:160-179</c>。</remarks>
    private void ParseLine(string line)
    {
        // 第一个分隔符（空格或等号）；行首就是分隔符或找不到分隔符则跳过。
        var varNameDelimiterPos = line.IndexOfAny([' ', '=']);

        if (varNameDelimiterPos <= 0)
        {
            return;
        }

        var varName = line[..varNameDelimiterPos];
        var value = line[(varNameDelimiterPos + 1)..];

        // 只保留第一次出现（原版注释：模仿 Milkdrop 行为）。
        if (varName.Length > 0 && !_presetValues.ContainsKey(varName))
        {
            _presetValues.Add(varName, value);
        }
    }

    /// <summary>模拟 <c>std::stoi</c>：跳过前导空白、可选正负号、连续数字，忽略后续字符。</summary>
    private static bool TryParseLeadingInt(string text, out int value)
    {
        value = 0;

        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        var start = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            i++;
        }

        var digitsStart = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == digitsStart)
        {
            return false;
        }

        // 溢出时 C++ 抛 out_of_range（属于 logic_error）→ 被捕获 → 返回默认值。
        return int.TryParse(text.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>模拟 <c>std::stof</c>：跳过前导空白、可选正负号、数字/小数点/指数，忽略后续字符。</summary>
    private static bool TryParseLeadingDouble(string text, out double value)
    {
        value = 0;

        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        var start = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            i++;
        }

        var integerDigitsStart = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (i == integerDigitsStart)
        {
            return false;
        }

        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            var exponentStart = i;
            i++;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                i++;
            }

            var exponentDigitsStart = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i == exponentDigitsStart)
            {
                // 指数部分无效，回退（std::stof 会在 'e' 处停止）。
                i = exponentStart;
            }
        }

        return double.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
