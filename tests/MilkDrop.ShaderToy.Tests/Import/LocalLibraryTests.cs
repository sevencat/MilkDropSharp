using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 本地库的写侧（P8）：删除守卫、占用统计、"最近播放"记录。
/// </summary>
public sealed class LocalLibraryTests : IDisposable
{
    private readonly string _root = CreateTempRoot();

    [Fact]
    public void ComputeSize_CountsNestedFiles()
    {
        string directory = Path.Combine(_root, "flgywc");
        Directory.CreateDirectory(Path.Combine(directory, "textures"));
        File.WriteAllBytes(Path.Combine(directory, "project.json"), new byte[10]);
        File.WriteAllBytes(Path.Combine(directory, "textures", "a.png"), new byte[90]);

        Assert.Equal(100, LocalLibrary.ComputeSize(directory));
        Assert.Equal(0, LocalLibrary.ComputeSize(Path.Combine(_root, "missing")));
        Assert.Equal("100 B", LocalLibrary.DescribeSize(100));
    }

    [Fact]
    public void TryDelete_RemovesStandardEntry()
    {
        string directory = WriteEntry("flgywc");
        ShaderLibraryEntry entry = Assert.Single(ShaderLibrary.Scan(_root).Entries);

        Assert.True(LocalLibrary.TryDelete(_root, entry, out string? error), error);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void TryDelete_RefusesRootAndNestedAndForeignDirectories()
    {
        WriteEntry("flgywc");

        // 库根本身
        var rootEntry = new ShaderLibraryEntry(
            "root", "root", "", Path.Combine(_root, "project.json"), _root, null, 0, DateTime.Now);
        Assert.False(LocalLibrary.TryDelete(_root, rootEntry, out _));

        // 二级目录（防手滑删掉整个子库）
        string nested = Path.Combine(_root, "group", "flgywc");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "project.json"), "{}");
        var nestedEntry = new ShaderLibraryEntry(
            "nested", "nested", "", Path.Combine(nested, "project.json"), nested, null, 0, DateTime.Now);
        Assert.False(LocalLibrary.TryDelete(_root, nestedEntry, out string? nestedError));
        Assert.Contains("一级", nestedError);

        // 没有 project.json 的目录
        string other = Path.Combine(_root, "notes");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "readme.txt"), "x");
        var otherEntry = new ShaderLibraryEntry(
            "notes", "notes", "", Path.Combine(other, "project.json"), other, null, 0, DateTime.Now);
        Assert.False(LocalLibrary.TryDelete(_root, otherEntry, out string? otherError));
        Assert.Contains("project.json", otherError);
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void MarkPlayed_UpdatesExistingMetaOnly()
    {
        string directory = WriteEntry("flgywc");
        var now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(8));

        // 没有 meta.json：不创建、不回写（S8-6 手工放进去的作品）
        Assert.False(LocalLibrary.MarkPlayed(Path.Combine(directory, "project.json"), now));
        Assert.False(File.Exists(ShaderMeta.PathFor(directory)));

        new ShaderMeta { Id = "flGyWc", Title = "Lunchbreak" }.Save(directory);

        Assert.True(LocalLibrary.MarkPlayed(Path.Combine(directory, "project.json"), now));

        ShaderMeta reloaded = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        Assert.Equal(now, reloaded.LastPlayed);
        Assert.Equal("flGyWc", reloaded.Id);

        // 列表按最近播放排序
        ShaderLibraryEntry entry = Assert.Single(ShaderLibrary.Scan(_root).Entries);
        Assert.Equal(now, entry.LastPlayed);
        Assert.Equal(now.LocalDateTime, entry.SortTime);
    }

    [Fact]
    public void Scan_ReadsFavoriteAndMissingCountFromMeta()
    {
        string directory = WriteEntry("flgywc");
        var meta = new ShaderMeta { Id = "flGyWc", Title = "Lunchbreak", Favorite = true };
        meta.MissingResources.Add(new MissingResource("https://example.com/x.png", "HTTP 404"));
        meta.Save(directory);

        ShaderLibraryEntry entry = Assert.Single(ShaderLibrary.Scan(_root).Entries);

        Assert.True(entry.Favorite);
        Assert.Equal(1, entry.MissingResources);
        Assert.Equal("Lunchbreak", entry.Title);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }

    private string WriteEntry(string id)
    {
        string directory = Path.Combine(_root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"),
            """
            [{"ver":"0.1","info":{"id":"flGyWc","name":"Lunchbreak","username":"nabr"},"renderpass":[]}]
            """);
        return directory;
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mds-local-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }
}
