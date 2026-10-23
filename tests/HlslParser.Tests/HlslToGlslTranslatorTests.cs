using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// P7: the public <see cref="HlslToGlslTranslator"/> must produce exactly the same
/// text as the low level <see cref="GLSLGenerator"/> pipeline (and thus as C++).
/// </summary>
public class HlslToGlslTranslatorTests
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
    public void TranslateMatchesCppGolden(string fileName)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "glsl");
        string inputPath = Path.Combine(directory, fileName);
        string argsPath = Path.ChangeExtension(inputPath, ".args");
        string expectedPath = Path.Combine(directory, "expected", Path.ChangeExtension(fileName, ".glsl"));

        Assert.True(File.Exists(expectedPath), $"Missing expected output: {expectedPath}");
        string expected = File.ReadAllText(expectedPath);

        var args = ParseArgs(File.Exists(argsPath) ? File.ReadAllText(argsPath) : string.Empty);

        var request = new TranslationRequest
        {
            Source = File.ReadAllText(inputPath),
            FileName = fileName,
            EntryPoint = args.Entry,
            Target = args.Target,
            Version = args.Version,
            Flags = args.Flags,
        };

        TranslationResult result = new HlslToGlslTranslator().Translate(request);

        Assert.True(result.Success, "translate failed: " +
                                    string.Join("; ", result.Diagnostics.Select(d => $"{d.FileName}:{d.Line}: {d.Message}")));
        Assert.Equal(expected, result.Glsl);
    }

    [Fact]
    public void MissingEntryPointIsReported()
    {
        var request = new TranslationRequest
        {
            Source = "float4 PS(float2 uv : TEXCOORD0) : COLOR { return float4(uv, 0, 1); }",
            FileName = "missing_entry.hlsl",
            EntryPoint = "NoSuchEntry",
            ReturnPreprocessedSource = true,
        };

        TranslationResult result = new HlslToGlslTranslator().Translate(request);

        Assert.False(result.Success);
        Assert.Null(result.Glsl);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains("NoSuchEntry", result.Diagnostics[0].Message);
        Assert.NotNull(result.PreprocessedSource);
    }

    [Fact]
    public void ParseFailureIsReported()
    {
        var request = new TranslationRequest
        {
            Source = "float4 PS( : COLOR { this is not hlsl",
            FileName = "broken.hlsl",
        };

        TranslationResult result = new HlslToGlslTranslator().Translate(request);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void PreprocessCanBeSkipped()
    {
        // Without preprocessing the #define is unknown to the parser, so this only
        // verifies the flag is honoured (the shader itself must be macro free).
        var request = new TranslationRequest
        {
            Source = "float4 PS(float2 uv : TEXCOORD0) : COLOR { return float4(uv, 0, 1); }",
            Preprocess = false,
        };

        TranslationResult result = new HlslToGlslTranslator().Translate(request);

        Assert.True(result.Success);
        Assert.Null(result.PreprocessedSource);
        Assert.Contains("vec4 PS(vec2 uv)", result.Glsl);
    }

    private static (string Entry, GlslTarget Target, GlslVersion Version, GlslFlags Flags) ParseArgs(string text)
    {
        string entry = "PS";
        GlslTarget target = GlslTarget.FragmentShader;
        GlslVersion version = GlslVersion.Version330;
        GlslFlags flags = GlslFlags.None;

        string[] parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; ++i)
        {
            switch (parts[i])
            {
                case "--entry":
                    entry = parts[++i];
                    break;
                case "--target":
                    target = parts[++i] == "vertex" ? GlslTarget.VertexShader : GlslTarget.FragmentShader;
                    break;
                case "--version":
                    version = parts[++i] switch
                    {
                        "110" => GlslVersion.Version110,
                        "120" => GlslVersion.Version120,
                        "140" => GlslVersion.Version140,
                        "150" => GlslVersion.Version150,
                        "330" => GlslVersion.Version330,
                        "100es" => GlslVersion.Version100Es,
                        "300es" => GlslVersion.Version300Es,
                        _ => throw new ArgumentException($"unknown version: {parts[i]}"),
                    };
                    break;
                case "--flags":
                    foreach (string flag in parts[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        flags |= flag switch
                        {
                            "FlipPositionOutput" => GlslFlags.FlipPositionOutput,
                            "EmulateConstantBuffer" => GlslFlags.EmulateConstantBuffer,
                            "PackMatrixRowMajor" => GlslFlags.PackMatrixRowMajor,
                            "LowerMatrixMultiplication" => GlslFlags.LowerMatrixMultiplication,
                            "AlternateNanPropagation" => GlslFlags.AlternateNanPropagation,
                            _ => GlslFlags.None,
                        };
                    }
                    break;
            }
        }

        return (entry, target, version, flags);
    }
}
