using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 越界 <c>nWaveMode</c> 的提示（<see cref="PresetState.DescribeUnsupportedWaveMode"/>）。
/// </summary>
/// <remarks>
/// 原版 <c>milkdropfs.cpp:2801</c> 的 <c>switch(wave)</c> 只处理 0..8、没有 <c>default</c>，
/// 9 及以上不产生任何顶点。语料里有 58 个预设用了 9..15（MD3 PRO 扩展波形），
/// 其中一部分（如 <c>Over Water</c>）除此之外没有任何内容来源 → 屏幕纯黑。
/// </remarks>
public sealed class PresetWaveModeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void SupportedModesHaveNoNote(int mode)
    {
        var state = new PresetState { WaveMode = mode };
        Assert.Null(state.DescribeUnsupportedWaveMode());
    }

    [Theory]
    [InlineData(9)]
    [InlineData(15)]
    public void OutOfRangeModesAreReported(int mode)
    {
        var state = new PresetState { WaveMode = mode };

        var note = state.DescribeUnsupportedWaveMode();

        Assert.NotNull(note);
        Assert.Contains($"nWaveMode={mode}", note, StringComparison.Ordinal);
        Assert.Contains("0..8", note, StringComparison.Ordinal);
    }

    [Fact]
    public void OverWaterPresetIsReported()
    {
        var state = LoadPreset("A story of lasers - MilkDrop2077 - Over Water.milk", out var skipped);
        if (state is null)
        {
            output.WriteLine(skipped);
            return;
        }

        Assert.Equal(15, state.WaveMode);
        Assert.NotNull(state.DescribeUnsupportedWaveMode());
    }

    [Fact]
    public void OrdinaryPresetHasNoNote()
    {
        var state = LoadPreset("martin - crystal palace.milk", out var skipped);
        if (state is null)
        {
            output.WriteLine(skipped);
            return;
        }

        Assert.InRange(state.WaveMode, 0, PresetState.NumWaveModes - 1);
        Assert.Null(state.DescribeUnsupportedWaveMode());
    }

    private static PresetState? LoadPreset(string fileName, out string skipped)
    {
        skipped = string.Empty;
        string path;

        try
        {
            path = Path.Combine(ResourceLocator.PresetsDir, fileName);
        }
        catch (Exception ex)
        {
            skipped = "跳过：找不到原版资源目录 - " + ex.Message;
            return null;
        }

        if (!File.Exists(path))
        {
            skipped = "跳过：找不到预设 " + fileName;
            return null;
        }

        var state = new PresetState();
        Assert.True(state.Import(path, 0f), "导入失败: " + state.LastError);
        return state;
    }
}
