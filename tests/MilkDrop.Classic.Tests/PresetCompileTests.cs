using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using MilkDrop.Classic.Visualizer.Sound;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 <c>CState::RecompileExpressions</c>（state.cpp:1552-1790）与
/// <c>StartBlendFrom</c>（state.cpp:704-849）的行为。
/// </summary>
public sealed class PresetCompileTests(ITestOutputHelper output)
{
    private static PerFrameContext Context(float time = 0f, float fps = 60f, double progress = 0)
        => new(time, fps, new MySoundData(), 0, progress, 48, 36, 640, 480, 1f, 1f);

    [Fact]
    public void InitCodeReadsStateVariables()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=0.5",
            "per_frame_init_1=q1 = zoom * 100;",
            "per_frame_1=zoom = q1 / 200;");

        var state = new PresetState(new Random(1));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        Assert.Null(state.LastError);
        Assert.Equal(50.0, state.PerFrame.QValuesAfterInitCode[0], 6);
        Assert.NotNull(state.PerFrameCode);
    }

    [Fact]
    public void InitCodeFailureIsReportedAndZeroesQ()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "per_frame_init_1=q1 = ;",
            "per_frame_1=zoom = 1;");

        var state = new PresetState(new Random(2));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        // init 失败：q/monitor 归零，但 per-frame 仍然要编译成功
        Assert.NotNull(state.LastError);
        Assert.All(state.PerFrame.QValuesAfterInitCode, v => Assert.Equal(0.0, v));
        Assert.NotNull(state.PerFrameCode);
    }

    [Fact]
    public void BlankCodeStringsAreCleared()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "per_frame_1=   ",
            "per_pixel_1= ");

        var state = new PresetState(new Random(3));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        Assert.Equal("", state.PerFrameExpr);
        Assert.Equal("", state.PerPixelExpr);
        Assert.Null(state.PerFrameCode);
    }

    [Fact]
    public void BlendableValuesInterpolateOnSwitch()
    {
        var from = new PresetState(new Random(4));
        from.Default();
        from.Zoom.Value = 1f;

        var to = new PresetState(new Random(5));
        to.Default();
        to.Zoom.Value = 3f;

        to.StartBlendFrom(from, animTime: 0f, timespan: 2f);

        Assert.True(to.IsBlending);
        Assert.Equal(1f, to.Zoom.Eval(0f), 5);
        Assert.Equal(2f, to.Zoom.Eval(1f), 5);
        Assert.Equal(3f, to.Zoom.Eval(2f), 5);
    }

    [Fact]
    public void BlendCopiesInstantFields()
    {
        var from = new PresetState(new Random(6));
        from.Default();
        from.WaveMode = 3;
        from.VideoEchoOrientation = 2;
        from.VideoEchoAlpha.Value = 0.5f;

        var to = new PresetState(new Random(7));
        to.Default();
        to.StartBlendFrom(from, 0f, 1f);

        Assert.Equal(3, to.OldWaveMode);
        Assert.Equal(2, to.VideoEchoOrientationOld);
        Assert.Equal(0.5f, to.VideoEchoAlphaOld, 5);
    }

    [Fact]
    public void FadingInMotionVectorsSkipsCountInterpolation()
    {
        var from = new PresetState(new Random(8));
        from.Default();
        from.MvA.Value = 1f;      // 不透明
        from.MvX.Value = 10f;

        var to = new PresetState(new Random(9));
        to.Default();
        to.MvA.Value = 0f;        // 透明
        to.MvX.Value = 30f;

        to.StartBlendFrom(from, 0f, 4f);

        // 新状态透明、旧状态不透明 → 数量直接对齐到旧状态当前值，而不是从 30 插到 10
        Assert.Equal(10f, to.MvX.Value, 5);
    }

    [Fact]
    public void RunPerFrameAppliesEquations()
    {
        var path = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=1.0",
            "per_frame_1=zoom = zoom + bass;");

        var state = new PresetState(new Random(10));
        Assert.True(state.Import(path, 0f));
        state.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        var mySound = new MySoundData();
        mySound.ImmRel[0] = 2.0f;
        var ctx = new PerFrameContext(0f, 60f, mySound, 0, 0, 48, 36, 640, 480, 1f, 1f);

        state.RunPerFrame(ctx);

        Assert.Equal(3.0, state.PerFrame["zoom"].Value, 6);

        // 每帧都从状态值重新灌入，所以结果不会累积
        state.RunPerFrame(ctx);
        Assert.Equal(3.0, state.PerFrame["zoom"].Value, 6);
    }

    [Fact]
    public void RunPerFrameAlsoRunsOldStateWhileBlending()
    {
        var pathA = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=1.0",
            "per_frame_1=zoom = zoom + 1;");
        var pathB = WriteTempMilk(
            "MILKDROP_PRESET_VERSION=200",
            "PSVERSION=2",
            "zoom=2.0",
            "per_frame_1=zoom = zoom + 2;");

        var stateA = new PresetState(new Random(11));
        stateA.Import(pathA, 0f);
        stateA.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        var stateB = new PresetState(new Random(12));
        stateB.Import(pathB, 0f);
        stateB.RecompileExpressions(PresetState.RecompilePresetCode, true, Context());

        stateB.StartBlendFrom(stateA, 0f, 1f);
        stateB.RunPerFrame(Context(), stateA);

        Assert.Equal(4.0, stateB.PerFrame["zoom"].Value, 6);
        Assert.Equal(2.0, stateA.PerFrame["zoom"].Value, 6);
    }

    [Fact]
    public void RecompilesWholeCorpus()
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

        var failures = new List<string>();
        var compiled = 0;
        var context = Context();

        foreach (var path in Directory.EnumerateFiles(root, "*.milk", SearchOption.AllDirectories))
        {
            var state = new PresetState(new Random(1));
            state.Import(path, 0f);
            state.RecompileExpressions(PresetState.RecompilePresetCode, true, context);

            if (state.PerFrameCode is not null)
            {
                compiled++;
            }

            if (state.LastError is not null)
            {
                failures.Add($"{Path.GetFileName(path)}: {state.LastError}");
            }
        }

        output.WriteLine($"per-frame 编译成功: {compiled}，失败: {failures.Count}");

        foreach (var failure in failures.Take(10))
        {
            output.WriteLine("FAIL " + failure);
        }

        Assert.Empty(failures);
    }

    private static string WriteTempMilk(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"milkdrop_{Guid.NewGuid():N}.milk");
        File.WriteAllLines(path, lines);
        return path;
    }
}
