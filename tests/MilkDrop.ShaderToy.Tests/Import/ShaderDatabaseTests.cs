using MilkDrop.ShaderToy.Import;
using MilkDrop.ShaderToy.Pass;

using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

public sealed class ShaderDatabaseTests : IDisposable
{
    private const string ImageCode = "void mainImage(out vec4 c, in vec2 f){ c = vec4(0.5); }";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "milkdrop-stdb-" + Guid.NewGuid().ToString("N"));

    public ShaderDatabaseTests() => Directory.CreateDirectory(_root);

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

    private static string MinimalProject(string id, string name, string author, string code) =>
        $$"""
          [{"ver":"0.1","info":{"id":"{{id}}","name":"{{name}}","username":"{{author}}","description":"demo","tags":["sdf"]},
            "renderpass":[
              {"inputs":[],"outputs":[{"id":"4dfGRr","channel":0}],"code":"{{code}}","name":"Image","description":"","type":"image"}
            ]}]
          """;

    [Fact]
    public void TryLoadProject_UsesRawJson()
    {
        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "abc123", "From json", "alice", rawJson: MinimalProject("abc123", "From json", "alice", ImageCode));

        using var database = new ShaderDatabase(DatabasePath);
        Assert.True(database.IsOpen, database.Error);

        ShaderToyProject? project = database.TryLoadProject("abc123", out string? error);

        Assert.Null(error);
        Assert.NotNull(project);
        Assert.Equal("From json", project.Metadata.Name);
        Assert.Equal("alice", project.Metadata.Author);
        Assert.Equal("abc123", project.Metadata.Id);
        Assert.Equal(["sdf"], project.Metadata.Tags);

        ShaderPass pass = Assert.Single(project.Passes);
        Assert.Equal(ShaderPassType.Image, pass.Type);
        Assert.Equal("4dfGRr", Assert.Single(pass.Outputs).Id);
        Assert.Equal(ImageCode, pass.Code);
    }

    [Fact]
    public void TryLoadProject_TakesPassCodeFromRenderpassesTable()
    {
        const string edited = "void mainImage(out vec4 c, in vec2 f){ c = vec4(1.0, 0.0, 0.0, 1.0); }";

        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "abc123", "Edited", "alice", rawJson: MinimalProject("abc123", "Edited", "alice", ImageCode));
        TestDatabase.AddPass(DatabasePath, "abc123", 0, "image", edited);

        using var database = new ShaderDatabase(DatabasePath);
        ShaderToyProject? project = database.TryLoadProject("abc123", out string? error);

        Assert.Null(error);
        Assert.NotNull(project);
        Assert.Equal(edited, Assert.Single(project.Passes).Code);
    }

    [Fact]
    public void TryLoadProject_IgnoresTableCodeWhenPassTypeDiffers()
    {
        // 库里的 row 顺序和 JSON 对不上（表被手工改过）：宁可用 JSON 的代码，也不能张冠李戴
        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "abc123", "Mismatch", "alice", rawJson: MinimalProject("abc123", "Mismatch", "alice", ImageCode));
        TestDatabase.AddPass(DatabasePath, "abc123", 0, "buffer", "// buffer code");

        using var database = new ShaderDatabase(DatabasePath);
        ShaderToyProject? project = database.TryLoadProject("abc123", out string? error);

        Assert.Null(error);
        Assert.NotNull(project);
        Assert.Equal(ImageCode, Assert.Single(project.Passes).Code);
    }

    [Fact]
    public void TryLoadProject_WithoutRawJson_BuildsFromTables()
    {
        TestDatabase.Create(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "tbl001", "From tables", "bob", tags: "raymarch,3d", description: "语料");
        TestDatabase.AddPass(DatabasePath, "tbl001", 0, "common", "float field(vec3 p){ return p.x; }");
        TestDatabase.AddPass(DatabasePath, "tbl001", 1, "image", ImageCode, name: "Image");
        TestDatabase.AddOutput(DatabasePath, "tbl001", 1, 0, "buf01");
        TestDatabase.AddInput(DatabasePath, "tbl001", 1, 0, "buffer", inputId: "buf01");
        TestDatabase.AddInput(
            DatabasePath, "tbl001", 1, 1, "texture",
            filePath: "/media/a/hash.png", localFile: "media/a/hash.png", filter: "mipmap", wrap: "repeat", vflip: "true");
        TestDatabase.AddInput(DatabasePath, "tbl001", 1, 2, "keyboard");

        using var database = new ShaderDatabase(DatabasePath);
        ShaderToyProject? project = database.TryLoadProject("tbl001", out string? error);

        Assert.Null(error);
        Assert.NotNull(project);
        Assert.Equal("From tables", project.Metadata.Name);
        Assert.Equal("bob", project.Metadata.Author);
        Assert.Equal(["raymarch", "3d"], project.Metadata.Tags);
        Assert.NotEmpty(project.Warnings);      // 明确告诉用户"是按表重建的"

        Assert.Equal(2, project.Passes.Count);

        ShaderPass common = project.Passes[0];
        Assert.Equal(ShaderPassType.Common, common.Type);
        Assert.Equal("float field(vec3 p){ return p.x; }", common.Code);

        ShaderPass image = project.Passes[1];
        Assert.Equal(ShaderPassType.Image, image.Type);
        Assert.Equal(ImageCode, image.Code);
        Assert.Equal("buf01", Assert.Single(image.Outputs).Id);

        Assert.Equal(3, image.Inputs.Count);

        ShaderPassInput buffer = image.Inputs[0];
        Assert.Equal(ShaderInputType.Buffer, buffer.Type);
        Assert.Equal("buf01", buffer.Id);

        ShaderPassInput texture = image.Inputs[1];
        Assert.Equal(ShaderInputType.Texture, texture.Type);
        Assert.Equal("/media/a/hash.png", texture.Path);          // 站点路径原样保留，由管线在 media/ 下找
        Assert.Equal(SamplerFilter.Mipmap, texture.Sampler.Filter);
        Assert.Equal(SamplerWrap.Repeat, texture.Sampler.Wrap);
        Assert.True(texture.Sampler.VFlip);

        Assert.Equal(ShaderInputType.Keyboard, image.Inputs[2].Type);
    }

    [Fact]
    public void TryReadDetails_CountsOnlyImageLikeInputs()
    {
        TestDatabase.CreateShadersOnly(DatabasePath);
        TestDatabase.AddShader(DatabasePath, "abc123", "Details", "alice", tags: "sdf,plasma");

        using var shadersOnly = new ShaderDatabase(DatabasePath);

        // 只建了 shaders 表：细节查询走不通，返回 null（列表本身不受影响）
        Assert.Null(shadersOnly.TryReadDetails("abc123"));

        TestDatabase.Create(DatabasePath + ".2");
        TestDatabase.AddShader(DatabasePath + ".2", "abc123", "Details", "alice", tags: "sdf,plasma");
        TestDatabase.AddPass(DatabasePath + ".2", "abc123", 0, "image", "");
        TestDatabase.AddInput(DatabasePath + ".2", "abc123", 0, 0, "texture", "/media/a/x.png", "media/a/x.png");
        TestDatabase.AddInput(DatabasePath + ".2", "abc123", 0, 1, "texture", "/media/a/y.png");
        TestDatabase.AddInput(DatabasePath + ".2", "abc123", 0, 2, "keyboard", "/presets/tex00.jpg");

        using var database = new ShaderDatabase(DatabasePath + ".2");
        ShaderDatabaseDetails? details = database.TryReadDetails("abc123");

        Assert.NotNull(details);
        Assert.Equal(1, details.PassCount);
        Assert.Equal(1, details.MissingMedia);
        Assert.Equal(["sdf", "plasma"], details.Tags);

        Assert.Null(database.TryReadDetails("nope00"));
    }

    [Fact]
    public void TryLoadProject_UnknownId_ReturnsNullWithReason()
    {
        TestDatabase.Create(DatabasePath);

        using var database = new ShaderDatabase(DatabasePath);
        ShaderToyProject? project = database.TryLoadProject("nope00", out string? error);

        Assert.Null(project);
        Assert.Contains("nope00", error);
    }

    [Fact]
    public void Constructor_MissingFile_ReportsError()
    {
        using var database = new ShaderDatabase(DatabasePath);

        Assert.False(database.IsOpen);
        Assert.NotNull(database.Error);
    }
}
