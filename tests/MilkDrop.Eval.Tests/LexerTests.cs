using Xunit;

namespace ProjectMEvalSharp.Tests;

public class LexerTests
{
    private static List<Token> Tokenize(string source)
    {
        var ctx = new CompileContext();
        var lexer = new Lexer(source, ctx);
        var tokens = new List<Token>();
        Token t;
        do
        {
            t = lexer.NextToken();
            tokens.Add(t);
        } while (t.Type != TokenType.Eof);
        return tokens;
    }

    private static List<TokenType> Types(string source) =>
        Tokenize(source).Select(t => t.Type).ToList();

    [Fact]
    public void ScansIntegers()
    {
        var tokens = Tokenize("42");
        Assert.Equal(TokenType.Num, tokens[0].Type);
        Assert.Equal(42.0, tokens[0].Value);
        Assert.Equal(TokenType.Eof, tokens[1].Type);
    }

    [Fact]
    public void ScansFloatVariants()
    {
        Assert.Equal(1.5, Tokenize("1.5")[0].Value);
        Assert.Equal(1.0, Tokenize("1.")[0].Value);
        Assert.Equal(0.5, Tokenize(".5")[0].Value);
        Assert.Equal(1000.0, Tokenize("1e3")[0].Value);
        Assert.Equal(1.5e3, Tokenize("1.5e3")[0].Value);
        Assert.Equal(1e3, Tokenize("1.e3")[0].Value);
    }

    [Fact]
    public void IntegerExponentWithoutDigitsIsTwoTokens()
    {
        // "1e" -> INT(1) 后跟 NAME(e)，指数无数字则回溯。
        var types = Types("1e");
        Assert.Equal(new[] { TokenType.Num, TokenType.Var, TokenType.Eof }, types);
    }

    [Fact]
    public void ScansDollarConstants()
    {
        Assert.Equal(255.0, Tokenize("$Xff")[0].Value);
        Assert.Equal(16.0, Tokenize("$x10")[0].Value);
        Assert.Equal(65.0, Tokenize("$'A'")[0].Value);
        Assert.Equal((double)3.141592653589793f, Tokenize("$pi")[0].Value);
        Assert.Equal((double)2.71828183f, Tokenize("$e")[0].Value);
        Assert.Equal((double)1.61803399f, Tokenize("$phi")[0].Value);
        Assert.Equal((double)3.141592653589793f, Tokenize("$PI")[0].Value);
    }

    [Fact]
    public void ScansGmemKeyword()
    {
        Assert.Equal(TokenType.Gmem, Tokenize("gmem")[0].Type);
        Assert.Equal(TokenType.Gmem, Tokenize("GMEM")[0].Type);
        Assert.Equal(TokenType.Gmem, Tokenize("GmEm")[0].Type);
        // gmemX 更长，按最长匹配走 NAME。
        Assert.Equal(TokenType.Var, Tokenize("gmemX")[0].Type);
    }

    [Fact]
    public void DistinguishesFunctionAndVariable()
    {
        Assert.Equal(TokenType.Func, Tokenize("sin")[0].Type);
        Assert.Equal(TokenType.Func, Tokenize("pow")[0].Type);
        Assert.Equal(TokenType.Var, Tokenize("myvar")[0].Type);
    }

    [Fact]
    public void ScansCompoundOperators()
    {
        Assert.Equal(TokenType.AddOp, Tokenize("+=")[0].Type);
        Assert.Equal(TokenType.SubOp, Tokenize("-=")[0].Type);
        Assert.Equal(TokenType.MulOp, Tokenize("*=")[0].Type);
        Assert.Equal(TokenType.DivOp, Tokenize("/=")[0].Type);
        Assert.Equal(TokenType.ModOp, Tokenize("%=")[0].Type);
        Assert.Equal(TokenType.OrOp, Tokenize("|=")[0].Type);
        Assert.Equal(TokenType.AndOp, Tokenize("&=")[0].Type);
        Assert.Equal(TokenType.PowOp, Tokenize("^=")[0].Type);
    }

    [Fact]
    public void ScansComparisonAndLogicalOperators()
    {
        Assert.Equal(TokenType.Equal, Tokenize("==")[0].Type);
        Assert.Equal(TokenType.Beleq, Tokenize("<=")[0].Type);
        Assert.Equal(TokenType.Aboeq, Tokenize(">=")[0].Type);
        Assert.Equal(TokenType.Noteq, Tokenize("!=")[0].Type);
        Assert.Equal(TokenType.BoolOr, Tokenize("||")[0].Type);
        Assert.Equal(TokenType.BoolAnd, Tokenize("&&")[0].Type);
        Assert.Equal(TokenType.Lt, Tokenize("<")[0].Type);
        Assert.Equal(TokenType.Assign, Tokenize("=")[0].Type);
    }

    [Fact]
    public void SkipsComments()
    {
        var types = Types("1 /* block */ 2 // line\n3");
        Assert.Equal(new[]
        {
            TokenType.Num, TokenType.Num, TokenType.Num, TokenType.Eof,
        }, types);
    }

    [Fact]
    public void SkipsMultilineCommentAndTracksLines()
    {
        var tokens = Tokenize("1 /* a\nb */ 2");
        // 第二个数字应在第 2 行。
        Assert.Equal(2, tokens[1].Line);
    }

    [Fact]
    public void TracksColumns()
    {
        var tokens = Tokenize("a + b");
        Assert.Equal(0, tokens[0].Column);
        Assert.Equal(2, tokens[1].Column);
        Assert.Equal(4, tokens[2].Column);
        Assert.Equal(1, tokens[0].Line);
    }

    [Fact]
    public void InvalidCharacterIsUndefined()
    {
        Assert.Equal(TokenType.Undefined, Tokenize("@")[0].Type);
    }
}
