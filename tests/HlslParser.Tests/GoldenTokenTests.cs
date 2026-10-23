using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// P1 acceptance: token streams must match the C++ tokenizer byte for byte.
/// The .tokens files are produced by
/// <c>tools/hlslgolden/bin/hlslgolden.exe --in &lt;file&gt; --tokens</c>.
/// </summary>
public class GoldenTokenTests
{
    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "tokens");

        if (!Directory.Exists(directory))
        {
            data.Add("<missing golden directory>");
            return data;
        }

        foreach (string file in Directory.GetFiles(directory, "*.hlsl"))
        {
            data.Add(Path.GetFileName(file));
        }

        if (data.Count == 0)
        {
            data.Add("<no golden inputs>");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void TokenStreamMatchesCppTokenizer(string fileName)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "tokens");
        string inputPath = Path.Combine(directory, fileName);
        Assert.True(File.Exists(inputPath), $"Golden input not copied to output: {inputPath}");

        string expectedPath = Path.ChangeExtension(inputPath, ".tokens");
        Assert.True(File.Exists(expectedPath), $"Missing expected token dump: {expectedPath}");

        string source = File.ReadAllText(inputPath);
        string expected = File.ReadAllText(expectedPath);
        string actual = TokenDump.Dump(source, fileName);

        Assert.Equal(expected, actual);
    }
}
