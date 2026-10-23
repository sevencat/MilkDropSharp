using System.Text;
using System.Text.RegularExpressions;
using HlslParser;

namespace HlslParser.Tests;

/// <summary>
/// Test-only replica of projectM's preset shader pipeline
/// (<c>PresetFileParser::GetCode</c> + <c>MilkdropShader::PreprocessPresetShader</c> +
/// <c>MilkdropShader::TranspileHLSLShader</c>'s text surgery), so the corpus tests can
/// feed hlslparser exactly the text projectM feeds it.
///
/// Not part of the library: the real projectM keeps this logic on the caller side.
/// </summary>
public static class MilkdropPresetCorpus
{
    private const string WarpPsSignature = @"
void PS(float4 _vDiffuse : COLOR,
        float4 _uv : TEXCOORD0,
        float2 _rad_ang : TEXCOORD1,
        out float4 _return_value : COLOR0,
        out float4 _mv_tex_coords : COLOR1)";

    private const string CompPsSignature = @"
void PS(float4 _vDiffuse : COLOR,
        float2 _uv : TEXCOORD0,
        float2 _rad_ang : TEXCOORD1,
        out float4 _return_value : COLOR)";

    public static Dictionary<string, string> ParsePresetValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            int equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = trimmed.Substring(0, equals).Trim().ToLowerInvariant();
            string value = trimmed.Substring(equals + 1);
            values[key] = value;
        }

        return values;
    }

    /// <summary>Port of <c>PresetFileParser::GetCode</c>.</summary>
    public static string GetCode(IReadOnlyDictionary<string, string> values, string keyPrefix)
    {
        var code = new StringBuilder();

        for (int index = 1; index <= 99999; ++index)
        {
            string key = keyPrefix + index;
            if (!values.TryGetValue(key, out string? line) || line == null)
            {
                break;
            }

            // Remove backtick char in shader code
            if (line.Length > 0 && line[0] == '`')
            {
                line = line.Substring(1);
            }

            code.Append(line).Append('\n');
        }

        return code.ToString();
    }

    /// <summary>
    /// Port of <c>Utils::StripComments</c>: comments become spaces, length and newlines preserved.
    /// </summary>
    public static string StripComments(string source)
    {
        char[] result = source.ToCharArray();
        int i = 0;

        while (i < result.Length)
        {
            if (i + 1 < result.Length && result[i] == '/' && result[i + 1] == '/')
            {
                while (i < result.Length && result[i] != '\n' && result[i] != '\r')
                {
                    result[i] = ' ';
                    ++i;
                }
            }
            else if (i + 1 < result.Length && result[i] == '/' && result[i + 1] == '*')
            {
                result[i] = ' ';
                result[i + 1] = ' ';
                i += 2;
                while (i < result.Length)
                {
                    if (i + 1 < result.Length && result[i] == '*' && result[i + 1] == '/')
                    {
                        result[i] = ' ';
                        result[i + 1] = ' ';
                        i += 2;
                        break;
                    }
                    if (result[i] != '\n' && result[i] != '\r')
                    {
                        result[i] = ' ';
                    }
                    ++i;
                }
            }
            else
            {
                ++i;
            }
        }

        return new string(result);
    }

    /// <summary>
    /// Port of <c>MilkdropShader::PreprocessPresetShader</c>. Returns false when the
    /// shader code has no <c>shader_body</c> (projectM throws in that case).
    /// </summary>
    public static bool TryPreprocessPresetShader(string code, bool warp, string header, out string? source)
    {
        source = null;

        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        string program = code;

        // Use a comment-stripped copy for searching so a commented-out shader_body is
        // not matched. StripComments preserves string length, so positions map 1:1.
        string stripped = StripComments(program);

        int found = stripped.IndexOf("shader_body", StringComparison.Ordinal);
        if (found < 0)
        {
            return false;
        }

        string signature = warp ? WarpPsSignature : CompPsSignature;
        program = program.Remove(found, 11).Insert(found, signature);

        found = program.IndexOf('{', found);
        if (found < 0)
        {
            return false;
        }

        string progMain = "{\nfloat3 ret = 0;\n";
        if (warp)
        {
            progMain += "_mv_tex_coords.xy = _uv.xy;\n";
        }
        program = program.Remove(found, 1).Insert(found, progMain);

        found = program.LastIndexOf('}');
        if (found < 0)
        {
            return false;
        }
        program = program.Remove(found, 1).Insert(found, "_return_value = float4(ret.xyz, 1.0);\n}\n");

        // Find the matching closing brace and cut off excess text.
        int bracesOpen = 1;
        int pos = found + 1;
        for (; pos < program.Length && bracesOpen > 0; ++pos)
        {
            switch (program[pos])
            {
                case '/':
                    if (pos < program.Length - 1 && program[pos + 1] == '/')
                    {
                        for (; pos < program.Length; ++pos)
                        {
                            if (program[pos] == '\n')
                            {
                                break;
                            }
                        }
                    }
                    else if (pos < program.Length - 1 && program[pos + 1] == '*')
                    {
                        pos += 2;
                        for (; pos < program.Length - 1; ++pos)
                        {
                            if (program[pos] == '*' && program[pos + 1] == '/')
                            {
                                ++pos;
                                break;
                            }
                        }
                    }
                    continue;

                case '{':
                    ++bracesOpen;
                    continue;

                case '}':
                    --bracesOpen;
                    break;
            }
        }

        if (pos < program.Length - 1)
        {
            program = program.Substring(0, pos);
        }

        var fullSource = new StringBuilder();
        fullSource.Append(header);

        if (warp)
        {
            fullSource.Append("#define rad _rad_ang.x\n" +
                              "#define ang _rad_ang.y\n" +
                              "#define uv _uv.xy\n" +
                              "#define uv_orig _uv.zw\n");
        }
        else
        {
            fullSource.Append("#define rad _rad_ang.x\n" +
                              "#define ang _rad_ang.y\n" +
                              "#define uv _uv.xy\n" +
                              "#define uv_orig _uv.xy\n" +
                              "#define hue_shader _vDiffuse.xyz\n");
        }

        fullSource.Append(program);

        source = fullSource.ToString();
        return true;
    }

    /// <summary>
    /// Port of the text surgery in <c>MilkdropShader::TranspileHLSLShader</c>:
    /// preprocess, drop the preset's own sampler/texsize declarations, then insert
    /// the declarations projectM synthesises from the referenced names.
    /// </summary>
    public static bool TryPrepareForParsing(string source, out string? prepared)
    {
        prepared = null;

        var allocator = new Allocator();
        var tree = new HLSLTree(allocator);
        var parser = new HLSLParser(allocator, tree, new CollectingDiagnosticSink());

        if (!parser.ApplyPreprocessor("", source, out string preprocessed))
        {
            return false;
        }

        // Remove previous shader declarations.
        preprocessed = Regex.Replace(preprocessed, "sampler(2D|3D|)(\\s+|\\()[^\\n]*", "");
        preprocessed = Regex.Replace(preprocessed, "float4\\s+texsize_[^\\n]*", "");

        // Collect unique samplers and texsize uniforms (projectM derives these from the
        // texture descriptors; for the corpus the referenced names are equivalent).
        string stripped = StripComments(preprocessed);
        var samplerNames = new SortedSet<string>(StringComparer.Ordinal) { "main" };
        foreach (Match match in Regex.Matches(stripped, "sampler_([A-Za-z0-9_]+)"))
        {
            string name = match.Groups[1].Value;
            if (name != "state")
            {
                samplerNames.Add(name);
            }
        }

        var texSizeNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(stripped, "texsize_([A-Za-z0-9_]+)"))
        {
            texSizeNames.Add(match.Groups[1].Value);
        }

        // projectM knows the texture type from the loaded descriptor; the corpus only
        // has the source, so infer volumes from a tex3D() call on that sampler.
        var volumeSamplers = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(stripped, "tex3D\\s*\\(\\s*sampler_([A-Za-z0-9_]+)"))
        {
            volumeSamplers.Add(match.Groups[1].Value);
        }

        // Now insert them on top: the C++ code inserts each set element at position 0,
        // so the final order is the reverse of the sorted order.
        var result = new StringBuilder(preprocessed);

        foreach (string name in texSizeNames)
        {
            result.Insert(0, TexSizeDeclaration(name));
        }

        foreach (string name in samplerNames)
        {
            result.Insert(0, SamplerDeclaration(name, volumeSamplers.Contains(name)));
        }

        prepared = result.ToString();
        return true;
    }

    /// <summary>Port of <c>TextureSamplerDescriptor::SamplerDeclaration</c>.</summary>
    private static string SamplerDeclaration(string name, bool volume)
    {
        var declaration = new StringBuilder();
        declaration.Append(volume ? "uniform sampler3D sampler_" : "uniform sampler2D sampler_").Append(name).Append(";\n");

        // Add short sampler name for prefixed random textures.
        if (name.Length > 7 && name.StartsWith("rand", StringComparison.Ordinal) && name[6] == '_')
        {
            declaration.Append("uniform sampler2D sampler_").Append(name.Substring(0, 6)).Append(";\n");
        }

        return declaration.ToString();
    }

    /// <summary>Port of <c>TextureSamplerDescriptor::TexSizeDeclaration</c>.</summary>
    private static string TexSizeDeclaration(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var declaration = new StringBuilder();
        declaration.Append("uniform float4 texsize_").Append(name).Append(";\n");

        if (name.Length > 7 && name.StartsWith("rand", StringComparison.Ordinal) && name[6] == '_')
        {
            declaration.Append("uniform float4 texsize_").Append(name.Substring(0, 6)).Append(";\n");
        }

        return declaration.ToString();
    }
}
