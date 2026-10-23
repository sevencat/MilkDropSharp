using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// P6 acceptance: generated GLSL must match the C++ implementation byte for byte.
///
/// Layout: <c>golden/glsl/&lt;case&gt;.hlsl</c> plus an optional
/// <c>golden/glsl/&lt;case&gt;.args</c> holding the <c>hlslgolden</c> switches
/// (defaults: <c>--entry PS --target fragment --version 330</c>) and
/// <c>golden/glsl/expected/&lt;case&gt;.glsl</c> produced by:
/// <code>
/// tools/hlslgolden/bin/hlslgolden.exe --in &lt;case&gt;.hlsl &lt;args&gt; --out expected/&lt;case&gt;.glsl
/// </code>
/// </summary>
public class GoldenGlslTests
{
    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "glsl");

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
    public void GeneratedGlslMatchesCpp(string fileName)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "glsl");
        string inputPath = Path.Combine(directory, fileName);
        string argsPath = Path.ChangeExtension(inputPath, ".args");
        string expectedPath = Path.Combine(directory, "expected", Path.ChangeExtension(fileName, ".glsl"));

        Assert.True(File.Exists(inputPath), $"Golden input not copied to output: {inputPath}");
        Assert.True(File.Exists(expectedPath), $"Missing expected output: {expectedPath}");

        var options = ParseArgs(File.Exists(argsPath) ? File.ReadAllText(argsPath) : string.Empty);

        string source = File.ReadAllText(inputPath);

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var diagnostics = new CollectingDiagnosticSink();
        var parser = new HLSLParser(allocator, tree, diagnostics);

        Assert.True(parser.ApplyPreprocessor(fileName, source, out string preprocessed), "preprocess failed");
        Assert.True(parser.Parse(fileName, preprocessed), "parse failed: " + FormatDiagnostics(diagnostics));

        var generator = new GLSLGenerator();
        bool generated = generator.Generate(tree, options.Target, options.Version, options.Entry, options.Options);
        Assert.True(generated, "generate failed: " + FormatDiagnostics(diagnostics));

        string expected = File.ReadAllText(expectedPath);
        string actual = generator.GetResult();

        Assert.True(expected == actual, DescribeDifference(expected, actual));
    }

    private sealed class TestOptions
    {
        public string Entry = "PS";
        public GLSLGenerator.Target Target = GLSLGenerator.Target.Target_FragmentShader;
        public GLSLGenerator.Version Version = GLSLGenerator.Version.Version_330;
        public GLSLGenerator.Options Options = new();
    }

    private static TestOptions ParseArgs(string text)
    {
        var result = new TestOptions();
        string[] parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; ++i)
        {
            switch (parts[i])
            {
                case "--entry":
                    result.Entry = parts[++i];
                    break;
                case "--target":
                    result.Target = parts[++i] == "vertex"
                        ? GLSLGenerator.Target.Target_VertexShader
                        : GLSLGenerator.Target.Target_FragmentShader;
                    break;
                case "--version":
                    result.Version = parts[++i] switch
                    {
                        "110" => GLSLGenerator.Version.Version_110,
                        "120" => GLSLGenerator.Version.Version_120,
                        "140" => GLSLGenerator.Version.Version_140,
                        "150" => GLSLGenerator.Version.Version_150,
                        "330" => GLSLGenerator.Version.Version_330,
                        "100es" => GLSLGenerator.Version.Version_100_ES,
                        "300es" => GLSLGenerator.Version.Version_300_ES,
                        _ => throw new ArgumentException($"unknown version: {parts[i]}"),
                    };
                    break;
                case "--flags":
                    foreach (string flag in parts[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        result.Options.Flags |= flag switch
                        {
                            "FlipPositionOutput" => (int)GLSLGenerator.Flags.Flag_FlipPositionOutput,
                            "EmulateConstantBuffer" => (int)GLSLGenerator.Flags.Flag_EmulateConstantBuffer,
                            "PackMatrixRowMajor" => (int)GLSLGenerator.Flags.Flag_PackMatrixRowMajor,
                            "LowerMatrixMultiplication" => (int)GLSLGenerator.Flags.Flag_LowerMatrixMultiplication,
                            "AlternateNanPropagation" => (int)GLSLGenerator.Flags.Flag_AlternateNanPropagation,
                            _ => 0,
                        };
                    }
                    break;
            }
        }

        return result;
    }

    private static string FormatDiagnostics(CollectingDiagnosticSink diagnostics)
    {
        return string.Join("; ", diagnostics.Diagnostics.ConvertAll(d => $"{d.FileName}:{d.Line}: {d.Message}"));
    }

    /// <summary>Reports the first differing offset with context instead of a bare "not equal".</summary>
    private static string DescribeDifference(string expected, string actual)
    {
        int limit = Math.Min(expected.Length, actual.Length);
        int index = 0;
        while (index < limit && expected[index] == actual[index])
        {
            ++index;
        }

        string context = index <= expected.Length
            ? expected.Substring(Math.Max(0, index - 40), Math.Min(80, expected.Length - Math.Max(0, index - 40)))
            : string.Empty;

        return $"GLSL mismatch at offset {index} (expected length {expected.Length}, actual {actual.Length})\n" +
               $"expected: {Escape(index <= expected.Length ? expected.Substring(index, Math.Min(60, expected.Length - index)) : "<end>")}\n" +
               $"actual:   {Escape(index <= actual.Length ? actual.Substring(index, Math.Min(60, actual.Length - index)) : "<end>")}\n" +
               $"context:  {Escape(context)}";
    }

    private static string Escape(string text)
    {
        return text.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
