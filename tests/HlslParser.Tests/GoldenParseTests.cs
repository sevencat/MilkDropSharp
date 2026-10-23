using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// Parses preprocessed real-world sources. Expected data is produced with
/// <c>tools/hlslgolden --preprocess-only</c> so the parser (not the yet
/// unported preprocessor) is exercised on realistic input.
/// </summary>
public class GoldenParseTests
{
    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "parse");

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
    public void ParsesPreprocessedSource(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "golden", "parse", fileName);
        Assert.True(File.Exists(path), $"Golden input not copied to output: {path}");

        string source = File.ReadAllText(path);

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var diagnostics = new CollectingDiagnosticSink();
        var parser = new HLSLParser(allocator, tree, diagnostics);

        bool ok = parser.Parse(fileName, source);
        Assert.True(ok, "parse failed: " + string.Join("; ", diagnostics.Diagnostics.ConvertAll(d => $"{d.Line}: {d.Message}")));

        Assert.NotNull(tree.GetRoot().Statement);
    }
}
