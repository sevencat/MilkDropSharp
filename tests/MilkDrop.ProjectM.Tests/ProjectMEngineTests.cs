using System.Text;
using MilkDrop.ProjectM.Tests.Rendering;
using Silk.NET.OpenGLES;
using Xunit;

namespace MilkDrop.ProjectM.Tests;

/// <summary>
/// 引擎门面（P8）：待机预设、预设加载/切换、事件、参数夹取。
/// </summary>
[Collection("GL")]
public sealed class ProjectMEngineTests(OffscreenGlFixture gl) : IDisposable
{
    private const string RedPreset = """
        [preset00]
        MILKDROP_PRESET_VERSION=201
        PSVERSION_WARP=2
        PSVERSION_COMP=2
        warp_1=`shader_body { ret = tex2D(sampler_main, uv).xyz; }
        comp_1=`shader_body { ret = float3(1.0, 0.0, 0.0); }
        """;

    private readonly string _workDir = Directory.CreateTempSubdirectory("milkdrop-engine-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ConstructorLoadsIdlePresetAndRenders()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var engine = new ProjectMEngine([_workDir]);
        engine.SetWindowSize(32, 24);

        engine.RenderFrame();
        engine.RenderFrame();

        Assert.Equal(2, engine.FrameCount);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    [Fact]
    public void HardCutLoadsPresetImmediately()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        string presetPath = WritePreset("red.milk", RedPreset);

        using var engine = new ProjectMEngine([_workDir]);
        engine.SetWindowSize(32, 24);

        engine.LoadPresetFile(presetPath, smoothTransition: false);
        engine.RenderFrame();

        Assert.EndsWith("red.milk", engine.ActivePresetFileName, StringComparison.Ordinal);
        Assert.False(engine.IsTransitioning);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    [Fact]
    public void SmoothCutKeepsBothPresetsUntilTransitionEnds()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        string presetPath = WritePreset("red.milk", RedPreset);

        using var engine = new ProjectMEngine([_workDir]);
        engine.SetWindowSize(32, 24);
        engine.SoftCutDuration = 0.05;

        engine.LoadPresetFile(presetPath, smoothTransition: true);
        Assert.True(engine.IsTransitioning);

        engine.RenderFrame();
        Thread.Sleep(120);
        engine.RenderFrame();

        Assert.False(engine.IsTransitioning);
        Assert.EndsWith("red.milk", engine.ActivePresetFileName, StringComparison.Ordinal);
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    [Fact]
    public void FailedLoadRaisesEvent()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var engine = new ProjectMEngine([_workDir]);
        engine.SetWindowSize(32, 24);

        string? failedFile = null;
        string? message = null;
        engine.PresetSwitchFailed += (file, detail) =>
        {
            failedFile = file;
            message = detail;
        };

        engine.LoadPresetFile(Path.Combine(_workDir, "does-not-exist.milk"), smoothTransition: false);

        Assert.NotNull(failedFile);
        Assert.False(string.IsNullOrEmpty(message));
        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    [Fact]
    public void PresetSwitchRequestedWhenDurationElapses()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        string presetPath = WritePreset("red.milk", RedPreset);

        using var engine = new ProjectMEngine([_workDir]);
        engine.SetWindowSize(32, 24);
        // 时长在 StartPreset() 时采样，所以必须先设置再加载；
        // EasterEgg 默认 1.0 会按高斯分布抖动时长，这里关掉它。
        engine.EasterEgg = 0;
        engine.PresetDuration = 0.01;

        engine.LoadPresetFile(presetPath, smoothTransition: false);

        var requested = 0;
        engine.PresetSwitchRequested += _ => requested++;

        Thread.Sleep(60);
        engine.RenderFrame();

        Assert.Equal(1, requested);
    }

    [Fact]
    public void MeshSizeIsRoundedToEvenAndClamped()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var engine = new ProjectMEngine([_workDir]);

        engine.SetMeshSize(33, 25);
        Assert.Equal((34u, 26u), engine.MeshSize);

        engine.SetMeshSize(1, 9999);
        Assert.Equal((8u, 400u), engine.MeshSize);
    }

    [Fact]
    public void LoadPresetDataAcceptsStream()
    {
        if (!gl.TryMakeCurrent())
        {
            return;
        }

        using var engine = new ProjectMEngine([_workDir]);
        engine.SetWindowSize(32, 24);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(RedPreset));
        engine.LoadPresetData(stream, smoothTransition: false);
        engine.RenderFrame();

        Assert.Equal(GLEnum.NoError, gl.Gl.GetError());
    }

    private string WritePreset(string name, string content)
    {
        string path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
