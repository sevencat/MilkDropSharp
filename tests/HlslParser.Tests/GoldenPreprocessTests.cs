using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// P5 acceptance: preprocessed text must match the C++ implementation byte for
/// byte. Expected files come from
/// <c>tools/hlslgolden --in &lt;file&gt; --preprocess-only</c>.
/// </summary>
public class GoldenPreprocessTests
{
    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "preprocess");

        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.GetFiles(directory, "*.hlsl"))
            {
                data.Add(Path.GetFileName(file));
            }
        }

        if (data.Count == 0)
        {
            data.Add("<no golden inputs>");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void PreprocessedTextMatchesCpp(string fileName)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "preprocess");
        string inputPath = Path.Combine(directory, fileName);
        string expectedPath = Path.Combine(directory, "expected", Path.ChangeExtension(fileName, ".glsl"));

        Assert.True(File.Exists(inputPath), $"Golden input not copied to output: {inputPath}");
        Assert.True(File.Exists(expectedPath), $"Missing expected output: {expectedPath}");

        string source = File.ReadAllText(inputPath);
        string expected = File.ReadAllText(expectedPath);

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var diagnostics = new CollectingDiagnosticSink();
        var parser = new HLSLParser(allocator, tree, diagnostics);

        bool ok = parser.ApplyPreprocessor(fileName, source, out string actual);
        Assert.True(ok, "preprocess failed: " + string.Join("; ", diagnostics.Diagnostics.ConvertAll(d => $"{d.Line}: {d.Message}")));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Integration: our preprocessor output must be consumable by our parser
    /// (the C++ tests only exercise each half separately).
    /// </summary>
    [Fact]
    public void PreprocessedRealHeaderParses()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "preprocess");
        string inputPath = Path.Combine(directory, "preset_header.hlsl");
        Assert.True(File.Exists(inputPath), $"Golden input not copied to output: {inputPath}");

        string source = File.ReadAllText(inputPath);

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var diagnostics = new CollectingDiagnosticSink();
        var parser = new HLSLParser(allocator, tree, diagnostics);

        Assert.True(parser.ApplyPreprocessor("preset_header.hlsl", source, out string preprocessed));

        var parseAllocator = new Allocator();
        var parseTree = new HLSLTree(parseAllocator);
        var parseDiagnostics = new CollectingDiagnosticSink();
        var parseParser = new HLSLParser(parseAllocator, parseTree, parseDiagnostics);

        bool ok = parseParser.Parse("preset_header.hlsl", preprocessed);
        Assert.True(ok, "parse failed: " + string.Join("; ", parseDiagnostics.Diagnostics.ConvertAll(d => $"{d.Line}: {d.Message}")));
        Assert.NotNull(parseTree.GetRoot().Statement);
    }
}
