using System.Globalization;
using System.Text;

namespace HlslParser;

/// <summary>
/// Minimal printf implementation. The C++ code base builds almost every output
/// line with printf-style format strings; keeping the same call sites in C#
/// (instead of rewriting them to interpolation) keeps the generated GLSL
/// byte-for-byte comparable and makes the port reviewable.
///
/// Supported: %s %d %i %u %c %f %F %g %G %x %X %% with flags "-+0 #",
/// width and precision. Length modifiers (h/l/ll/z/j/t) are ignored.
/// </summary>
internal static class CPrintf
{
    public static string Format(string format, params object[] args)
    {
        if (string.IsNullOrEmpty(format) || format.IndexOf('%') < 0)
        {
            return format ?? string.Empty;
        }

        var builder = new StringBuilder(format.Length + 16);
        int argIndex = 0;

        for (int i = 0; i < format.Length; ++i)
        {
            char c = format[i];
            if (c != '%')
            {
                builder.Append(c);
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                builder.Append('%');
                ++i;
                continue;
            }

            int j = i + 1;

            bool leftAlign = false;
            bool zeroPad = false;
            while (j < format.Length && "-+0 #".IndexOf(format[j]) >= 0)
            {
                leftAlign |= format[j] == '-';
                zeroPad |= format[j] == '0';
                ++j;
            }

            int width = 0;
            bool hasWidth = false;
            while (j < format.Length && format[j] >= '0' && format[j] <= '9')
            {
                hasWidth = true;
                width = width * 10 + (format[j] - '0');
                ++j;
            }

            int precision = -1;
            if (j < format.Length && format[j] == '.')
            {
                ++j;
                precision = 0;
                while (j < format.Length && format[j] >= '0' && format[j] <= '9')
                {
                    precision = precision * 10 + (format[j] - '0');
                    ++j;
                }
            }

            while (j < format.Length && "hlLzjtq".IndexOf(format[j]) >= 0)
            {
                ++j;
            }

            if (j >= format.Length)
            {
                builder.Append(format, i, format.Length - i);
                break;
            }

            char specifier = format[j];
            object arg = argIndex < args.Length ? args[argIndex] : null;
            ++argIndex;

            string text;
            bool numeric = true;
            switch (specifier)
            {
                case 'd':
                case 'i':
                    text = ToInt64(arg).ToString(CultureInfo.InvariantCulture);
                    break;
                case 'u':
                    unchecked
                    {
                        text = ((ulong)ToInt64(arg)).ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case 'x':
                    text = ToUInt64(arg).ToString("x", CultureInfo.InvariantCulture);
                    break;
                case 'X':
                    text = ToUInt64(arg).ToString("X", CultureInfo.InvariantCulture);
                    break;
                case 'c':
                    text = ((char)ToInt64(arg)).ToString();
                    break;
                case 'f':
                case 'F':
                    text = ToDouble(arg).ToString("F" + (precision < 0 ? 6 : precision).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    break;
                case 'g':
                case 'G':
                    text = FormatGeneral(ToDouble(arg), precision < 0 ? 6 : precision);
                    break;
                case 's':
                    text = arg == null ? string.Empty : arg.ToString();
                    numeric = false;
                    break;
                default:
                    // Mirror C: unknown conversions print nothing useful; keep
                    // the sequence verbatim so mismatches are obvious.
                    builder.Append('%').Append(specifier);
                    i = j;
                    continue;
            }

            if (specifier == 's' && precision >= 0 && text.Length > precision)
            {
                text = text.Substring(0, precision);
            }

            if (hasWidth && text.Length < width)
            {
                if (leftAlign)
                {
                    text = text.PadRight(width);
                }
                else if (zeroPad && numeric)
                {
                    bool negative = text.StartsWith("-", StringComparison.Ordinal);
                    if (negative)
                    {
                        text = "-" + text.Substring(1).PadLeft(width - 1, '0');
                    }
                    else
                    {
                        text = text.PadLeft(width, '0');
                    }
                }
                else
                {
                    text = text.PadLeft(width);
                }
            }

            builder.Append(text);
            i = j;
        }

        return builder.ToString();
    }

    private static string FormatGeneral(double value, int precision)
    {
        string text = value.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        // C++ default streams and printf both use a lower case exponent marker.
        return text.Replace("E", "e", StringComparison.Ordinal);
    }

    private static long ToInt64(object value)
    {
        return value switch
        {
            null => 0,
            int v => v,
            uint v => v,
            long v => v,
            ulong v => unchecked((long)v),
            short v => v,
            ushort v => v,
            byte v => v,
            sbyte v => v,
            bool v => v ? 1 : 0,
            float v => (long)v,
            double v => (long)v,
            _ => 0,
        };
    }

    private static ulong ToUInt64(object value)
    {
        return value switch
        {
            null => 0,
            int v => unchecked((uint)v),
            uint v => v,
            long v => unchecked((ulong)v),
            ulong v => v,
            short v => unchecked((ushort)v),
            ushort v => v,
            byte v => v,
            sbyte v => unchecked((byte)v),
            float v => (ulong)v,
            double v => (ulong)v,
            _ => 0,
        };
    }

    private static double ToDouble(object value)
    {
        return value switch
        {
            null => 0.0,
            double v => v,
            float v => v,
            _ => ToInt64(value),
        };
    }
}
