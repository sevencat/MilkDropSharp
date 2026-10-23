using MilkDrop.Classic.Visualizer;
using MilkDrop.Classic.Visualizer.Preset;
using MilkDrop.Classic.Visualizer.Sound;
using MilkDrop.Eval;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// 锁死 per-frame 变量注册面（state.cpp:267-348）与 init code 回读语义（state.cpp:1665-1694）。
/// </summary>
public sealed class PerFrameVariablesTests(ITestOutputHelper output)
{
    [Fact]
    public void RegistersDocumentedNames()
    {
        var vars = new PerFrameVariables(new ProjectMEvalEngine());

        string[] expected =
        [
            "zoom", "zoomexp", "rot", "warp", "cx", "cy", "dx", "dy", "sx", "sy",
            "time", "fps", "bass", "mid", "treb", "bass_att", "mid_att", "treb_att", "frame",
            "decay", "wave_a", "wave_mystery", "wave_mode", "progress",
            "ob_size", "ib_a", "mv_a", "monitor", "echo_orient",
            "wave_usedots", "darken_center", "gamma", "solarize",
            "meshx", "pixelsy", "aspectx",
            "blur1_min", "blur3_max", "blur1_edge_darken",
            "q1", "q16", "q32",
        ];

        foreach (var name in expected)
        {
            Assert.True(vars.Contains(name), $"缺少变量 {name}");
        }

        Assert.Equal(PerFrameVariables.NumQVar, vars.Q.Count);
    }

    [Fact]
    public void SoundVarsUseRelativeLevels()
    {
        var vars = new PerFrameVariables(new ProjectMEvalEngine());
        var mySound = new MySoundData();
        mySound.ImmRel[0] = 2.5f;
        mySound.ImmRel[1] = 0.75f;
        mySound.AvgRel[0] = 0.5f;

        vars.SyncReadOnly(time: 3.5f, fps: 60f, mySound, frame: 42, progress: 0.25);

        Assert.Equal(2.5, vars["bass"].Value, 6);
        Assert.Equal(0.75, vars["mid"].Value, 6);
        Assert.Equal(0.5, vars["bass_att"].Value, 6);
        Assert.Equal(42.0, vars["frame"].Value, 6);
        Assert.Equal(0.25, vars["progress"].Value, 6);
    }

    [Fact]
    public void CompiledCodeReadsSyncedValues()
    {
        var vars = new PerFrameVariables(new ProjectMEvalEngine());
        var mySound = new MySoundData { };
        mySound.ImmRel[0] = 2.0f;

        vars.SyncReadOnly(0f, 60f, mySound, 7, 0);

        var code = vars.Compile("bass * 2 + frame;");
        Assert.NotNull(code);
        Assert.Equal(11.0, code!.Execute(), 6);
    }

    [Fact]
    public void InitCodeRoundTripsQAndMonitor()
    {
        var vars = new PerFrameVariables(new ProjectMEvalEngine());

        Assert.True(vars.RunInitCode("q1 = 3; q32 = 7; monitor = q1 + q32;", null, out var error), error?.ToString());
        Assert.Equal(3.0, vars.QValuesAfterInitCode[0]);
        Assert.Equal(7.0, vars.QValuesAfterInitCode[31]);
        Assert.Equal(10.0, vars.MonitorAfterInitCode);

        // 每帧开始时把回读值写回 VM：之后 per-frame 代码看到的 q1 应是 3 而不是 0
        vars.SyncAfterInitCode();
        var code = vars.Compile("q1 + q32 + monitor;");
        Assert.NotNull(code);
        Assert.Equal(20.0, code!.Execute(), 6);
    }

    [Fact]
    public void InitCodeCanReadSoundVars()
    {
        var vars = new PerFrameVariables(new ProjectMEvalEngine());
        var mySound = new MySoundData();
        mySound.ImmRel[0] = 4.0f;

        Assert.True(
            vars.RunInitCode(
                "q1 = bass * 10;",
                () => vars.SyncReadOnly(0f, 60f, mySound, 0, 0),
                out var error),
            error?.ToString());

        Assert.Equal(40.0, vars.QValuesAfterInitCode[0], 6);
    }

    [Fact]
    public void FailedInitCodeZeroesQ()
    {
        var vars = new PerFrameVariables(new ProjectMEvalEngine());
        vars.QValuesAfterInitCode[0] = 9;
        vars.MonitorAfterInitCode = 9;

        Assert.False(vars.RunInitCode("q1 = ;", null, out var error));
        Assert.NotNull(error);

        // state.cpp:1676-1678：编译失败时 q/monitor 归零
        Assert.All(vars.QValuesAfterInitCode, v => Assert.Equal(0.0, v));
        Assert.Equal(0.0, vars.MonitorAfterInitCode);
    }

    /// <summary>
    /// 注册了完整变量表之后，真实语料仍然全部可编译（等价于 cbplan §6.6 的 692/692）。
    /// </summary>
    [Fact]
    public void RealPresetsCompileAgainstRegistry()
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
            output.WriteLine("跳过：预设目录不存在 " + root);
            return;
        }

        var vars = new PerFrameVariables(new ProjectMEvalEngine());
        var failures = new List<string>();
        var count = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.milk", SearchOption.AllDirectories))
        {
            var preset = PresetKeyValueFile.Load(path);

            foreach (var prefix in new[] { "per_frame_init_", "per_frame_" })
            {
                var code = preset.ReadCode(prefix);
                if (code.Trim().Length == 0)
                {
                    continue;
                }

                count++;
                if (vars.Compile(code) is null)
                {
                    failures.Add($"{Path.GetFileName(path)} [{prefix}] {vars.Vm.LastError}");
                }
            }
        }

        output.WriteLine($"per-frame 语料: {count}");
        Assert.True(count > 100, $"语料数量异常（{count}）");

        foreach (var failure in failures.Take(10))
        {
            output.WriteLine("FAIL " + failure);
        }

        Assert.Empty(failures);
    }
}
