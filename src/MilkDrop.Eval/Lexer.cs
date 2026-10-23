using System.Globalization;

namespace ProjectMEvalSharp;

/// <summary>词法单元类型，对应 C 版 bison token 枚举（Scanner.l 返回的种类）。</summary>
internal enum TokenType
{
    Eof,
    Undefined,
    Num,
    Var,
    Func,
    Gmem,

    AddOp, SubOp, MulOp, DivOp, ModOp, OrOp, AndOp, PowOp,
    Equal, Beleq, Aboeq, Noteq,
    BoolOr, BoolAnd,

    Lt, Gt, Plus, Minus, Star, Slash, Percent, Caret, Amp, Pipe, Bang, Assign,
    Question, Colon, Comma, Semicolon,
    LParen, RParen, LBracket, RBracket,
}

/// <summary>词法单元。</summary>
internal readonly struct Token
{
    public TokenType Type { get; }
    public double Value { get; }
    public string Text { get; }
    public int Line { get; }
    public int Column { get; }
    public int EndColumn { get; }

    public Token(TokenType type, double value, string text, int line, int column, int endColumn)
    {
        Type = type;
        Value = value;
        Text = text;
        Line = line;
        Column = column;
        EndColumn = endColumn;
    }
}

/// <summary>手写词法分析器，等价移植 <c>Scanner.l</c> 的词法规则。</summary>
internal sealed class Lexer
{
    // $pi / $e / $phi：保持 C 版 float 字面量精度。
    private const double PiConst = 3.141592653589793f;
    private const double EConst = 2.71828183f;
    private const double PhiConst = 1.61803399f;

    private readonly string _src;
    private readonly CompileContext _ctx;
    private int _pos;
    private int _line = 1;
    private int _column;

    public Lexer(string source, CompileContext ctx)
    {
        _src = source ?? string.Empty;
        _ctx = ctx;
    }

    public Token NextToken()
    {
        SkipTrivia();
        int line = _line;
        int column = _column;

        if (_pos >= _src.Length)
        {
            return new Token(TokenType.Eof, 0.0, string.Empty, line, column, column);
        }

        char c = _src[_pos];

        if (IsDigit(c) || (c == '.' && _pos + 1 < _src.Length && IsDigit(_src[_pos + 1])))
        {
            return ScanNumber(line, column);
        }
        if (c == '$')
        {
            return ScanDollarConstant(line, column);
        }
        if (IsLetter(c) || c == '_')
        {
            return ScanName(line, column);
        }
        return ScanOperator(line, column);
    }

    // ---- 空白与注释 ----

    private void SkipTrivia()
    {
        while (true)
        {
            while (_pos < _src.Length && IsWhitespace(_src[_pos]))
            {
                if (_src[_pos] == '\n')
                {
                    _line++;
                    _column = 0;
                }
                else
                {
                    _column++;
                }
                _pos++;
            }

            if (_pos >= _src.Length)
            {
                return;
            }

            if (_src[_pos] == '/' && _pos + 1 < _src.Length)
            {
                if (_src[_pos + 1] == '*')
                {
                    _pos += 2;
                    _column += 2;
                    while (_pos < _src.Length)
                    {
                        if (_src[_pos] == '*' && _pos + 1 < _src.Length && _src[_pos + 1] == '/')
                        {
                            _pos += 2;
                            _column += 2;
                            break;
                        }
                        if (_src[_pos] == '\n')
                        {
                            _line++;
                            _column = 0;
                        }
                        else
                        {
                            _column++;
                        }
                        _pos++;
                    }
                    continue;
                }
                if (_src[_pos + 1] == '/')
                {
                    _pos += 2;
                    _column += 2;
                    while (_pos < _src.Length && _src[_pos] != '\n')
                    {
                        _column++;
                        _pos++;
                    }
                    continue;
                }
            }

            return;
        }
    }

    // ---- 数字 ----

    private Token ScanNumber(int line, int column)
    {
        int start = _pos;
        bool isFloat = false;

        while (_pos < _src.Length && IsDigit(_src[_pos]))
        {
            _pos++;
        }

        if (_pos < _src.Length && _src[_pos] == '.')
        {
            _pos++;
            while (_pos < _src.Length && IsDigit(_src[_pos]))
            {
                _pos++;
            }
            isFloat = true;
            TryScanExponent(ref isFloat);
        }
        else
        {
            TryScanExponent(ref isFloat);
        }

        string text = _src.Substring(start, _pos - start);
        _column += _pos - start;

        double value;
        if (isFloat)
        {
            value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        else if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
        {
            value = (int)integer; // 模拟 atoi = (int)strtol
        }
        else
        {
            value = 0.0;
        }

        return new Token(TokenType.Num, value, text, line, column, _column);
    }

    private void TryScanExponent(ref bool isFloat)
    {
        if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
        {
            int save = _pos;
            _pos++;
            if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-'))
            {
                _pos++;
            }
            int digits = 0;
            while (_pos < _src.Length && IsDigit(_src[_pos]))
            {
                _pos++;
                digits++;
            }
            if (digits == 0)
            {
                _pos = save;
            }
            else
            {
                isFloat = true;
            }
        }
    }

    // ---- $ 常量 ----

    private Token ScanDollarConstant(int line, int column)
    {
        int start = _pos;

        if (_pos + 1 < _src.Length && (_src[_pos + 1] == 'X' || _src[_pos + 1] == 'x'))
        {
            int i = _pos + 2;
            int hexStart = i;
            while (i < _src.Length && IsHexDigit(_src[i]))
            {
                i++;
            }
            if (i > hexStart)
            {
                double value = ulong.Parse(_src.Substring(hexStart, i - hexStart),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _pos = i;
                _column += _pos - start;
                return new Token(TokenType.Num, value, _src.Substring(start, _pos - start), line, column, _column);
            }
        }

        if (IsDollarCharLiteral())
        {
            double value = _src[_pos + 2];
            _pos += 4;
            _column += 4;
            return new Token(TokenType.Num, value, _src.Substring(start, 4), line, column, _column);
        }

        if (Matches("$phi"))
        {
            _pos += 4;
            _column += 4;
            return new Token(TokenType.Num, PhiConst, _src.Substring(start, 4), line, column, _column);
        }

        if (Matches("$pi"))
        {
            _pos += 3;
            _column += 3;
            return new Token(TokenType.Num, PiConst, _src.Substring(start, 3), line, column, _column);
        }

        if (_pos + 1 < _src.Length && (_src[_pos + 1] == 'e' || _src[_pos + 1] == 'E'))
        {
            _pos += 2;
            _column += 2;
            return new Token(TokenType.Num, EConst, _src.Substring(start, 2), line, column, _column);
        }

        _pos++;
        _column++;
        return new Token(TokenType.Undefined, 0.0, _src.Substring(start, 1), line, column, _column);
    }

    private bool IsDollarCharLiteral()
    {
        return _pos + 3 < _src.Length
            && _src[_pos + 1] == '\''
            && _src[_pos + 2] != '\n'
            && _src[_pos + 3] == '\'';
    }

    // ---- 标识符 ----

    private Token ScanName(int line, int column)
    {
        int start = _pos;
        _pos++;
        while (_pos < _src.Length && (IsLetter(_src[_pos]) || IsDigit(_src[_pos]) || _src[_pos] == '_'))
        {
            _pos++;
        }

        string text = _src.Substring(start, _pos - start);
        _column += _pos - start;

        if (text.Length == 4 && text.Equals("gmem", StringComparison.OrdinalIgnoreCase))
        {
            return new Token(TokenType.Gmem, 0.0, text, line, column, _column);
        }

        if (_ctx.FindFunction(text) != null)
        {
            return new Token(TokenType.Func, 0.0, text, line, column, _column);
        }

        return new Token(TokenType.Var, 0.0, text, line, column, _column);
    }

    // ---- 运算符 ----

    private Token ScanOperator(int line, int column)
    {
        int start = _pos;
        char c = _src[_pos];

        if (_pos + 1 < _src.Length)
        {
            char d = _src[_pos + 1];
            TokenType? two = (c, d) switch
            {
                ('+', '=') => TokenType.AddOp,
                ('-', '=') => TokenType.SubOp,
                ('%', '=') => TokenType.ModOp,
                ('|', '=') => TokenType.OrOp,
                ('&', '=') => TokenType.AndOp,
                ('/', '=') => TokenType.DivOp,
                ('*', '=') => TokenType.MulOp,
                ('^', '=') => TokenType.PowOp,
                ('=', '=') => TokenType.Equal,
                ('<', '=') => TokenType.Beleq,
                ('>', '=') => TokenType.Aboeq,
                ('!', '=') => TokenType.Noteq,
                ('|', '|') => TokenType.BoolOr,
                ('&', '&') => TokenType.BoolAnd,
                _ => null,
            };
            if (two is not null)
            {
                _pos += 2;
                _column += 2;
                return new Token(two.Value, 0.0, _src.Substring(start, 2), line, column, _column);
            }
        }

        TokenType single = c switch
        {
            '<' => TokenType.Lt,
            '>' => TokenType.Gt,
            '+' => TokenType.Plus,
            '-' => TokenType.Minus,
            '*' => TokenType.Star,
            '/' => TokenType.Slash,
            '%' => TokenType.Percent,
            '^' => TokenType.Caret,
            '&' => TokenType.Amp,
            '|' => TokenType.Pipe,
            '!' => TokenType.Bang,
            '=' => TokenType.Assign,
            '?' => TokenType.Question,
            ':' => TokenType.Colon,
            ',' => TokenType.Comma,
            ';' => TokenType.Semicolon,
            '(' => TokenType.LParen,
            ')' => TokenType.RParen,
            '[' => TokenType.LBracket,
            ']' => TokenType.RBracket,
            _ => TokenType.Undefined,
        };

        _pos++;
        _column++;
        return new Token(single, 0.0, _src.Substring(start, 1), line, column, _column);
    }

    // ---- 辅助 ----

    private bool Matches(string keyword)
    {
        return _pos + keyword.Length <= _src.Length
            && _src.AsSpan(_pos, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDigit(char c) => char.IsAsciiDigit(c);

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsLetter(char c) => char.IsAsciiLetter(c);

    private static bool IsWhitespace(char c) => c is ' ' or '\r' or '\n' or '\t' or '\v' or '\f';
}
