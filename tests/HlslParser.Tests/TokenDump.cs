using System.Text;
using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// Mirrors the output of <c>tools/hlslgolden --tokens</c> exactly:
/// index TAB raw token value TAB token name TAB line number.
/// </summary>
internal static class TokenDump
{
    public static string Dump(string source, string fileName = "test.hlsl")
    {
        var builder = new StringBuilder();
        var tokenizer = new HLSLTokenizer(fileName, source);
        int index = 0;

        while (tokenizer.GetToken() != (int)HLSLToken.EndOfStream)
        {
            AppendLine(builder, index, tokenizer);
            ++index;
            tokenizer.Next();
        }

        AppendLine(builder, index, tokenizer);
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, int index, HLSLTokenizer tokenizer)
    {
        builder.Append(index).Append('\t')
            .Append(tokenizer.GetToken()).Append('\t')
            .Append(tokenizer.GetTokenName()).Append('\t')
            .Append(tokenizer.GetLineNumber()).Append('\n');
    }
}
