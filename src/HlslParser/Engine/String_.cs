using System.Globalization;

namespace HlslParser;

/// <summary>
/// Port of Engine/String.cpp (the subset hlslparser actually uses).
/// </summary>
public static class String_
{
    public static bool Equal(string a, string b)
    {
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    public static bool EqualNoCase(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static double ToDouble(string str, out int end)
    {
        return ToDouble(str, 0, out end);
    }

    /// <summary>
    /// Port of <c>iss_strtod</c>: first collect the characters that may form a
    /// number, then parse the longest valid prefix inside that window.
    /// </summary>
    public static double ToDouble(string str, int start, out int end)
    {
        end = start;
        if (str == null || start >= str.Length)
        {
            return 0.0;
        }

        int numEnd = start;
        while (numEnd < str.Length)
        {
            char c = str[numEnd];
            if (Ascii.IsDigit(c) || c == '.' || c == 'e' || c == 'E')
            {
                ++numEnd;
            }
            else if ((c == '+' || c == '-') && numEnd > start && (str[numEnd - 1] == 'e' || str[numEnd - 1] == 'E'))
            {
                ++numEnd;
            }
            else
            {
                break;
            }
        }

        if (numEnd == start)
        {
            return 0.0;
        }

        // Longest valid prefix: digits [ '.' digits ] [ ('e'|'E') ['+'|'-'] digits ]
        int index = start;
        bool anyDigit = false;
        while (index < numEnd && Ascii.IsDigit(str[index]))
        {
            ++index;
            anyDigit = true;
        }
        if (index < numEnd && str[index] == '.')
        {
            ++index;
            while (index < numEnd && Ascii.IsDigit(str[index]))
            {
                ++index;
                anyDigit = true;
            }
        }
        if (anyDigit && index < numEnd && (str[index] == 'e' || str[index] == 'E'))
        {
            int exponentEnd = index + 1;
            if (exponentEnd < numEnd && (str[exponentEnd] == '+' || str[exponentEnd] == '-'))
            {
                ++exponentEnd;
            }
            int digitsStart = exponentEnd;
            while (exponentEnd < numEnd && Ascii.IsDigit(str[exponentEnd]))
            {
                ++exponentEnd;
            }
            if (exponentEnd > digitsStart)
            {
                index = exponentEnd;
            }
        }

        if (!anyDigit)
        {
            return 0.0;
        }

        string text = str.Substring(start, index - start);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            // "1." is accepted by the C++ stream but may trip some runtimes.
            if (text.EndsWith(".", StringComparison.Ordinal) &&
                double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                end = index;
                return value;
            }
            return 0.0;
        }

        end = index;
        return value;
    }

    public static int ToInteger(string str, out int end)
    {
        return ToInteger(str, 0, out end);
    }

    /// <summary>Port of <c>strtol(str, end, 10)</c> for the cases the tokenizer uses.</summary>
    public static int ToInteger(string str, int start, out int end)
    {
        end = start;
        if (str == null || start >= str.Length)
        {
            return 0;
        }

        int index = start;
        while (index < str.Length && Ascii.IsSpace(str[index]))
        {
            ++index;
        }

        bool negative = false;
        if (index < str.Length && (str[index] == '+' || str[index] == '-'))
        {
            negative = str[index] == '-';
            ++index;
        }

        int digitsStart = index;
        unchecked
        {
            int value = 0;
            while (index < str.Length && Ascii.IsDigit(str[index]))
            {
                value = value * 10 + (str[index] - '0');
                ++index;
            }

            if (index == digitsStart)
            {
                return 0;
            }

            end = index;
            return negative ? -value : value;
        }
    }

    /// <summary>
    /// Port of <c>String_FormatFloat</c>: C++ streams the value with default
    /// settings (6 significant digits, "C" locale) inside <c>float(...)</c>.
    /// </summary>
    public static string FormatFloat(float value)
    {
        return "float(" + FormatGeneral(value, 6) + ")";
    }

    /// <summary>
    /// Approximation of C++ <c>operator&lt;&lt;(ostream, float)</c> with default
    /// precision (6 significant digits). .NET's "G6" matches the value choice;
    /// the only textual difference is the exponent marker case.
    /// </summary>
    public static string FormatGeneral(float value, int precision)
    {
        string text = value.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return text.Replace("E", "e", StringComparison.Ordinal);
    }
}
