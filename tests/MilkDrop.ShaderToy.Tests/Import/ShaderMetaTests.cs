using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>meta.json 的读写（P8）：私有元信息不污染 project.json，且要能容忍手改。</summary>
public sealed class ShaderMetaTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "mds-meta-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void SaveAndLoad_RoundTripsAllFields()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8));

        var meta = new ShaderMeta
        {
            Id = "flGyWc",
            Title = "午休 时间",     // 中文不能被写成 \uXXXX 才好读
            Author = "nabr",
            SourceUrl = "https://www.shadertoy.com/view/flGyWc",
            DownloadedAt = now,
            Favorite = true,
        };

        meta.Tags.Add("sound");
        meta.MissingResources.Add(new MissingResource("https://example.com/x.png", "HTTP 404"));
        meta.Save(_directory);

        string text = File.ReadAllText(ShaderMeta.PathFor(_directory));
        Assert.Contains("午休 时间", text);

        ShaderMeta loaded = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(_directory));
        Assert.Equal(meta.Id, loaded.Id);
        Assert.Equal(meta.Title, loaded.Title);
        Assert.Equal(meta.Author, loaded.Author);
        Assert.Equal(meta.SourceUrl, loaded.SourceUrl);
        Assert.Equal(now, loaded.DownloadedAt);
        Assert.True(loaded.Favorite);
        Assert.Equal(["sound"], loaded.Tags);
        Assert.Single(loaded.MissingResources);
    }

    [Fact]
    public void TryParse_AcceptsStringFormMissingResources()
    {
        // 手改/旧版本可能只写了 URL 字符串，不该让整张列表炸掉
        const string json = """
            { "id": "abc", "missingResources": ["https://example.com/a.png"] }
            """;

        Assert.True(ShaderMeta.TryParse(json, out ShaderMeta? meta));
        MissingResource missing = Assert.Single(meta!.MissingResources);
        Assert.Equal("https://example.com/a.png", missing.Url);
        Assert.Null(missing.Error);
    }

    [Fact]
    public void TryLoad_MalformedFileReturnsNull()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ShaderMeta.PathFor(_directory), "{ not json");

        Assert.Null(ShaderMeta.TryLoad(_directory));
        Assert.Null(ShaderMeta.TryLoad(Path.Combine(_directory, "missing")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }
}
