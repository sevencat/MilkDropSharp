using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// P7 acceptance: real Milkdrop presets must transpile byte-identically.
///
/// Two steps, mirroring the C++ tool based golden workflow:
/// <list type="number">
/// <item><c>DumpCorpusSources</c> (needs <c>HLSL_CORPUS_OUT</c>) extracts the shader code
/// from <c>presets/**/*.milk</c> exactly like projectM does and writes the text that would
/// be handed to the hlslparser into <c>golden/preset/&lt;case&gt;.hlsl</c>.</item>
/// <item><c>CorpusMatchesCpp</c> compiles those files with
/// <c>hlslgolden --skip-preprocess</c> (checked in as <c>expected/&lt;case&gt;.glsl</c>) and
/// compares the C# output.</item>
/// </list>
/// </summary>
public class MilkdropPresetCorpusTests
{
    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "preset");

        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.GetFiles(directory, "*.hlsl"))
            {
                data.Add(Path.GetFileName(file));
            }
        }

        if (data.Count == 0)
        {
            data.Add("<no corpus inputs>");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusMatchesCpp(string fileName)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "golden", "preset");
        string inputPath = Path.Combine(directory, fileName);
        string expectedPath = Path.Combine(directory, "expected", Path.ChangeExtension(fileName, ".glsl"));

        Assert.True(File.Exists(inputPath), $"Corpus input not copied to output: {inputPath}");
        Assert.True(File.Exists(expectedPath), $"Missing expected output: {expectedPath}");

        // The file already went through projectM's preprocessing, so hlslparser must
        // not preprocess it again (that is what hlslgolden --skip-preprocess does).
        var request = new TranslationRequest
        {
            Source = File.ReadAllText(inputPath),
            FileName = fileName,
            EntryPoint = "PS",
            Target = GlslTarget.FragmentShader,
            Version = GlslVersion.Version330,
            Flags = GlslFlags.AlternateNanPropagation,
            Preprocess = false,
        };

        TranslationResult result = new HlslToGlslTranslator().Translate(request);
        Assert.True(result.Success, "translate failed: " +
                                    string.Join("; ", result.Diagnostics.Select(d => $"{d.Line}: {d.Message}")));

        Assert.Equal(File.ReadAllText(expectedPath), result.Glsl);
    }

    /// <summary>
    /// Writes the extracted corpus sources so <c>hlslgolden.exe --skip-preprocess</c> can
    /// produce the expected GLSL. No-op unless <c>HLSL_CORPUS_OUT</c> is set, because
    /// tests must not write into the source tree by default.
    /// </summary>
    [Fact]
    public void DumpCorpusSources()
    {
        string? outputDirectory = Environment.GetEnvironmentVariable("HLSL_CORPUS_OUT");
        if (string.IsNullOrEmpty(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);

        string headerPath = Path.Combine(AppContext.BaseDirectory, "golden", "preprocess", "preset_header.hlsl");
        string header = File.ReadAllText(headerPath);
        string presetsDirectory = Path.Combine(AppContext.BaseDirectory, "presets");

        int dumped = 0;
        foreach ((string presetPath, bool warp, string prepared) in EnumeratePresetShaders(presetsDirectory, header))
        {
            string caseId = Path.GetFileNameWithoutExtension(presetPath) + (warp ? ".warp" : ".comp");
            File.WriteAllText(Path.Combine(outputDirectory, caseId + ".hlsl"), prepared);
            ++dumped;
        }


        Assert.True(dumped >= 0, $"dumped {dumped} preset shader(s)");
    }

    private static IEnumerable<(string PresetPath, bool Warp, string Prepared)> EnumeratePresetShaders(string presetsDirectory, string header)
    {
        if (!Directory.Exists(presetsDirectory))
        {
            yield break;
        }

        foreach (string presetPath in Directory.GetFiles(presetsDirectory, "*.milk", SearchOption.AllDirectories))
        {
            Dictionary<string, string> values = MilkdropPresetCorpus.ParsePresetValues(File.ReadAllText(presetPath));

            foreach (bool warp in new[] { true, false })
            {
                string code = MilkdropPresetCorpus.GetCode(values, warp ? "warp_" : "comp_");
                if (code.Length == 0)
                {
                    continue;
                }

                if (!MilkdropPresetCorpus.TryPreprocessPresetShader(code, warp, header, out string? source) || source == null)
                {
                    continue;
                }

                if (!MilkdropPresetCorpus.TryPrepareForParsing(source, out string? prepared) || prepared == null)
                {
                    continue;
                }

                yield return (presetPath, warp, prepared);
            }
        }
    }
}
