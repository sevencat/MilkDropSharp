using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// 本地 JSON 导入（P8 增补）：文件名即 ID，旁边的图片按相对路径一起带进 <c>textures/</c>。
/// </summary>
public sealed class LocalJsonImporterTests : IDisposable
{
    private readonly string _root = CreateTempRoot();

    [Fact]
    public void Import_UsesFileNameAsId_AndCopiesRelativeTexture()
    {
        string source = Path.Combine(NewSourceDirectory(), "lunchbreak.json");
        string sourceDirectory = Path.GetDirectoryName(source)!;

        Directory.CreateDirectory(Path.Combine(sourceDirectory, "img"));
        File.WriteAllBytes(Path.Combine(sourceDirectory, "img", "hash.png"), [1, 2, 3]);
        File.WriteAllText(source, Project("img/hash.png"));

        var importer = new LocalJsonImporter(LibraryRoot);
        ImportResult result = importer.Import(source);

        Assert.True(result.Success, result.Error);

        // 文件名是 ID（JSON 里的 info.id 是 flGyWc，不采用）
        string directory = Path.Combine(LibraryRoot, "lunchbreak");
        Assert.Equal(directory, result.Directory);
        Assert.Equal("lunchbreak", result.Id);

        Assert.True(File.Exists(Path.Combine(directory, "textures", "hash.png")));
        Assert.True(File.Exists(source));   // 拷贝不是移动

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/hash.png\"", project);
        Assert.DoesNotContain("img/hash.png", project);
        Assert.DoesNotContain('\n', project);   // 原文单行，没有被重新格式化
        Assert.Contains("\\n", project);        // 代码里的字面 \n 保留

        ShaderMeta meta = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        Assert.Equal("lunchbreak", meta.Id);
        Assert.Equal("Lunchbreak", meta.Title);
        Assert.Equal("nabr", meta.Author);
        Assert.Empty(meta.MissingResources);

        // 文件布局：raw.json + textures/（播放侧的数据源现在是 shadertoy.db，这里只验证导入产物）
        Assert.True(File.Exists(Path.Combine(directory, ShaderFiles.RawProject)));
        Assert.True(File.Exists(Path.Combine(directory, "textures", "hash.png")));
    }

    [Fact]
    public void Import_AbsoluteTexturePath_IsRewrittenToRelative()
    {
        string sourceDirectory = NewSourceDirectory();
        string texture = Path.Combine(sourceDirectory, "hash.png");
        File.WriteAllBytes(texture, [4, 5, 6]);

        string source = Path.Combine(sourceDirectory, "absolute.json");
        File.WriteAllText(source, Project(texture.Replace('\\', '/')));

        var importer = new LocalJsonImporter(LibraryRoot);
        ImportResult result = importer.Import(source);

        Assert.True(result.Success, result.Error);

        string directory = Path.Combine(LibraryRoot, "absolute");
        Assert.True(File.Exists(Path.Combine(directory, "textures", "hash.png")));

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/hash.png\"", project);
        Assert.DoesNotContain(sourceDirectory.Replace('\\', '/'), project);
    }

    [Fact]
    public void Import_SitePath_StillImportsButIsMarkedMissing()
    {
        string source = Path.Combine(NewSourceDirectory(), "withSitePath.json");
        File.WriteAllText(source, Project("\\/media\\/a\\/hash.png"));

        var importer = new LocalJsonImporter(LibraryRoot);
        ImportResult result = importer.Import(source);

        Assert.True(result.Success, result.Error);

        string directory = Path.Combine(LibraryRoot, "withsitepath");
        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("media", project);      // 本地拿不到就保持原样

        ShaderMeta meta = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        Assert.Single(meta.MissingResources);
    }

    [Fact]
    public void Import_CopiesAllSixCubemapFaces()
    {
        string sourceDirectory = NewSourceDirectory();
        for (var face = 0; face <= 5; face++)
        {
            File.WriteAllBytes(Path.Combine(sourceDirectory, $"cube_{face}.png"), [(byte)(0x10 + face)]);
        }

        string source = Path.Combine(sourceDirectory, "cubemap.json");
        File.WriteAllText(source, Project("cube_0.png", type: "cubemap"));

        var importer = new LocalJsonImporter(LibraryRoot);
        ImportResult result = importer.Import(source);

        Assert.True(result.Success, result.Error);

        string directory = Path.Combine(LibraryRoot, "cubemap");
        for (var face = 0; face <= 5; face++)
        {
            Assert.True(File.Exists(Path.Combine(directory, "textures", $"cube_{face}.png")), $"缺第 {face} 面");
        }

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/cube_0.png\"", project);
    }

    [Fact]
    public void Import_SameFileNameTwice_OverwritesOrCreatesSuffix()
    {
        string source = Path.Combine(NewSourceDirectory(), "twice.json");
        File.WriteAllText(source, Project(""));

        var importer = new LocalJsonImporter(LibraryRoot);

        ImportResult first = importer.Import(source);
        ImportResult second = importer.Import(source);

        Assert.True(first.Success && second.Success);
        Assert.Equal(first.Directory, second.Directory);
        Assert.False(Directory.Exists(Path.Combine(LibraryRoot, "twice-2")));

        ImportResult copy = importer.Import(source, overwrite: false);

        Assert.True(copy.Success, copy.Error);
        Assert.Equal(Path.Combine(LibraryRoot, "twice-2"), copy.Directory);
        Assert.Contains(copy.Warnings, w => w.Contains("另存为"));
    }

    [Fact]
    public void Import_InvalidJson_FailsWithReason()
    {
        string source = Path.Combine(NewSourceDirectory(), "broken.json");
        File.WriteAllText(source, "{ this is not json ");

        var importer = new LocalJsonImporter(LibraryRoot);
        ImportResult result = importer.Import(source);

        Assert.False(result.Success);
        Assert.Contains("不是可识别的 Shadertoy 工程", result.Error);
        Assert.False(Directory.Exists(LibraryRoot));   // 解析失败时不该建目录
    }

    [Fact]
    public void Import_OwnProjectJsonInLibrary_Fails()
    {
        // 先从别处导入一个作品，再"导入"库里它自己的 raw.json
        string source = Path.Combine(NewSourceDirectory(), "self.json");
        File.WriteAllText(source, Project(""));

        var importer = new LocalJsonImporter(LibraryRoot);
        ImportResult first = importer.Import(source);
        Assert.True(first.Success, first.Error);

        ImportResult again = importer.Import(Path.Combine(first.Directory!, ShaderFiles.RawProject));

        Assert.False(again.Success);
        Assert.Contains("已经在库里", again.Error);
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

    private string LibraryRoot => Path.Combine(_root, "library");

    private string NewSourceDirectory()
    {
        string directory = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>一个 texture/cubemap 输入的工程；代码里保留字面 <c>\n</c>，验证原文保真。</summary>
    private static string Project(string filepathLiteral, string type = "texture") =>
        "[{\"ver\":\"0.1\",\"info\":{\"id\":\"flGyWc\",\"name\":\"Lunchbreak\",\"username\":\"nabr\","
        + "\"tags\":[\"sound\"]},\"renderpass\":[{"
        + "\"inputs\":[{\"type\":\"" + type + "\",\"channel\":0,\"filepath\":\"" + filepathLiteral + "\","
        + "\"sampler\":{\"filter\":\"mipmap\",\"wrap\":\"repeat\",\"vflip\":\"true\"}}],"
        + "\"outputs\":[{\"id\":\"x\",\"channel\":0}],"
        + "\"code\":\"void mainImage(out vec4 c, in vec2 f)\\n{\\n\\tc = vec4(1.0);\\n}\","
        + "\"name\":\"Image\",\"description\":\"\",\"type\":\"image\"}]}]";

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mds-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }
}
