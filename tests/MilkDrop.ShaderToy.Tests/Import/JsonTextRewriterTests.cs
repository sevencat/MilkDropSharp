using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 在 JSON 原文上做"整段字符串值替换"（P8 的资源路径改写，<c>shaderToyIssue.md</c> S8-2/S8-3）。
/// </summary>
public sealed class JsonTextRewriterTests
{
    [Fact]
    public void ReplacesOnlyExactStringValues_AndKeepsEverythingElse()
    {
        const string json = """{"a":"/media/a/x.png","b":"prefix /media/a/x.png suffix","c":"/media/a/x.png"}""";

        string result = JsonTextRewriter.ReplaceStringValues(
            json,
            new Dictionary<string, string> { ["/media/a/x.png"] = "textures/x.png" },
            out int replaced);

        Assert.Equal(2, replaced);
        Assert.Equal("""{"a":"textures/x.png","b":"prefix /media/a/x.png suffix","c":"textures/x.png"}""", result);
    }

    [Fact]
    public void MatchesEscapedSlashesInSource()
    {
        // 官方导出把斜杠转义成 \/（res/shadertoy/stpreset/flGyWc.json 就是这样）
        const string json = """{"filepath":"\/media\/a\/hash.png"}""";

        string result = JsonTextRewriter.ReplaceStringValues(
            json,
            new Dictionary<string, string> { ["/media/a/hash.png"] = "textures/hash.png" },
            out int replaced);

        Assert.Equal(1, replaced);
        Assert.Equal("""{"filepath":"textures/hash.png"}""", result);
    }

    [Fact]
    public void KeepsLiteralNewlineEscapesInCode()
    {
        const string json = """{"code":"void main()\n{\n\tx = 1;\n}","filepath":"/m/a.png"}""";

        string result = JsonTextRewriter.ReplaceStringValues(
            json,
            new Dictionary<string, string> { ["/m/a.png"] = "textures/a.png" },
            out _);

        Assert.Equal("""{"code":"void main()\n{\n\tx = 1;\n}","filepath":"textures/a.png"}""", result);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public void MissingKey_LeavesJsonUntouched()
    {
        const string json = """{"a":"b"}""";

        string result = JsonTextRewriter.ReplaceStringValues(
            json,
            new Dictionary<string, string> { ["x"] = "y" },
            out int replaced);

        Assert.Equal(0, replaced);
        Assert.Equal(json, result);
    }

    [Fact]
    public void EscapesQuotesInReplacement()
    {
        const string json = """{"a":"x"}""";

        string result = JsonTextRewriter.ReplaceStringValues(
            json,
            new Dictionary<string, string> { ["x"] = "a\"b" },
            out _);

        Assert.Equal("""{"a":"a\"b"}""", result);
    }
}
