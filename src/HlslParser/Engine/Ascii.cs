namespace HlslParser;

/// <summary>
/// Locale independent replacements for the C <c>isspace</c>/<c>isdigit</c>
/// family. The C++ tokenizer uses the C locale variants; using
/// <see cref="char.IsWhiteSpace(char)"/> in C# would accept Unicode spaces and
/// change token boundaries.
/// </summary>
internal static class Ascii
{
    public static bool IsSpace(char c)
    {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
    }

    public static bool IsDigit(char c)
    {
        return c >= '0' && c <= '9';
    }
}
