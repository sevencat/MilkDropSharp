using MilkDrop.Classic.Visualizer;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 cbplan.md §2.4 的三条预处理规则 —— 写错会让 25/692 条预设表达式编译失败。
/// </summary>
public sealed class EelPreprocessorTests
{
    private const char Lf = EelPreprocessor.LinefeedControlChar;

    [Fact]
    public void StripsLinefeedControlCharsWithoutInsertingSpaces()
    {
        var source = $"k1 =  is_{Lf}beat*2;{Lf}";
        Assert.Equal("k1 =  is_beat*2;", EelPreprocessor.StripLinefeedsAndComments(source));
    }

    [Fact]
    public void StripsSlashSlashCommentsUntilLineEnd()
    {
        var source = $"zoom = zoom + 0.1; // note{Lf}rot = rot + 0.2;{Lf}";
        Assert.Equal("zoom = zoom + 0.1; rot = rot + 0.2;", EelPreprocessor.StripLinefeedsAndComments(source));
    }

    [Fact]
    public void StripsBackslashBackslashCommentsUntilLineEnd()
    {
        var source = $"a = 1; \\\\ note{Lf}b = 2;{Lf}";
        Assert.Equal("a = 1; b = 2;", EelPreprocessor.StripLinefeedsAndComments(source));
    }

    [Fact]
    public void KeepsSingleSlashDivision()
    {
        var source = $"a = b/2;{Lf}";
        Assert.Equal("a = b/2;", EelPreprocessor.StripLinefeedsAndComments(source));
    }

    [Fact]
    public void JoinStopsAtFirstMissingIndex()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["per_frame_1"] = "a = 1;",
            ["per_frame_2"] = "b = 2;",
            // 缺少 per_frame_3
            ["per_frame_4"] = "c = 3;",
        };

        Assert.Equal($"a = 1;{Lf}b = 2;{Lf}", EelPreprocessor.JoinContiguousLines("per_frame_", keys));
    }

    [Fact]
    public void RemovesLeadingBacktickEscape()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["per_frame_1"] = "`// 这一行是代码，不是文件注释",
        };

        Assert.Equal($"// 这一行是代码，不是文件注释{Lf}", EelPreprocessor.JoinContiguousLines("per_frame_", keys));
    }

    /// <summary>真实预设里"跨行拼标识符"的用例（martin - liquid gold.milk:322-323）。</summary>
    [Fact]
    public void RealPresetSplitIdentifierCompiles()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["per_frame_1"] = "index = 0;",
            ["per_frame_2"] = "is_beat = above(bass, 1.0);",
            ["per_frame_3"] = "k1 =  is_",
            ["per_frame_4"] = "beat*equal(index%2,0);",
        };

        var code = EelPreprocessor.Prepare("per_frame_", keys);
        Assert.Contains("is_beat*equal", code);
        Assert.DoesNotContain("is_ beat", code);
    }
}
