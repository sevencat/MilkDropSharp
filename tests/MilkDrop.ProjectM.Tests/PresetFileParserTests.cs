using System.Text;
using MilkDrop.ProjectM.MilkdropPreset;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ProjectM.Tests;

/// <summary><see cref="PresetFileParser"/> 的解析语义（与 C++ 逐条对齐）。</summary>
public sealed class PresetFileParserTests(ITestOutputHelper output)
{
    private const string SamplePreset = """
        [preset00]
        fRating=2.000000
        fGammaAdj=1.980000
        nWaveMode=7
        zoom=1.020000
        warp=0.030000
        wavecode_0=`// line one
        `// line two
        warp_1=`shader_body {
        warp_2=`  ret = tex2D(sampler_main, uv);
        warp_3=`}
        per_frame_1=`this is per frame 1
        per_frame_2=`this is per frame 2
        per_frame_4=`gap: this line and later ones must be ignored
        per_frame_5=`ignored too
        """;

    [Fact]
    public void ReadBytes_ParsesKeyValuesAndSkipsSectionHeader()
    {
        var parser = new PresetFileParser();
        Assert.True(parser.ReadBytes(Encoding.Latin1.GetBytes(SamplePreset.ReplaceLineEndings("\n"))));

        Assert.Equal(1.98f, parser.GetFloat("fGammaAdj", -1f), 4);
        Assert.Equal(7, parser.GetInt("nWaveMode", -1));
        Assert.Equal(1.02f, parser.GetFloat("zoom", -1f), 4);
        Assert.False(parser.PresetValues.ContainsKey("[preset00]"));
        Assert.Equal(-1, parser.GetInt("missing", -1));
    }

    [Fact]
    public void ReadBytes_HandlesCrLfAndTrailingLineWithoutNewline()
    {
        var parser = new PresetFileParser();
        var bytes = Encoding.Latin1.GetBytes("a=1\r\nb=2\r\nc=3");
        Assert.True(parser.ReadBytes(bytes));

        Assert.Equal(1, parser.GetInt("a", 0));
        Assert.Equal(2, parser.GetInt("b", 0));
        Assert.Equal(3, parser.GetInt("c", 0));
    }

    [Fact]
    public void ReadBytes_KeepsFirstOccurrenceOfDuplicateKey()
    {
        var parser = new PresetFileParser();
        Assert.True(parser.ReadBytes(Encoding.Latin1.GetBytes("zoom=1\nzoom=9\n")));

        Assert.Equal(1f, parser.GetFloat("zoom", 0f), 4);
    }

    [Fact]
    public void ReadBytes_RejectsFilesWithNulBytes()
    {
        var parser = new PresetFileParser();
        var bytes = new byte[] { (byte)'a', (byte)'=', (byte)'1', 0, (byte)'b' };

        Assert.False(parser.ReadBytes(bytes));
    }

    [Fact]
    public void ReadBytes_ReturnsFalseForEmptyInput()
    {
        Assert.False(new PresetFileParser().ReadBytes([]));
    }

    [Fact]
    public void GetCode_StripsBackticksAndStopsAtFirstGap()
    {
        var parser = new PresetFileParser();
        parser.ReadBytes(Encoding.Latin1.GetBytes(SamplePreset.ReplaceLineEndings("\n")));

        // 编号从 1 开始，遇到第一个缺失编号就截断（per_frame_3 不存在）。
        Assert.Equal("this is per frame 1\nthis is per frame 2\n", parser.GetCode("per_frame_"));

        // shader 代码每行开头的反引号去掉，行尾统一补 \n。
        Assert.Equal(
            "shader_body {\n  ret = tex2D(sampler_main, uv);\n}\n",
            parser.GetCode("warp_"));
    }

    [Fact]
    public void GetCode_ReturnsEmptyForMissingPrefix()
    {
        var parser = new PresetFileParser();
        parser.ReadBytes(Encoding.Latin1.GetBytes(SamplePreset.ReplaceLineEndings("\n")));

        Assert.Equal(string.Empty, parser.GetCode("comp_"));
    }

    [Theory]
    [InlineData("3.5", 3)] // std::stoi 只吃前缀
    [InlineData("3abc", 3)]
    [InlineData("  -12", -12)]
    [InlineData("+7", 7)]
    [InlineData("abc", 42)] // 解析失败 → 默认值
    [InlineData("", 42)]
    [InlineData("-", 42)]
    public void GetInt_FollowsStoiSemantics(string raw, int expected)
    {
        var parser = new PresetFileParser();
        parser.ReadBytes(Encoding.Latin1.GetBytes($"value={raw}\n"));

        Assert.Equal(expected, parser.GetInt("value", 42));
    }

    [Theory]
    [InlineData("1.5abc", 1.5f)]
    [InlineData("  -0.25", -0.25f)]
    [InlineData("1e3", 1000f)]
    [InlineData("2.5e-2", 0.025f)]
    [InlineData("1e", 1f)] // 指数无效 → 在 'e' 处停止
    [InlineData("xyz", 7f)] // 解析失败 → 默认值
    public void GetFloat_FollowsStofSemantics(string raw, float expected)
    {
        var parser = new PresetFileParser();
        parser.ReadBytes(Encoding.Latin1.GetBytes($"value={raw}\n"));

        Assert.Equal(expected, parser.GetFloat("value", 7f), 5);
    }

    [Fact]
    public void GetBool_UsesIntegerSemantics()
    {
        var parser = new PresetFileParser();
        parser.ReadBytes(Encoding.Latin1.GetBytes("a=1\nb=0\nc=abc\n"));

        Assert.True(parser.GetBool("a", false));
        Assert.False(parser.GetBool("b", true));
        Assert.True(parser.GetBool("c", true));
        Assert.False(parser.GetBool("missing", false));
    }

    /// <summary>
    /// 语料回归：把原生 projectM 自带的全部预设都解析一遍，不能抛异常，且绝大多数要能解析出键值。
    /// </summary>
    [Fact]
    public void Corpus_AllProjectMPresetsParses()
    {
        if (!Directory.Exists(TestPaths.PresetDir))
        {
            output.WriteLine($"跳过：找不到预设目录 {TestPaths.PresetDir}");
            return;
        }

        var files = Directory.GetFiles(TestPaths.PresetDir, "*.milk", SearchOption.AllDirectories);
        Assert.True(files.Length > 9000, $"预设数量异常: {files.Length}");

        var failed = new List<string>();
        var emptyCodes = 0;

        foreach (var file in files)
        {
            var parser = new PresetFileParser();

            bool ok;
            try
            {
                ok = parser.ReadFile(file);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (!ok)
            {
                failed.Add(Path.GetFileName(file) + ": Read 返回 false");
                continue;
            }

            if (parser.GetCode("per_frame_").Length == 0 && parser.GetCode("warp_").Length == 0)
            {
                emptyCodes++;
            }
        }

        output.WriteLine($"共 {files.Length} 个预设，解析失败 {failed.Count} 个，无代码段 {emptyCodes} 个");
        foreach (var item in failed.Take(10))
        {
            output.WriteLine("  " + item);
        }

        Assert.Empty(failed);
    }
}
