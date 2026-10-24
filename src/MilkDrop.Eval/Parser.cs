namespace MilkDrop.Eval;

/// <summary>
/// 手写解析器，等价移植 <c>Compiler.y</c> 的文法与优先级。
/// 采用 Pratt（优先级爬升）方式处理二元/三元运算符，后缀 <c>[]</c> 作为最高优先级的后置运算。
/// 解析结果直接交给 <see cref="CompilerBuilder"/> 生成表达式树。
/// </summary>
internal sealed class Parser
{
    /// <summary>不可作为表达式的空括号右侧，用于区分 <c>()</c> 空表达式与带内容的括号表达式。</summary>
    private readonly CompileContext _ctx;
    private readonly CompilerBuilder _builder;
    private readonly Lexer _lexer;

    private Token _token;
    private Token? _peek;

    public Parser(CompileContext ctx, string code)
    {
        _ctx = ctx;
        _builder = new CompilerBuilder(ctx);
        _lexer = new Lexer(code, ctx);
        _token = _lexer.NextToken();
    }

    /// <summary>解析整个程序，返回可执行树；空程序返回 null。</summary>
    public EvalNode? ParseProgram()
    {
        if (_token.Type == TokenType.Eof)
        {
            return null;
        }

        var node = ParseInstructionList();

        if (_token.Type != TokenType.Eof)
        {
            throw Unexpected();
        }

        return node.TreeNode;
    }

    // ---- 指令列表 ----

    private CompilerNode ParseInstructionList()
    {
        var node = ParseExpression(0);

        while (_token.Type == TokenType.Semicolon)
        {
            Next();

            // empty-expression: %empty | '(' ')'
            if (_token.Type == TokenType.LParen && Peek().Type == TokenType.RParen)
            {
                Next();
                Next();
                continue;
            }

            if (!CanStartExpression(_token.Type))
            {
                continue;
            }

            var instruction = ParseExpression(0);
            node = _builder.AddInstruction(node, instruction)!;
        }

        return node;
    }

    // ---- 表达式（优先级爬升）----

    private CompilerNode ParseExpression(int minBindingPower)
    {
        var left = ParsePrimary();

        while (true)
        {
            // 三元运算符，优先级 6（右结合）。
            if (_token.Type == TokenType.Question)
            {
                if (TripleBindingPower < minBindingPower)
                {
                    break;
                }

                Next();
                var trueValue = ParseExpression(0);
                Expect(TokenType.Colon);
                var falseValue = ParseExpression(TripleBindingPower);
                left = CreateExpression("_if", left, trueValue, falseValue);
                continue;
            }

            // 后缀内存访问，优先级 20。
            if (_token.Type == TokenType.LBracket)
            {
                if (PostfixBindingPower < minBindingPower)
                {
                    break;
                }

                Next();
                if (_token.Type == TokenType.RBracket)
                {
                    Next();
                    left = CreateExpression("_mem", left);
                    continue;
                }

                var offset = ParseExpression(0);
                Expect(TokenType.RBracket);
                var index = CreateExpression("_add", left, offset);
                left = CreateExpression("_mem", index);
                continue;
            }

            int? precedence = BinaryPrecedence(_token.Type);
            if (precedence is null)
            {
                break;
            }

            int leftBindingPower = 2 * precedence.Value;
            if (leftBindingPower < minBindingPower)
            {
                break;
            }

            var op = _token.Type;
            Next();

            int rightBindingPower = IsRightAssociative(precedence.Value)
                ? leftBindingPower
                : leftBindingPower + 1;

            var right = ParseExpression(rightBindingPower);
            left = CreateBinary(op, left, right);
        }

        return left;
    }

    private CompilerNode ParsePrimary()
    {
        switch (_token.Type)
        {
            case TokenType.Num:
            {
                double value = _token.Value;
                Next();
                return _builder.CreateConstant(value);
            }

            case TokenType.Var:
            {
                string name = _token.Text;
                Next();
                return _builder.CreateVariable(name);
            }

            case TokenType.Func:
                return ParseFunctionCall();

            case TokenType.Gmem:
            {
                Next();
                Expect(TokenType.LBracket, reportExpected: true);

                if (_token.Type == TokenType.RBracket)
                {
                    Next();
                    return CreateExpression("_gmem", _builder.CreateConstant(0.0));
                }

                var index = ParseExpression(0);
                Expect(TokenType.RBracket);
                return CreateExpression("_gmem", index);
            }

            case TokenType.LParen:
            {
                Next();
                var inner = ParseInstructionList();
                Expect(TokenType.RParen);
                return inner;
            }

            case TokenType.Minus:
            {
                Next();
                var operand = ParseExpression(UnaryNegBindingPower);
                return CreateExpression("_neg", operand);
            }

            case TokenType.Plus:
                // 一元加不产生任何运算。
                Next();
                return ParseExpression(UnaryNegBindingPower);

            case TokenType.Bang:
            {
                Next();
                var operand = ParseExpression(UnaryNotBindingPower);
                return CreateExpression("_not", operand);
            }

            default:
                throw Unexpected();
        }
    }

    private CompilerNode ParseFunctionCall()
    {
        var nameToken = _token;
        Next();

        Expect(TokenType.LParen, reportExpected: true);

        // function-arglist 至少包含一个 instruction-list，因此 "f()" 是语法错误。
        if (_token.Type == TokenType.RParen)
        {
            throw Unexpected();
        }

        var args = new List<CompilerNode> { ParseInstructionList() };

        while (_token.Type == TokenType.Comma)
        {
            Next();
            args.Add(ParseInstructionList());
        }

        Expect(TokenType.RParen);

        var function = _ctx.FindFunction(nameToken.Text);
        if (function is null)
        {
            throw new CompileException(
                $"Unknown function \"{nameToken.Text}\".",
                nameToken.Line,
                nameToken.Column,
                nameToken.EndColumn);
        }

        return _builder.CreateFunction(nameToken.Text, function, args);
    }

    // ---- 辅助 ----

    private CompilerNode CreateExpression(string functionName, params CompilerNode[] args)
        => _builder.CreateExpression(_ctx.FindFunction(functionName)!, args);

    private CompilerNode CreateBinary(TokenType op, CompilerNode left, CompilerNode right)
    {
        string name = op switch
        {
            TokenType.AddOp => "_addop",
            TokenType.SubOp => "_subop",
            TokenType.ModOp => "_modop",
            TokenType.OrOp => "_orop",
            TokenType.AndOp => "_andop",
            TokenType.DivOp => "_divop",
            TokenType.MulOp => "_mulop",
            TokenType.PowOp => "_powop",

            TokenType.Equal => "_equal",
            TokenType.Beleq => "_beleq",
            TokenType.Aboeq => "_aboeq",
            TokenType.Noteq => "_noteq",
            TokenType.Lt => "_below",
            TokenType.Gt => "_above",

            TokenType.BoolOr => "_or",
            TokenType.BoolAnd => "_and",

            TokenType.Assign => "_set",

            TokenType.Plus => "_add",
            TokenType.Minus => "_sub",
            TokenType.Star => "_mul",
            TokenType.Slash => "_div",
            TokenType.Percent => "_mod",
            TokenType.Caret => "pow",
            TokenType.Pipe => "/*or*/",
            TokenType.Amp => "/*and*/",

            _ => throw new InvalidOperationException($"Not a binary operator: {op}"),
        };

        return CreateExpression(name, left, right);
    }

    /// <summary>二元运算符优先级，与 Compiler.y 的 %precedence 声明一一对应（数值越大绑定越紧）。</summary>
    private static int? BinaryPrecedence(TokenType type) => type switch
    {
        TokenType.Assign => 2,

        TokenType.AddOp or TokenType.SubOp => 3,
        TokenType.MulOp or TokenType.DivOp or TokenType.ModOp => 4,
        TokenType.PowOp or TokenType.OrOp or TokenType.AndOp => 5,

        TokenType.BoolOr => 7,
        TokenType.BoolAnd => 8,
        TokenType.Pipe => 9,
        TokenType.Amp => 10,
        TokenType.Equal or TokenType.Noteq => 11,
        TokenType.Gt or TokenType.Aboeq => 12,
        TokenType.Lt or TokenType.Beleq => 13,
        TokenType.Plus or TokenType.Minus => 14,
        TokenType.Star or TokenType.Slash => 15,
        TokenType.Percent => 16,
        TokenType.Caret => 18,

        _ => null,
    };

    private static bool IsRightAssociative(int precedence)
        => precedence is 2 or 3 or 4 or 5;

    private const int TripleBindingPower = 12;   // 2 * 6
    private const int PostfixBindingPower = 40;  // 2 * 20
    private const int UnaryNotBindingPower = 34; // 2 * 17（'!'）
    private const int UnaryNegBindingPower = 38; // 2 * 19（NEG/POS）

    private static bool CanStartExpression(TokenType type)
        => type is TokenType.Num
            or TokenType.Var
            or TokenType.Func
            or TokenType.Gmem
            or TokenType.LParen
            or TokenType.Minus
            or TokenType.Plus
            or TokenType.Bang;

    private Token Peek()
    {
        _peek ??= _lexer.NextToken();
        return _peek.Value;
    }

    private Token Next()
    {
        var current = _token;
        if (_peek is { } peeked)
        {
            _token = peeked;
            _peek = null;
        }
        else
        {
            _token = _lexer.NextToken();
        }

        return current;
    }

    private void Expect(TokenType type, bool reportExpected = false)
    {
        if (_token.Type != type)
        {
            // 仅在与 bison 一致的场合给出 "expecting ..."：此处后继集合只含单个记号。
            throw Unexpected(reportExpected ? $"expecting {TokenName(type)}" : null);
        }

        Next();
    }

    private CompileException Unexpected(string? expected = null)
    {
        string message = $"syntax error, unexpected {TokenName(_token.Type)}";
        if (expected is not null)
        {
            message += $", {expected}";
        }

        return new CompileException(message, _token.Line, _token.Column, _token.EndColumn);
    }

    private static string TokenName(TokenType type) => type switch
    {
        TokenType.Eof => "end of file",
        TokenType.Undefined => "invalid token",
        TokenType.Num => "NUM",
        TokenType.Var => "VAR",
        TokenType.Func => "FUNC",
        TokenType.Gmem => "GMEM",
        TokenType.AddOp => "ADDOP",
        TokenType.SubOp => "SUBOP",
        TokenType.ModOp => "MODOP",
        TokenType.OrOp => "OROP",
        TokenType.AndOp => "ANDOP",
        TokenType.DivOp => "DIVOP",
        TokenType.MulOp => "MULOP",
        TokenType.PowOp => "POWOP",
        TokenType.Equal => "EQUAL",
        TokenType.Beleq => "BELEQ",
        TokenType.Aboeq => "ABOEQ",
        TokenType.Noteq => "NOTEQ",
        TokenType.BoolOr => "BOOLOR",
        TokenType.BoolAnd => "BOOLAND",
        TokenType.Lt => "'<'",
        TokenType.Gt => "'>'",
        TokenType.Plus => "'+'",
        TokenType.Minus => "'-'",
        TokenType.Star => "'*'",
        TokenType.Slash => "'/'",
        TokenType.Percent => "'%'",
        TokenType.Caret => "'^'",
        TokenType.Amp => "'&'",
        TokenType.Pipe => "'|'",
        TokenType.Bang => "'!'",
        TokenType.Assign => "'='",
        TokenType.Question => "'?'",
        TokenType.Colon => "':'",
        TokenType.Comma => "','",
        TokenType.Semicolon => "';'",
        TokenType.LParen => "'('",
        TokenType.RParen => "')'",
        TokenType.LBracket => "'['",
        TokenType.RBracket => "']'",
        _ => "invalid token",
    };
}
