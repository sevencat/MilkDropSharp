using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 从页面上抠工程 JSON（P8 下载的第一步，<c>shaderToyIssue.md</c> S8-1）。
/// </summary>
public sealed class ShaderToyPageTests
{
    [Fact]
    public void ExtractsObjectLiteral_AndIgnoresBracesInsideStrings()
    {
        const string html = """
            <html><script>
            var gShaderToy = {"ver":"0.1","info":{"description":"a } b ] c"},"renderpass":[]};
            </script></html>
            """;

        Assert.True(ShaderToyPage.TryExtractProjectJson(html, out string json, out string? error), error);
        Assert.Equal("""{"ver":"0.1","info":{"description":"a } b ] c"},"renderpass":[]}""", json);
    }

    [Fact]
    public void ExtractsFromJsonParseCall_AndDecodesJsEscapes()
    {
        const string html = """
            <script>window.gShaderToy = JSON.parse('[{"ver":"0.1","code":"a\\"b\\nc"}]');</script>
            """;

        Assert.True(ShaderToyPage.TryExtractProjectJson(html, out string json, out string? error), error);
        Assert.Equal("""[{"ver":"0.1","code":"a\"b\nc"}]""", json);
    }

    [Fact]
    public void ExtractsEntityEscapedObjectLiteral()
    {
        const string html = """<script>var gShaderToy = {&quot;ver&quot;:&quot;0.1&quot;};</script>""";

        Assert.True(ShaderToyPage.TryExtractProjectJson(html, out string json, out _));
        Assert.Equal("""{"ver":"0.1"}""", json);
    }

    [Fact]
    public void MissingVariable_ReportsReason()
    {
        Assert.False(ShaderToyPage.TryExtractProjectJson("<html>nothing here</html>", out _, out string? error));
        Assert.Contains("gShaderToy", error);
    }

    [Fact]
    public void TruncatedProject_ReportsReason()
    {
        Assert.False(ShaderToyPage.TryExtractProjectJson("var gShaderToy = {\"a\":[1,2", out _, out string? error));
        Assert.Contains("没有结束", error);
    }

    [Fact]
    public void ExtractsThumbnailFromOpenGraph()
    {
        const string html = """
            <meta property="og:image" content="https://www.shadertoy.com/media/shaders/flGyWc.jpg">
            """;

        Assert.Equal("https://www.shadertoy.com/media/shaders/flGyWc.jpg", ShaderToyPage.TryExtractThumbnailUrl(html));
        Assert.Null(ShaderToyPage.TryExtractThumbnailUrl("<html></html>"));
    }

    [Fact]
    public void UnescapeHtmlEntities_HandlesAmpLast()
    {
        Assert.Equal("a&b\"c", ShaderToyPage.UnescapeHtmlEntities("a&amp;b&quot;c"));
    }
}
