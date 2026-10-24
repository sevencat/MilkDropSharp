using MilkDrop.ShaderToy.Import;
using Xunit;
using Xunit.Abstractions;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 本地库扫描（P7 左侧列表的数据源）：两种目录布局、meta.json 优先、手工放进去也能扫到。
/// </summary>
public sealed class ShaderLibraryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = CreateTempRoot();

    [Fact]
    public void Scan_FindsStandardLayoutAndFlatJson()
    {
        WriteProject(Path.Combine(_root, "flGyWc"), "lunchbreak", "nabr");
        WriteProject(Path.Combine(_root, "A1b2C3"), "Fractal Land", "Kali");

        // 平铺布局：根目录直接放 json（仓库里的 res/shadertoy/stpreset 就是这种）
        File.WriteAllText(Path.Combine(_root, "XlfGRj.json"), MinimalProject("Star Nest", "Kali"));

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Empty(library.Warnings);
        Assert.Equal(3, library.Entries.Count);

        ShaderLibraryEntry fractal = library.Entries.Single(e => e.Id == "A1b2C3");
        Assert.Equal("Fractal Land", fractal.Title);
        Assert.Equal("Kali", fractal.Author);
        Assert.Equal("A1b2C3", fractal.Id);

        ShaderLibraryEntry flat = library.Entries.Single(e => e.Id == "XlfGRj");
        Assert.Equal("Star Nest", flat.Title);
        Assert.Equal(_root, flat.Directory);
    }

    [Fact]
    public void Scan_PrefersMetaJson_AndSkipsFullParse()
    {
        string directory = Path.Combine(_root, "Ms2SD1");
        WriteProject(directory, "Seascape", "TDM");

        // meta.json 与 project.json 冲突时以 meta.json 为准（P8 下载后的标准布局）
        File.WriteAllText(
            Path.Combine(directory, "meta.json"),
            """{ "id": "Ms2SD1", "title": "Seascape (本地改名)", "author": "TDM" }""");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        ShaderLibraryEntry entry = Assert.Single(library.Entries);
        Assert.Equal("Seascape (本地改名)", entry.Title);
    }

    [Fact]
    public void Scan_ToleratesBrokenJson_AndStillListsIt()
    {
        string directory = Path.Combine(_root, "broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"), "{ this is not json ");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        ShaderLibraryEntry entry = Assert.Single(library.Entries);
        Assert.Equal("broken", entry.Id);                 // 退回目录名
        Assert.Equal("broken", entry.DisplayName);
        Assert.NotEmpty(library.Warnings);                // 但提示里要说清楚
        output.WriteLine(string.Join(" | ", library.Warnings));
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmptyWithWarning()
    {
        ShaderLibrary library = ShaderLibrary.Scan(Path.Combine(_root, "nope"));

        Assert.Empty(library.Entries);
        Assert.Single(library.Warnings);
        Assert.Contains("库目录不存在", library.Warnings[0]);
    }

    [Fact]
    public void Search_MatchesIdTitleAndAuthor()
    {
        WriteProject(Path.Combine(_root, "flGyWc"), "lunchbreak", "nabr");
        WriteProject(Path.Combine(_root, "A1b2C3"), "Fractal Land", "Kali");

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.Equal(2, library.Search(null).Count());
        Assert.Equal(2, library.Search("  ").Count());
        Assert.Equal("A1b2C3", library.Search("kali").Single().Id);       // 作者（大小写不敏感）
        Assert.Equal("flGyWc", library.Search("LUNCH").Single().Id);      // 标题
        Assert.Single(library.Search("flGy"));                            // ID 片段
        Assert.Empty(library.Search("zzz"));
    }

    [Fact]
    public void Scan_FindsThumbnailNextToProject()
    {
        string directory = Path.Combine(_root, "withThumb");
        WriteProject(directory, "With Thumb", "me");
        File.WriteAllBytes(Path.Combine(directory, "thumbnail.png"), [0x89, 0x50, 0x4E, 0x47]);

        ShaderLibrary library = ShaderLibrary.Scan(_root);

        Assert.NotNull(Assert.Single(library.Entries).ThumbnailPath);
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

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mds-library-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteProject(string directory, string title, string author)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project.json"), MinimalProject(title, author));
    }

    private static string MinimalProject(string title, string author) =>
        $$"""
        [
          {
            "ver": "0.1",
            "info": { "id": "test", "name": "{{title}}", "username": "{{author}}" },
            "renderpass": [
              {
                "type": "image",
                "name": "Image",
                "code": "void mainImage(out vec4 c, in vec2 f) { c = vec4(1.0); }",
                "inputs": [],
                "outputs": []
              }
            ]
          }
        ]
        """;
}
