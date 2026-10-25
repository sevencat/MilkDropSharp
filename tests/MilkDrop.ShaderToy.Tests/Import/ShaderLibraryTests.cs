using MilkDrop.ShaderToy.Import;

using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

public sealed class ShaderLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "milkdrop-stlib-" + Guid.NewGuid().ToString("N"));

    public ShaderLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private string DatabasePath => Path.Combine(_root, ShaderDatabase.FileName);

    [Fact]
    public void Scan_WithoutDatabase_ReturnsEmptyWithWarning()
    {
        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Empty(library.Entries);
        Assert.Single(library.Warnings);
        Assert.Contains("shadertoy.db", library.Warnings[0]);
        Assert.Equal(DatabasePath, library.DatabasePath);
    }

    [Fact]
    public void Scan_ReadsNamesNewestFirst()
    {
        TestDatabase.Create(DatabasePath);

        TestDatabase.AddShader(DatabasePath, "old001", "Older Work", "alice", "2026-01-01 08:00:00", "raymarch,3d");
        TestDatabase.AddPass(DatabasePath, "old001", 0, "image", "void mainImage(out vec4 c, in vec2 f){c=vec4(0);}");

        TestDatabase.AddShader(DatabasePath, "new002", "Newer Work", "bob", "2026-05-06 07:08:09", "sdf");
        TestDatabase.AddPass(DatabasePath, "new002", 0, "common", "float x(){return 1.;}");
        TestDatabase.AddPass(DatabasePath, "new002", 1, "image", "void mainImage(out vec4 c, in vec2 f){c=vec4(1);}");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Empty(library.Warnings);
        Assert.Equal(2, library.Entries.Count);

        ShaderLibraryEntry newest = library.Entries[0];
        Assert.Equal("new002", newest.Id);
        Assert.Equal("Newer Work", newest.Title);
        Assert.Equal("bob", newest.Author);
        Assert.Equal(DatabasePath, newest.DatabasePath);
        Assert.Equal(new DateTime(2026, 5, 6, 7, 8, 9), newest.CapturedAt);
        Assert.Equal("Newer Work", newest.DisplayName);

        Assert.Equal("old001", library.Entries[1].Id);
    }

    [Fact]
    public void Scan_OnlyNeedsShadersTable()
    {
        // 列表只读名字那几列：故意只建 shaders 表，不建 renderpasses/inputs/outputs
        TestDatabase.CreateShadersOnly(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "abc123", "Name only", "alice");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Empty(library.Warnings);
        ShaderLibraryEntry entry = Assert.Single(library.Entries);
        Assert.Equal("abc123", entry.Id);
        Assert.Equal("Name only", entry.Title);

        // 细节是按需查的：表都不存在 → 明确返回 null，而不是让列表挂掉
        Assert.Null(library.TryReadDetails(entry));
    }

    [Fact]
    public void TryReadDetails_ReturnsCountsAndTags()
    {
        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "old001", "Older Work", "alice", tags: "raymarch,3d");
        TestDatabase.AddPass(DatabasePath, "old001", 0, "image", "");
        TestDatabase.AddInput(DatabasePath, "old001", 0, 0, "texture", "/media/a/hash.png", "media/a/hash.png");
        TestDatabase.AddInput(DatabasePath, "old001", 0, 1, "texture", "/media/a/other.png");
        TestDatabase.AddInput(DatabasePath, "old001", 0, 2, "keyboard");

        ShaderLibraryEntry entry = Assert.Single(ShaderLibrary.Scan(_root).Entries);
        ShaderDatabaseDetails? details = ShaderLibrary.Scan(_root).TryReadDetails(entry);

        Assert.NotNull(details);
        Assert.Equal(1, details.PassCount);
        Assert.Equal(1, details.MissingMedia);          // keyboard 的 /presets/... 不算缺图
        Assert.Equal(["raymarch", "3d"], details.Tags);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndFallsBackToSubstring()
    {
        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "flGyWc", "Lunchbreak", "nabr");
        TestDatabase.AddPass(DatabasePath, "flGyWc", 0, "image", "");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Equal("flGyWc", library.Find("flgywc")?.Id);
        Assert.Equal("flGyWc", library.Find("gyw")?.Id);
        Assert.Null(library.Find("nope"));
        Assert.Null(library.Find(""));
    }

    [Fact]
    public void Search_MatchesIdTitleAndAuthor()
    {
        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "aaa111", "Ray Marching", "iq", tags: "raymarch,sdf");
        TestDatabase.AddPass(DatabasePath, "aaa111", 0, "image", "");
        TestDatabase.AddShader(DatabasePath, "bbb222", "Plasma", "someone", tags: "noise");
        TestDatabase.AddPass(DatabasePath, "bbb222", 0, "image", "");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Equal(2, library.Search(null).Count());
        Assert.Equal("bbb222", Assert.Single(library.Search("plasma")).Id);
        Assert.Equal("aaa111", Assert.Single(library.Search("iq")).Id);
        Assert.Equal("bbb222", Assert.Single(library.Search("bbb2")).Id);

        // 标签不在列表里（细节按需查），所以不参与过滤
        Assert.Empty(library.Search("sdf"));
    }

    [Fact]
    public void Scan_BrokenDatabaseFile_WarnsInsteadOfThrowing()
    {
        File.WriteAllText(DatabasePath, "this is not a sqlite database");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Empty(library.Entries);
        Assert.NotEmpty(library.Warnings);
    }
}
