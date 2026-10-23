using System.Globalization;
using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>CState::Import</c>（state.cpp:1263-1457）的关键行为：
/// 版本分支、文件名描述、<c>wave_*</c> 的 rot 缺省值怪癖、mash-up 的区段拷贝。
/// </summary>
public sealed class PresetImportTests(ITestOutputHelper output)
{
    [Fact]
    public void ImportsRealPreset()
    {
        var path = FindPresetWith("per_frame_1");
        if (path is null)
        {
            output.WriteLine("跳过：找不到原版预设目录");
            return;
        }

        var state = new PresetState(new Random(1));
        Assert.True(state.Import(path, 12.5f));

        Assert.Equal(Path.GetFileNameWithoutExtension(path), state.Description);
        Assert.Equal(12.5f, state.PresetStartTime);
        Assert.False(string.IsNullOrEmpty(state.PerFrameExpr));
        Assert.True(state.MinPsVersion <= state.MaxPsVersion);

        // zoom 读进来后应该是一个有限值，且和文件里写的数字一致
        var file = PresetKeyValueFile.Load(path);
        if (file.TryGetExact("zoom", out var zoomText) &&
            float.TryParse(zoomText, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
        {
            Assert.Equal(zoom, state.Zoom.Value, 4);
        }
    }

    [Fact]
    public void WaveColourFallsBackToDefaultRot()
    {
        // 原版把 wave_r/g/b/x/y 的缺省值写成了 m_fRot.eval(-1)（state.cpp:1368-1372），
        // 而 wave 区在 motion 区**之前**解析，此刻 m_fRot 还是 Default 给的 0 —— 缺键时波形是黑的。
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "rot=0.3",
            "zoom=1.5");

        var state = new PresetState(new Random(2));
        Assert.True(state.Import(path, 0f));

        Assert.Equal(0.0f, state.WaveR.Value, 5);
        Assert.Equal(0.0f, state.WaveY.Value, 5);
        Assert.Equal(0.3f, state.Rot.Value, 5);
        Assert.Equal(1.5f, state.Zoom.Value, 5);
    }

    [Fact]
    public void Version200SharesPsVersion()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=3");

        var state = new PresetState(new Random(3));
        Assert.True(state.Import(path, 0f));

        Assert.Equal(3, state.WarpPsVersion);
        Assert.Equal(3, state.CompPsVersion);
        Assert.Equal(3, state.MaxPsVersion);
        Assert.Equal(3, state.MinPsVersion);
    }

    [Fact]
    public void Version201UsesSeparatePsVersions()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=201",
            "PSVERSION_WARP=2",
            "PSVERSION_COMP=3");

        var state = new PresetState(new Random(4));
        Assert.True(state.Import(path, 0f));

        Assert.Equal(2, state.WarpPsVersion);
        Assert.Equal(3, state.CompPsVersion);
        Assert.Equal(3, state.MaxPsVersion);
        Assert.Equal(2, state.MinPsVersion);
    }

    [Fact]
    public void VersionBefore200HasNoShaders()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=199",
            "PSVERSION=2");

        var state = new PresetState(new Random(5));
        Assert.True(state.Import(path, 0f));

        Assert.Equal(0, state.WarpPsVersion);
        Assert.Equal(0, state.CompPsVersion);
    }

    [Fact]
    public void MashUpOnlyOverridesRequestedSections()
    {
        var source = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "fDecay=0.5",
            "fGammaAdj=1.0",
            "zoom=2.0");
        var target = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "fDecay=0.9",
            "fGammaAdj=3.0",
            "zoom=0.25");

        var oldState = new PresetState(new Random(6));
        Assert.True(oldState.Import(source, 0f));

        var state = new PresetState(new Random(7));
        Assert.True(state.Import(target, 5f, oldState, PresetState.StateMotion));

        // general 区来自旧状态，motion 区来自新文件，描述也保持旧状态
        Assert.Equal(0.5f, state.Decay.Value, 5);
        Assert.Equal(1.0f, state.GammaAdj.Value, 5);
        Assert.Equal(0.25f, state.Zoom.Value, 5);
        Assert.Equal(oldState.Description, state.Description);
    }

    [Fact]
    public void ImportsWholeCorpus()
    {
        string root;
        try
        {
            root = ResourceLocator.PresetsDir;
        }
        catch (Exception ex)
        {
            output.WriteLine("跳过：找不到原版资源目录 - " + ex.Message);
            return;
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var files = Directory.EnumerateFiles(root, "*.milk", SearchOption.AllDirectories).ToArray();
        var failures = new List<string>();
        var withShaders = 0;

        foreach (var path in files)
        {
            var state = new PresetState(new Random(1));
            try
            {
                if (!state.Import(path, 0f))
                {
                    failures.Add(Path.GetFileName(path) + ": Import 返回 false");
                    continue;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            if (state.MaxPsVersion > 0)
            {
                withShaders++;
            }

            Assert.Equal(Path.GetFileNameWithoutExtension(path), state.Description);
        }

        output.WriteLine($".milk: {files.Length}，其中带 shader: {withShaders}");
        Assert.True(files.Length > 300);
        Assert.Empty(failures);
    }

    private static string? FindPresetWith(string key)
    {
        string root;
        try
        {
            root = ResourceLocator.PresetsDir;
        }
        catch
        {
            return null;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.milk", SearchOption.AllDirectories))
        {
            if (PresetKeyValueFile.Load(path).TryGetExact(key, out _))
            {
                return path;
            }
        }

        return null;
    }

    private static string WriteTempMilk(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"milkdrop_{Guid.NewGuid():N}.milk");
        File.WriteAllLines(path, lines);
        return path;
    }
}
