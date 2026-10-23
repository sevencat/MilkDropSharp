using MilkDrop.Classic.Render;
using MilkDrop.Classic.Visualizer.Sound;
using MilkDrop.Classic.Visualizer.Sprites;
using MilkDrop.Eval;
using Xunit;

namespace MilkDrop.Classic.Tests;

/// <summary>
/// <c>texmgr</c> 的行为：槽管理、纹理复用/引用计数、init 与 per-frame 代码、
/// 以及 <c>milk2_img.ini</c>（<c>[imgNN]</c> 段）的读取（<c>texmgr.cpp</c> / <c>plugin.cpp:8567-8713</c>）。
/// </summary>
public sealed class SpriteManagerTests
{
    private sealed class FakeTexture(int width, int height) : ITexture, IDisposable
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class StubLoader
    {
        public List<string> Requests { get; } = [];

        public FakeTexture? Next { get; set; } = new(64, 32);

        public FakeTexture? Load(string fileName, uint colorKey)
        {
            Requests.Add($"{fileName}|{colorKey:X6}");
            return Next;
        }
    }

    private static SpriteManager CreateManager(out StubLoader loader)
    {
        loader = new StubLoader();
        return new SpriteManager(new ProjectMEvalEngine(), loader.Load);
    }

    [Fact]
    public void StartsWithSixteenEmptySlots()
    {
        var manager = CreateManager(out _);

        Assert.Equal(16, manager.Slots.Count);
        Assert.Equal(0, manager.ActiveCount);
        Assert.All(manager.Slots, slot => Assert.False(slot.IsActive));
    }

    [Fact]
    public void LoadFillsSlotFromTexture()
    {
        var manager = CreateManager(out var loader);
        loader.Next = new FakeTexture(200, 100);

        var result = manager.LoadTexture(@"C:\sprites\face1.jpg", 3, string.Empty, string.Empty, 12.5f, 40, 0x112233);

        Assert.True(result.Ok);
        var slot = manager[3];
        Assert.True(slot.IsActive);
        Assert.Equal(200, slot.ImageWidth);
        Assert.Equal(100, slot.ImageHeight);
        Assert.Equal(12.5f, slot.StartTime);
        Assert.Equal(40, slot.StartFrame);
        Assert.Single(loader.Requests);
        Assert.EndsWith("|112233", loader.Requests[0]);
    }

    [Fact]
    public void InitCodeRunsAndSetsOutputs()
    {
        var manager = CreateManager(out _);

        manager.LoadTexture("a.png", 0, "x = 0.25; sy = 0.5;", "x = x + 0.1;", 0f, 0, 0);

        var slot = manager[0];
        Assert.Equal(0.25, slot.Get("x"), 6);
        Assert.Equal(0.5, slot.Get("sy"), 6);

        // per-frame 代码不自动执行；执行一次才 +0.1
        slot.RunFrameCode();
        Assert.Equal(0.35, slot.Get("x"), 6);
    }

    [Fact]
    public void InitCodeStartsFromDefaults()
    {
        var manager = CreateManager(out _);

        manager.LoadTexture("a.png", 0, "x = 0.9;", string.Empty, 0f, 0, 0);
        manager.LoadTexture("b.png", 0, string.Empty, string.Empty, 0f, 0, 0);

        // 原版 RegisterBuiltInVariables 会重置变量，再写默认值（texmgr.cpp:216-231）
        var slot = manager[0];
        Assert.Equal(0.5, slot.Get("x"), 6);
        Assert.Equal(1.0, slot.Get("sx"), 6);
        Assert.Equal(1.0, slot.Get("burn"), 6);
        Assert.Equal(0.0, slot.Get("done"), 6);
    }

    [Fact]
    public void BadCodeIsReportedAsWarning()
    {
        var manager = CreateManager(out _);

        var result = manager.LoadTexture("a.png", 0, "x = ;;;", "y = (((", 0f, 0, 0);

        Assert.True(result.Ok);
        Assert.True(result.Warnings.HasFlag(SpriteLoadWarnings.InitCode));
        Assert.True(result.Warnings.HasFlag(SpriteLoadWarnings.FrameCode));
        Assert.NotNull(manager[0].CodeError);
        Assert.Null(manager[0].FrameCode);
    }

    [Fact]
    public void BadSlotAndMissingFileAreReported()
    {
        var manager = CreateManager(out var loader);
        loader.Next = null;

        Assert.Equal(SpriteLoadError.BadIndex, manager.LoadTexture("a.png", -1, "", "", 0f, 0, 0).Error);
        Assert.Equal(SpriteLoadError.BadIndex, manager.LoadTexture("a.png", 16, "", "", 0f, 0, 0).Error);
        Assert.Equal(SpriteLoadError.BadFile, manager.LoadTexture("a.png", 0, "", "", 0f, 0, 0).Error);
        Assert.False(manager[0].IsActive);
    }

    [Fact]
    public void SameFileSharesTextureUntilLastSlotDies()
    {
        var manager = CreateManager(out var loader);
        var texture = new FakeTexture(32, 32);
        loader.Next = texture;

        manager.LoadTexture("same.png", 0, "", "", 0f, 0, 0);
        manager.LoadTexture("same.png", 1, "", "", 0f, 0, 0);

        Assert.Single(loader.Requests);            // 第二次是"实例"，不再解码
        Assert.Same(manager[0].Texture, manager[1].Texture);

        manager.KillTexture(0);
        Assert.Equal(0, texture.DisposeCount);     // 还有一个槽在用
        Assert.True(manager[1].IsActive);

        manager.KillTexture(1);
        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void KillByUserDataRemovesAllMatchingSlots()
    {
        var manager = CreateManager(out var loader);
        loader.Next = new FakeTexture(8, 8);

        manager.LoadTexture("a.png", 0, "", "", 0f, 0, 0);
        manager.LoadTexture("b.png", 1, "", "", 0f, 0, 0);
        manager[0].UserData = 7;
        manager[1].UserData = 7;

        Assert.Equal(2, manager.KillByUserData(7));
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void FindsFreeSlotThenOldest()
    {
        var manager = CreateManager(out var loader);
        loader.Next = new FakeTexture(4, 4);

        Assert.Equal(0, manager.FindSlotForNewSprite());

        for (var i = 0; i < 16; i++)
        {
            manager.LoadTexture($"s{i}.png", i, "", "", i, i, 0);
        }

        // 全满 → 回收 StartFrame 最小的（0 号）
        Assert.Equal(0, manager.FindSlotForNewSprite());
    }

    [Fact]
    public void SyncInputsUsesSpawnTimeAndFrame()
    {
        var manager = CreateManager(out _);
        manager.LoadTexture("a.png", 0, "", "", 10f, 100, 0);

        var sound = new MySoundData();
        sound.ImmRel[0] = 1.5f;
        sound.AvgRel[2] = 0.75f;

        manager[0].SyncInputs(12.5f, 59.94f, sound, 0.4, 130);

        Assert.Equal(2.5, manager[0].Get("time"), 4);
        Assert.Equal(30.0, manager[0].Get("frame"), 4);
        Assert.Equal(59.94f, manager[0].Get("fps"), 4);
        Assert.Equal(0.4, manager[0].Get("progress"), 4);
        Assert.Equal(1.5, manager[0].Get("bass"), 4);
        Assert.Equal(0.75, manager[0].Get("treb_att"), 4);
    }

    [Fact]
    public void LaunchFromIniReadsSectionAndSplitsCodeLines()
    {
        var root = Path.Combine(Path.GetTempPath(), "milkdrop_sprite_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ini = Path.Combine(root, "milk2_img.ini");

        File.WriteAllLines(ini,
        [
            "[img00]",
            "img=sprites\\burst1.png",
            "colorkey=0x102030",
            "init_1=x = 0.25;",
            "init_2=rot = 0.5;",
            "code_1=sx = is_",
            "code_2=beat * 0.1;",
            "; 注释行应该被忽略",
            "[img01]",
            "img=sprites\\other.jpg",
            "colorkey_lo=0x0000FF",
            "colorkey=0x000000",
        ]);

        try
        {
            var manager = CreateManager(out var loader);

            Assert.True(manager.LaunchFromIni(ini, 0, -1, root, 3f, 7, out var error), error);

            var slot = manager[0];
            Assert.Equal(Path.Combine(root, "sprites\\burst1.png"), slot.FileName);
            Assert.EndsWith("|102030", loader.Requests[0]);
            Assert.Equal(0.25, slot.Get("x"), 6);   // init 代码执行过
            Assert.Equal(0.5, slot.Get("rot"), 6);
            Assert.Contains("is_beat", slot.FrameCodeText);   // 两行拼成一个标识符
            Assert.NotNull(slot.FrameCode);
            Assert.Equal(0, slot.UserData);

            // colorkey 覆盖 colorkey_lo
            Assert.True(manager.LaunchFromIni(ini, 1, -1, root, 3f, 8, out _));
            Assert.EndsWith("|000000", loader.Requests[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LaunchFromIniReportsMissingSectionAndImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "milkdrop_sprite_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ini = Path.Combine(root, "milk2_img.ini");
        File.WriteAllLines(ini, ["[img03]", "img=", "[img04]", "colorkey=1"]);

        try
        {
            var manager = CreateManager(out _);

            Assert.False(manager.LaunchFromIni(ini, 0, -1, root, 0f, 0, out var missingSection));
            Assert.Contains("img00", missingSection);

            Assert.False(manager.LaunchFromIni(ini, 3, -1, root, 0f, 0, out var missingImage));
            Assert.Contains("img 未定义", missingImage);

            Assert.False(manager.LaunchFromIni(ini, 4, -1, root, 0f, 0, out var missingName));
            Assert.Contains("img 未定义", missingName);

            Assert.False(manager.LaunchFromIni(
                Path.Combine(root, "nope.ini"), 0, -1, root, 0f, 0, out var noFile));
            Assert.NotNull(noFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResetTexturesDisposesEverything()
    {
        var manager = CreateManager(out var loader);
        var texture = new FakeTexture(16, 16);
        loader.Next = texture;

        manager.LoadTexture("a.png", 0, "", "", 0f, 0, 0);
        manager.ResetTextures();

        Assert.Equal(1, texture.DisposeCount);
        Assert.Equal(0, manager.ActiveCount);
    }
}
