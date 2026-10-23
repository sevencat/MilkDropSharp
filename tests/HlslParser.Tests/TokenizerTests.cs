using System.Text;
using HlslParser;

namespace HlslParser.Tests;

public class TokenizerTests
{
    private static HLSLTokenizer Create(string source, out CollectingDiagnosticSink diagnostics, string fileName = "test.hlsl")
    {
        diagnostics = new CollectingDiagnosticSink();
        return new HLSLTokenizer(fileName, source, diagnostics);
    }

    private static List<(int Token, string Name, int Line)> Collect(string source, string fileName = "test.hlsl")
    {
        var result = new List<(int, string, int)>();
        var tokenizer = new HLSLTokenizer(fileName, source);

        while (tokenizer.GetToken() != (int)HLSLToken.EndOfStream)
        {
            result.Add((tokenizer.GetToken(), tokenizer.GetTokenName(), tokenizer.GetLineNumber()));
            tokenizer.Next();
        }

        result.Add((tokenizer.GetToken(), tokenizer.GetTokenName(), tokenizer.GetLineNumber()));
        return result;
    }

    [Fact]
    public void ReservedWordTableMatchesTokenEnum()
    {
        // 103 entries starting at 256, so LessEqual (first non reserved token) is 359.
        Assert.Equal(103, ReservedWords.Values.Length);
        Assert.Equal(359, (int)HLSLToken.LessEqual);
        Assert.Equal((int)HLSLToken.LessEqual, 256 + ReservedWords.Values.Length);
        Assert.Equal("pass", ReservedWords.Values[^1]);
    }

    [Fact]
    public void TokenizesSimpleFunction()
    {
        const string source = "float4 PS(float2 uv : TEXCOORD0) : COLOR { return float4(uv, 0, 1); }";
        var tokens = Collect(source);

        // Symbols are their own ASCII character code (C++ parity), not identifiers.
        var expected = new (HLSLToken Token, string Name)[]
        {
            (HLSLToken.Float4, "float4"),
            (HLSLToken.Identifier, "PS"),
            ((HLSLToken)'(', "("),
            (HLSLToken.Float2, "float2"),
            (HLSLToken.Identifier, "uv"),
            ((HLSLToken)':', ":"),
            (HLSLToken.Identifier, "TEXCOORD0"),
            ((HLSLToken)')', ")"),
            ((HLSLToken)':', ":"),
            (HLSLToken.Identifier, "COLOR"),
            ((HLSLToken)'{', "{"),
            (HLSLToken.Return, "return"),
            (HLSLToken.Float4, "float4"),
            ((HLSLToken)'(', "("),
            (HLSLToken.Identifier, "uv"),
            ((HLSLToken)',', ","),
            (HLSLToken.IntLiteral, "0"),
            ((HLSLToken)',', ","),
            (HLSLToken.IntLiteral, "1"),
            ((HLSLToken)')', ")"),
            ((HLSLToken)';', ";"),
            ((HLSLToken)'}', "}"),
            (HLSLToken.EndOfStream, "<eof>"),
        };

        Assert.Equal(expected.Length, tokens.Count);
        for (int i = 0; i < expected.Length; ++i)
        {
            Assert.Equal((int)expected[i].Token, tokens[i].Token);
            Assert.Equal(expected[i].Name, tokens[i].Name);
        }
    }

    [Fact]
    public void ScansNumbersLikeCppTokenizer()
    {
        var tokenizer = Create("1.0f 2 0x1F .5 1e-3", out _);

        Assert.Equal((int)HLSLToken.FloatLiteral, tokenizer.GetToken());
        Assert.Equal(1.0f, tokenizer.GetFloat());

        tokenizer.Next();
        Assert.Equal((int)HLSLToken.IntLiteral, tokenizer.GetToken());
        Assert.Equal(2, tokenizer.GetInt());

        tokenizer.Next();
        Assert.Equal((int)HLSLToken.IntLiteral, tokenizer.GetToken());
        Assert.Equal(0x1F, tokenizer.GetInt());

        tokenizer.Next();
        Assert.Equal((int)HLSLToken.FloatLiteral, tokenizer.GetToken());
        Assert.Equal(0.5f, tokenizer.GetFloat());

        tokenizer.Next();
        Assert.Equal((int)HLSLToken.FloatLiteral, tokenizer.GetToken());
        Assert.Equal(0.001f, tokenizer.GetFloat(), 6);

        tokenizer.Next();
        Assert.Equal((int)HLSLToken.EndOfStream, tokenizer.GetToken());
    }

    [Fact]
    public void HexWithoutDigitsFallsBackToIdentifier()
    {
        // strtol does not consume anything, and 'x' is not a number separator,
        // so the whole thing becomes an identifier (C++ parity).
        var tokenizer = Create("0xZZ", out _);
        Assert.Equal((int)HLSLToken.Identifier, tokenizer.GetToken());
        Assert.Equal("0xZZ", tokenizer.GetIdentifier());
    }

    [Fact]
    public void SkipsCommentsAndTracksLines()
    {
        var tokenizer = Create("a /* x\ny */ b // c\nd", out _);

        Assert.Equal("a", tokenizer.GetIdentifier());
        Assert.Equal(1, tokenizer.GetLineNumber());

        tokenizer.Next();
        Assert.Equal("b", tokenizer.GetIdentifier());
        Assert.Equal(2, tokenizer.GetLineNumber());

        tokenizer.Next();
        Assert.Equal("d", tokenizer.GetIdentifier());
        Assert.Equal(3, tokenizer.GetLineNumber());
    }

    [Fact]
    public void LineDirectiveChangesLineAndFileName()
    {
        var tokenizer = Create("#line 100 \"other.hlsl\"\na", out _);

        Assert.Equal((int)HLSLToken.Identifier, tokenizer.GetToken());
        Assert.Equal("a", tokenizer.GetIdentifier());
        Assert.Equal(100, tokenizer.GetLineNumber());
        Assert.Equal("other.hlsl", tokenizer.GetFileName());
    }

    [Fact]
    public void LineDirectiveWithoutFileName()
    {
        var tokenizer = Create("#line 7\na", out _);
        Assert.Equal("a", tokenizer.GetIdentifier());
        Assert.Equal(7, tokenizer.GetLineNumber());
        Assert.Equal("test.hlsl", tokenizer.GetFileName());
    }

    [Fact]
    public void PragmaDirectiveIsSkipped()
    {
        var tokenizer = Create("#pragma pack_matrix(row_major)\na", out var diagnostics);

        Assert.Equal((int)HLSLToken.Identifier, tokenizer.GetToken());
        Assert.Equal("a", tokenizer.GetIdentifier());
        Assert.Empty(diagnostics.Diagnostics);
    }

    [Fact]
    public void DefineWithoutArgumentsIsSingleToken()
    {
        var tokenizer = Create("#define FOO 1", out _);

        Assert.Equal((int)HLSLToken.PreprocessorDefine, tokenizer.GetToken());
        tokenizer.Next();
        Assert.Equal("FOO", tokenizer.GetIdentifier());

        // The next character is a space, which is how ParsePreprocessorDefine
        // distinguishes "FOO 1" from "FOO(x)".
        Assert.True(tokenizer.NextIsWhitespace());
    }

    [Fact]
    public void DefineWithArgumentsHasNoWhitespaceAfterName()
    {
        var tokenizer = Create("#define F(x) ((x)*2)", out _);

        Assert.Equal((int)HLSLToken.PreprocessorDefine, tokenizer.GetToken());
        tokenizer.Next();
        Assert.Equal("F", tokenizer.GetIdentifier());
        Assert.Equal('(', tokenizer.CharAtCurrentPos());
    }

    [Fact]
    public void MultiCharacterOperators()
    {
        var tokenizer = Create("+= -= *= /= == != <= >= && || ++ --", out _);

        var expected = new[]
        {
            HLSLToken.PlusEqual, HLSLToken.MinusEqual, HLSLToken.TimesEqual, HLSLToken.DivideEqual,
            HLSLToken.EqualEqual, HLSLToken.NotEqual, HLSLToken.LessEqual, HLSLToken.GreaterEqual,
            HLSLToken.AndAnd, HLSLToken.BarBar, HLSLToken.PlusPlus, HLSLToken.MinusMinus,
        };

        foreach (var token in expected)
        {
            Assert.Equal((int)token, tokenizer.GetToken());
            tokenizer.Next();
        }

        Assert.Equal((int)HLSLToken.EndOfStream, tokenizer.GetToken());
    }

    [Fact]
    public void TokenNameFallsBackToUnknownForComparisonOperators()
    {
        // Parity note: the C++ GetTokenName switch does not cover <=, >=, ==,
        // !=, &&, || or EndOfLine, so those print "unknown".
        Assert.Equal("unknown", HLSLTokenizer.GetTokenName((int)HLSLToken.LessEqual));
        Assert.Equal("unknown", HLSLTokenizer.GetTokenName((int)HLSLToken.EndOfLine));
        Assert.Equal("<", HLSLTokenizer.GetTokenName('<'));
        Assert.Equal("float", HLSLTokenizer.GetTokenName((int)HLSLToken.FloatLiteral));
    }

    [Fact]
    public void PreviousTokenPositionsAreExposed()
    {
        var tokenizer = Create("abc  def", out _);

        Assert.Equal(0, tokenizer.GetLastPos(false));
        Assert.Equal(0, tokenizer.GetLastPos(true));

        tokenizer.Next();

        // The previous token ended at index 3; trimming skips the two spaces.
        Assert.Equal(3, tokenizer.GetLastPos(false));
        Assert.Equal(5, tokenizer.GetLastPos(true));
    }

    [Fact]
    public void ReturnToPosRewindsStream()
    {
        var tokenizer = Create("abc def", out _);

        tokenizer.Next();
        Assert.Equal("def", tokenizer.GetIdentifier());

        tokenizer.ReturnToPos(0);
        tokenizer.Next();
        Assert.Equal("abc", tokenizer.GetIdentifier());
    }

    [Fact]
    public void UnterminatedBlockCommentDoesNotThrow()
    {
        var tokenizer = Create("a /* b", out _);
        Assert.Equal("a", tokenizer.GetIdentifier());
        tokenizer.Next();
        Assert.Equal((int)HLSLToken.EndOfStream, tokenizer.GetToken());
    }

    [Fact]
    public void DumpFormatMatchesGoldenTool()
    {
        var tokenizer = new HLSLTokenizer("test.hlsl", "float x = 1e-3;");
        var builder = new StringBuilder();
        int index = 0;
        while (tokenizer.GetToken() != (int)HLSLToken.EndOfStream)
        {
            builder.Append(index).Append('\t')
                .Append(tokenizer.GetToken()).Append('\t')
                .Append(tokenizer.GetTokenName()).Append('\t')
                .Append(tokenizer.GetLineNumber()).Append('\n');
            ++index;
            tokenizer.Next();
        }

        var lines = builder.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);

        // The instance overload prints the literal value (sprintf "%f"), the
        // static overload prints the token class name ("float").
        Assert.Equal($"3\t{(int)HLSLToken.FloatLiteral}\t0.001000\t1", lines[3]);
    }
}

internal static class TokenizerTestExtensions
{
    public static char CharAtCurrentPos(this HLSLTokenizer tokenizer)
    {
        int pos = tokenizer.GetCurrentPos();
        return pos < tokenizer.Buffer.Length ? tokenizer.Buffer[pos] : '\0';
    }
}
