using System.Globalization;
using System.Text;

namespace HlslParser;

/// <summary>
/// Port of HLSLTokenizer.cpp.
///
/// The C++ implementation is a hand written scanner over a NUL terminated
/// buffer with a few unusual requirements that the parser and the
/// preprocessor depend on:
///   * <see cref="GetLastPos"/> returns the start of the previous token and can
///     optionally skip leading whitespace (used to rebuild macro values),
///   * <see cref="ReturnToPos"/> rewinds the scanner (used when macro arguments
///     do not match),
///   * <see cref="NextIsWhitespace"/> distinguishes <c>#define F(x)</c> from
///     <c>#define F (x)</c>.
/// Do not replace this with a pre-tokenized list.
/// </summary>
public sealed class HLSLTokenizer
{
    /// <summary>Maximum string length of an identifier.</summary>
    public const int MaxIdentifier = 255 + 1;

    private readonly string _buffer;
    private readonly int _end;
    private readonly IDiagnosticSink _diagnostics;

    private string _currentFileName;
    private int _pos;
    private int _prevPos;
    private int _lineNumber;
    private bool _error;

    private int _token;
    private float _fValue;
    private int _iValue;
    private string _identifier = string.Empty;
    private string _lineDirectiveFileName = string.Empty;
    private int _tokenLineNumber;

    /// <summary>Parameterless constructor kept for parity with the C++ class.</summary>
    public HLSLTokenizer()
        : this(string.Empty, string.Empty, null)
    {
    }

    /// <summary>The file name is only used for error reporting.</summary>
    public HLSLTokenizer(string fileName, string buffer, IDiagnosticSink diagnostics = null)
    {
        _buffer = buffer ?? string.Empty;
        _end = _buffer.Length;
        _currentFileName = fileName ?? string.Empty;
        _lineNumber = 1;
        _tokenLineNumber = 1;
        _error = false;
        _diagnostics = diagnostics ?? NullDiagnosticSink.Instance;
        Next();
    }

    public bool HasError => _error;

    /// <summary>Raw source, used by the preprocessor to rebuild text slices.</summary>
    internal string Buffer => _buffer;

    /// <summary>Advances to the next token in the stream.</summary>
    public void Next(bool eolSkipping = true)
    {
        _prevPos = _pos;

        while (SkipWhitespace(eolSkipping) || SkipComment(eolSkipping) || ScanLineDirective() || SkipPragmaDirective())
        {
        }

        if (_error)
        {
            _token = (int)HLSLToken.EndOfStream;
            return;
        }

        if (!eolSkipping && Ch() == '\n')
        {
            _token = (int)HLSLToken.EndOfLine;
            return;
        }

        _tokenLineNumber = _lineNumber;

        if (_pos >= _end || Ch() == '\0')
        {
            _token = (int)HLSLToken.EndOfStream;
            return;
        }

        int start = _pos;
        char c0 = Ch();
        char c1 = Ch(1);

        // +=, -=, *=, /=, ==, !=, <=, >=, &&, ||, ++, --
        if (c0 == '+' && c1 == '=')
        {
            _token = (int)HLSLToken.PlusEqual;
            _pos += 2;
            return;
        }
        if (c0 == '-' && c1 == '=')
        {
            _token = (int)HLSLToken.MinusEqual;
            _pos += 2;
            return;
        }
        if (c0 == '*' && c1 == '=')
        {
            _token = (int)HLSLToken.TimesEqual;
            _pos += 2;
            return;
        }
        if (c0 == '/' && c1 == '=')
        {
            _token = (int)HLSLToken.DivideEqual;
            _pos += 2;
            return;
        }
        if (c0 == '=' && c1 == '=')
        {
            _token = (int)HLSLToken.EqualEqual;
            _pos += 2;
            return;
        }
        if (c0 == '!' && c1 == '=')
        {
            _token = (int)HLSLToken.NotEqual;
            _pos += 2;
            return;
        }
        if (c0 == '<' && c1 == '=')
        {
            _token = (int)HLSLToken.LessEqual;
            _pos += 2;
            return;
        }
        if (c0 == '>' && c1 == '=')
        {
            _token = (int)HLSLToken.GreaterEqual;
            _pos += 2;
            return;
        }
        if (c0 == '&' && c1 == '&')
        {
            _token = (int)HLSLToken.AndAnd;
            _pos += 2;
            return;
        }
        if (c0 == '|' && c1 == '|')
        {
            _token = (int)HLSLToken.BarBar;
            _pos += 2;
            return;
        }
        if ((c0 == '-' || c0 == '+') && c1 == c0)
        {
            _token = c0 == '+' ? (int)HLSLToken.PlusPlus : (int)HLSLToken.MinusMinus;
            _pos += 2;
            return;
        }

        // Check for the start of a number.
        if (ScanNumber())
        {
            return;
        }

        if (IsSymbol(Ch()))
        {
            _token = Ch();
            ++_pos;
            return;
        }

        // Must be an identifier or a reserved word.
        while (_pos < _end && Ch() != '\0' && !IsSymbol(Ch()) && !Ascii.IsSpace(Ch()))
        {
            ++_pos;
        }

        int length = _pos - start;
        // C++ copies into a fixed 256 byte buffer without a bounds check; clamp
        // so pathological input cannot crash here (documented difference).
        _identifier = _buffer.Substring(start, Math.Min(length, MaxIdentifier - 1));

        for (int i = 0; i < ReservedWords.Values.Length; ++i)
        {
            if (String_.Equal(ReservedWords.Values[i], _identifier))
            {
                _token = 256 + i;
                return;
            }
        }

        _token = (int)HLSLToken.Identifier;
    }

    /// <summary>Returns true if the next character is a whitespace.</summary>
    public bool NextIsWhitespace()
    {
        return Ascii.IsSpace(Ch());
    }

    /// <summary>
    /// Start of the previous token. When <paramref name="trimmed"/> is true,
    /// leading whitespace is skipped. Returns a buffer index.
    /// </summary>
    public int GetLastPos(bool trimmed)
    {
        int start = _prevPos;
        if (trimmed)
        {
            while (start < _end && Ascii.IsSpace(_buffer[start]))
            {
                ++start;
            }
        }
        return start;
    }

    public int GetCurrentPos()
    {
        return _pos;
    }

    public void ReturnToPos(int pos)
    {
        _pos = pos;
    }

    internal string SourceSlice(int start, int end)
    {
        if (start >= end || start < 0 || start >= _buffer.Length)
        {
            return string.Empty;
        }
        return _buffer.Substring(start, Math.Min(end, _buffer.Length) - start);
    }

    /// <summary>Returns the current token in the stream.</summary>
    public int GetToken()
    {
        return _token;
    }

    /// <summary>Returns the value of the current float literal.</summary>
    public float GetFloat()
    {
        return _fValue;
    }

    /// <summary>Returns the value of the current int literal.</summary>
    public int GetInt()
    {
        return _iValue;
    }

    /// <summary>Returns the identifier for the current token.</summary>
    public string GetIdentifier()
    {
        return _identifier;
    }

    /// <summary>Returns the line number where the current token began.</summary>
    public int GetLineNumber()
    {
        return _tokenLineNumber;
    }

    /// <summary>Returns the file name where the current token began.</summary>
    public string GetFileName()
    {
        return _currentFileName;
    }

    /// <summary>Gets a human readable description of the current token.</summary>
    public string GetTokenName()
    {
        if (_token == (int)HLSLToken.FloatLiteral)
        {
            return _fValue.ToString("F6", CultureInfo.InvariantCulture);
        }
        if (_token == (int)HLSLToken.IntLiteral)
        {
            return _iValue.ToString(CultureInfo.InvariantCulture);
        }
        if (_token == (int)HLSLToken.Identifier)
        {
            return _identifier;
        }
        return GetTokenName(_token);
    }

    /// <summary>
    /// Gets a human readable description of the specified token.
    /// Note: like the C++ version, multi-character comparison operators and
    /// <see cref="HLSLToken.EndOfLine"/> fall through to "unknown".
    /// </summary>
    public static string GetTokenName(int token)
    {
        if (token < 256)
        {
            return ((char)token).ToString();
        }
        if (token < (int)HLSLToken.LessEqual)
        {
            return ReservedWords.Values[token - 256];
        }

        switch ((HLSLToken)token)
        {
            case HLSLToken.PlusPlus: return "++";
            case HLSLToken.MinusMinus: return "--";
            case HLSLToken.PlusEqual: return "+=";
            case HLSLToken.MinusEqual: return "-=";
            case HLSLToken.TimesEqual: return "*=";
            case HLSLToken.DivideEqual: return "/=";
            case HLSLToken.FloatLiteral: return "float";
            case HLSLToken.IntLiteral: return "int";
            case HLSLToken.Identifier: return "identifier";
            case HLSLToken.EndOfStream: return "<eof>";
            default: return "unknown";
        }
    }

    /// <summary>
    /// Reports an error. Only the first error is reported; after that the token
    /// stream reports EndOfStream (same as C++).
    /// </summary>
    public void Error(string message)
    {
        if (_error)
        {
            return;
        }
        _error = true;
        _diagnostics.Error(new Diagnostic(_currentFileName, _lineNumber, message));
    }

    private char Ch(int offset = 0)
    {
        int index = _pos + offset;
        return index >= 0 && index < _end ? _buffer[index] : '\0';
    }

    private char CharAt(int index)
    {
        return index >= 0 && index < _end ? _buffer[index] : '\0';
    }

    private bool SkipWhitespace(bool eolSkipping)
    {
        bool result = false;
        while (_pos < _end && Ascii.IsSpace(Ch()) && (eolSkipping || Ch() != '\n'))
        {
            result = true;
            if (Ch() == '\n')
            {
                ++_lineNumber;
            }
            ++_pos;
        }
        return result;
    }

    private bool SkipComment(bool eolSkipping)
    {
        bool result = false;
        if (Ch() == '/')
        {
            if (Ch(1) == '/')
            {
                // Single line comment.
                result = true;
                _pos += 2;
                while (_pos < _end)
                {
                    if (Ch() == '\n')
                    {
                        ++_lineNumber;
                        if (eolSkipping)
                        {
                            ++_pos;
                        }
                        break;
                    }
                    ++_pos;
                }
            }
            else if (Ch(1) == '*')
            {
                // Multi-line comment.
                result = true;
                _pos += 2;
                while (_pos < _end)
                {
                    if (Ch() == '\n')
                    {
                        ++_lineNumber;
                    }
                    if (Ch() == '*' && Ch(1) == '/')
                    {
                        break;
                    }
                    ++_pos;
                }
                if (_pos < _end)
                {
                    _pos += 2;
                }
            }
        }
        return result;
    }

    private bool SkipPragmaDirective()
    {
        bool result = false;
        if (_end - _pos > 7 && Ch() == '#')
        {
            int ptr = _pos + 1;
            while (Ascii.IsSpace(CharAt(ptr)))
            {
                ++ptr;
            }

            if (string.CompareOrdinal(_buffer, ptr, "pragma", 0, 6) == 0 && Ascii.IsSpace(CharAt(ptr + 6)))
            {
                _pos = ptr + 6;
                result = true;
                while (_pos < _end)
                {
                    char c = Ch();
                    ++_pos;
                    if (c == '\n')
                    {
                        ++_lineNumber;
                        break;
                    }
                }
            }
        }
        return result;
    }

    private bool ScanNumber()
    {
        // Don't treat the + or - as part of the number.
        if (Ch() == '+' || Ch() == '-')
        {
            return false;
        }

        // Parse hex literals.
        if (_end - _pos > 2 && Ch() == '0' && Ch(1) == 'x')
        {
            int hexEnd = _pos + 2;
            long hexValue = 0;
            while (hexEnd < _end)
            {
                int digit = HexDigit(CharAt(hexEnd));
                if (digit < 0)
                {
                    break;
                }
                hexValue = hexValue * 16 + digit;
                ++hexEnd;
            }

            if (IsNumberSeparator(CharAt(hexEnd)))
            {
                _pos = hexEnd;
                _token = (int)HLSLToken.IntLiteral;
                unchecked
                {
                    _iValue = (int)hexValue;
                }
                return true;
            }
        }

        double fValue = String_.ToDouble(_buffer, _pos, out int fEnd);
        if (fEnd == _pos)
        {
            return false;
        }

        int iValue = String_.ToInteger(_buffer, _pos, out int iEnd);

        // If the character after the number is an f (or h) then it is part of
        // the number (to handle 1.0f syntax).
        if ((CharAt(fEnd) == 'f' || CharAt(fEnd) == 'h') && fEnd < _end)
        {
            ++fEnd;
        }

        if (fEnd > iEnd && IsNumberSeparator(CharAt(fEnd)))
        {
            _pos = fEnd;
            _token = (int)HLSLToken.FloatLiteral;
            _fValue = (float)fValue;
            return true;
        }

        if (iEnd > _pos && IsNumberSeparator(CharAt(iEnd)))
        {
            _pos = iEnd;
            _token = (int)HLSLToken.IntLiteral;
            _iValue = iValue;
            return true;
        }

        return false;
    }

    private bool ScanLineDirective()
    {
        if (_end - _pos > 5 && string.CompareOrdinal(_buffer, _pos, "#line", 0, 5) == 0 && Ascii.IsSpace(CharAt(_pos + 5)))
        {
            _pos += 5;

            while (_pos < _end && Ascii.IsSpace(Ch()))
            {
                if (Ch() == '\n')
                {
                    Error("Syntax error: expected line number after #line");
                    return false;
                }
                ++_pos;
            }

            int lineNumber = String_.ToInteger(_buffer, _pos, out int iEnd);

            if (!Ascii.IsSpace(CharAt(iEnd)))
            {
                Error("Syntax error: expected line number after #line");
                return false;
            }

            _pos = iEnd;
            while (_pos < _end && Ascii.IsSpace(Ch()))
            {
                char c = Ch();
                ++_pos;
                if (c == '\n')
                {
                    _lineNumber = lineNumber;
                    return true;
                }
            }

            if (_pos >= _end)
            {
                _lineNumber = lineNumber;
                return true;
            }

            if (Ch() != '"')
            {
                Error("Syntax error: expected '\"' after line number near #line");
                return false;
            }

            ++_pos;

            var fileName = new StringBuilder();
            int i = 0;
            while (i + 1 < MaxIdentifier && _pos < _end && Ch() != '"')
            {
                if (Ch() == '\n')
                {
                    Error("Syntax error: expected '\"' before end of line near #line");
                    return false;
                }

                fileName.Append(Ch());
                ++_pos;
                ++i;
            }

            if (_pos >= _end)
            {
                Error("Syntax error: expected '\"' before end of file near #line");
                return false;
            }

            if (i + 1 >= MaxIdentifier)
            {
                Error("Syntax error: file name too long near #line");
                return false;
            }

            _lineDirectiveFileName = fileName.ToString();

            // Skip the closing quote.
            ++_pos;

            while (_pos < _end && Ch() != '\n')
            {
                if (!Ascii.IsSpace(Ch()))
                {
                    Error("Syntax error: unexpected input after file name near #line");
                    return false;
                }
                ++_pos;
            }

            // Skip new line.
            ++_pos;

            _lineNumber = lineNumber;
            _currentFileName = _lineDirectiveFileName;

            return true;
        }

        return false;
    }

    private static bool IsSymbol(char c)
    {
        switch (c)
        {
            case ';':
            case ':':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
            case '-':
            case '+':
            case '*':
            case '/':
            case '%':
            case '?':
            case '!':
            case ',':
            case '=':
            case '.':
            case '<':
            case '>':
            case '|':
            case '&':
            case '^':
            case '~':
            case '@':
                return true;
        }
        return false;
    }

    /// <summary>Returns true if the character is a valid token separator at the end of a number.</summary>
    private static bool IsNumberSeparator(char c)
    {
        return c == '\0' || Ascii.IsSpace(c) || IsSymbol(c);
    }

    private static int HexDigit(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        return -1;
    }
}
