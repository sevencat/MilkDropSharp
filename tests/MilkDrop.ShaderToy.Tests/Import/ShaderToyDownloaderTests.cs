using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// P8 的下载链路：页面 → 工程 JSON → 资源 → 路径改写 → project.json/meta.json/缩略图。
/// </summary>
/// <remarks>
/// 全部走假的 <see cref="HttpMessageHandler"/>，不联网；这正是把 <see cref="HttpClient"/>
/// 做成构造参数的原因。
/// </remarks>
public sealed class ShaderToyDownloaderTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Download_WritesProjectMetaAndAssets_WithRelativePaths()
    {
        using var library = new TempLibrary();
        using var http = new HttpClient(BuildHandler());

        var downloader = new ShaderToyDownloader(library.Root, http);
        DownloadResult result = await downloader.DownloadAsync("tEst01", Options());

        Assert.True(result.Success, result.Error);

        string directory = Path.Combine(library.Root, "test01");
        Assert.Equal(directory, result.Directory);

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/hash.png\"", project);
        Assert.DoesNotContain("/media/a/hash.png", project);   // 站点路径已改写
        Assert.DoesNotContain('\n', project);                  // 原文是单行 JSON，没有被重新格式化
        Assert.Contains("\\n", project);                       // 代码里的字面 \n 保留（没有二次反转义）

        Assert.True(File.Exists(Path.Combine(directory, "textures", "hash.png")));
        Assert.True(File.Exists(Path.Combine(directory, "thumbnail.png")));

        ShaderMeta meta = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        Assert.Equal("tEst01", meta.Id);
        Assert.Equal("Test Work", meta.Title);
        Assert.Equal("tester", meta.Author);
        Assert.Equal("https://www.shadertoy.com/view/tEst01", meta.SourceUrl);
        Assert.Equal(FixedNow, meta.DownloadedAt);
        Assert.Equal(["sound", "demo"], meta.Tags);
        Assert.Empty(meta.MissingResources);
    }

    [Fact]
    public async Task Download_MissingTexture_StillSucceedsAndIsMarked()
    {
        using var library = new TempLibrary();
        FakeHttpHandler handler = BuildHandler(omitTexture: true);
        using var http = new HttpClient(handler);

        var downloader = new ShaderToyDownloader(library.Root, http);
        DownloadResult result = await downloader.DownloadAsync("tEst01", Options());

        Assert.True(result.Success, result.Error);

        MissingResource missing = Assert.Single(result.Missing);
        Assert.Equal("https://www.shadertoy.com/media/a/hash.png", missing.Url);

        string directory = Path.Combine(library.Root, "test01");

        // 拿不到就保持站点路径：加载时退化成黑纹理，UI 里标"缺资源"
        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("media", project);
        Assert.DoesNotContain("textures/hash.png", project);

        ShaderMeta meta = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        Assert.Single(meta.MissingResources);

        // 404 属于"不可重试"，只请求一次
        Assert.Equal(1, handler.Hits["https://www.shadertoy.com/media/a/hash.png"]);
    }

    [Fact]
    public async Task Download_RetriesOnTooManyRequests()
    {
        const string texture = "https://www.shadertoy.com/media/a/hash.png";

        using var library = new TempLibrary();
        FakeHttpHandler handler = new FakeHttpHandler()
            .Text(PageUrl("tEst01"), Html("tEst01", TextureProject("\\/media\\/a\\/hash.png")))
            .Bytes(ThumbUrl("tEst01"), [9, 9])
            .Sequence(texture, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        using var http = new HttpClient(handler);
        var downloader = new ShaderToyDownloader(library.Root, http);
        DownloadResult result = await downloader.DownloadAsync("tEst01", Options());

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Missing);           // 重试成功，不算缺资源
        Assert.Equal(2, handler.Hits[texture]); // 429 → 退避一次 → 200
    }

    [Fact]
    public async Task Download_SameIdAgain_OverwritesIncrementally_AndCopyModeCreatesSuffix()
    {
        using var library = new TempLibrary();
        FakeHttpHandler handler = BuildHandler();
        using var http = new HttpClient(handler);

        var downloader = new ShaderToyDownloader(library.Root, http);

        DownloadResult first = await downloader.DownloadAsync("tEst01", Options());
        DownloadResult second = await downloader.DownloadAsync("tEst01", Options());

        Assert.True(first.Success && second.Success);
        Assert.Equal(first.Directory, second.Directory);
        Assert.False(Directory.Exists(Path.Combine(library.Root, "test01-2")));

        // 资源已存在且非空 → 第二次不重复下载
        Assert.Equal(1, handler.Hits["https://www.shadertoy.com/media/a/hash.png"]);

        DownloadResult copy = await downloader.DownloadAsync("tEst01", Options(overwrite: false));

        Assert.True(copy.Success, copy.Error);
        Assert.Equal(Path.Combine(library.Root, "test01-2"), copy.Directory);
        Assert.Contains(copy.Warnings, w => w.Contains("另存为"));

        // 两份文件布局都在（播放侧不再读它们，抓取结果以 shadertoy.db 为准）
        Assert.True(File.Exists(Path.Combine(library.Root, "test01", ShaderFiles.RawProject)));
        Assert.True(File.Exists(Path.Combine(library.Root, "test01-2", ShaderFiles.RawProject)));
    }

    [Fact]
    public async Task Download_Cubemap_FetchesSixFaces_AndRewritesOnlyFaceZero()
    {
        using var library = new TempLibrary();
        FakeHttpHandler handler = new FakeHttpHandler()
            .Text(PageUrl("CubE01"), Html("CubE01", CubeProject("\\/media\\/a\\/cube_0.png")))
            .Bytes(ThumbUrl("CubE01"), [7, 7]);

        for (var face = 0; face <= 5; face++)
        {
            handler.Bytes($"https://www.shadertoy.com/media/a/cube_{face}.png", [(byte)(0x10 + face)]);
        }

        using var http = new HttpClient(handler);
        var downloader = new ShaderToyDownloader(library.Root, http);
        DownloadResult result = await downloader.DownloadAsync("CubE01", Options());

        Assert.True(result.Success, result.Error);

        string directory = Path.Combine(library.Root, "cube01");
        for (var face = 0; face <= 5; face++)
        {
            Assert.True(File.Exists(Path.Combine(directory, "textures", $"cube_{face}.png")), $"缺第 {face} 面");
        }

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/cube_0.png\"", project);
    }

    [Theory]
    [InlineData("flGyWc", "flGyWc")]
    [InlineData("  XlfGRj  ", "XlfGRj")]
    [InlineData("/view/Ms2SD1", "Ms2SD1")]
    [InlineData("https://www.shadertoy.com/view/flGyWc", "flGyWc")]
    [InlineData("https://www.shadertoy.com/view/flGyWc/", "flGyWc")]
    [InlineData("https://www.shadertoy.com/view/flGyWc?foo=1", "flGyWc")]
    public void TryParseTarget_AcceptsIdsAndUrls(string input, string expectedId)
    {
        Assert.True(ShaderToyDownloader.TryParseTarget(input, out string id, out string pageUrl, out string? error), error);
        Assert.Equal(expectedId, id);
        Assert.Equal("https://www.shadertoy.com/view/" + expectedId, pageUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("not an id")]
    [InlineData("https://example.com/")]
    public void TryParseTarget_RejectsGarbage(string input)
    {
        Assert.False(ShaderToyDownloader.TryParseTarget(input, out _, out _, out _));
    }

    private static DownloadOptions Options(bool overwrite = true) =>
        new()
        {
            Overwrite = overwrite,
            Now = FixedNow,
            RetryDelay = TimeSpan.Zero,
        };

    private static FakeHttpHandler BuildHandler(bool omitTexture = false)
    {
        var handler = new FakeHttpHandler()
            .Text(PageUrl("tEst01"), Html("tEst01", TextureProject("\\/media\\/a\\/hash.png")))
            .Bytes(ThumbUrl("tEst01"), [9, 9]);

        if (!omitTexture)
        {
            handler.Bytes("https://www.shadertoy.com/media/a/hash.png", [1, 2, 3]);
        }

        return handler;
    }

    private static string PageUrl(string id) => $"https://www.shadertoy.com/view/{id}";

    private static string ThumbUrl(string id) => $"https://www.shadertoy.com/media/shaders/{id}.png";

    private static string Html(string id, string projectJson) =>
        "<html><head><meta property=\"og:image\" content=\"" + ThumbUrl(id) + "\"></head>"
        + "<body><script>var gShaderToy = " + projectJson + ";</script></body></html>";

    /// <summary>一个 texture 输入的工程（代码里保留字面 <c>\n</c> 与 <c>\/</c>，验证原样保真）。</summary>
    private static string TextureProject(string filepathLiteral) =>
        "[{\"ver\":\"0.1\",\"info\":{\"id\":\"tEst01\",\"name\":\"Test Work\",\"username\":\"tester\","
        + "\"tags\":[\"sound\",\"demo\"]},\"renderpass\":[{"
        + "\"inputs\":[{\"id\":\"4dXGzr\",\"filepath\":\"" + filepathLiteral + "\",\"type\":\"texture\","
        + "\"channel\":0,\"sampler\":{\"filter\":\"mipmap\",\"wrap\":\"repeat\",\"vflip\":\"true\"}}],"
        + "\"outputs\":[{\"id\":\"4dfGRr\",\"channel\":0}],"
        + "\"code\":\"void mainImage(out vec4 c, in vec2 f)\\n{\\n\\tc = texture(iChannel0, f\\/iResolution.xy);\\n}\","
        + "\"name\":\"Image\",\"description\":\"\",\"type\":\"image\"}]}]";

    /// <summary>一个 cubemap 资源输入的工程（站点命名约定 <c>_0</c>）。</summary>
    private static string CubeProject(string filepathLiteral) =>
        "[{\"ver\":\"0.1\",\"info\":{\"id\":\"CubE01\",\"name\":\"Cube Test\",\"username\":\"tester\"},"
        + "\"renderpass\":[{"
        + "\"inputs\":[{\"id\":\"4dXGzr\",\"filepath\":\"" + filepathLiteral + "\",\"type\":\"cubemap\","
        + "\"channel\":0,\"sampler\":{\"filter\":\"linear\",\"wrap\":\"clamp\",\"vflip\":\"false\"}}],"
        + "\"outputs\":[{\"id\":\"4dfGRr\",\"channel\":0}],"
        + "\"code\":\"void mainImage(out vec4 c, in vec2 f)\\n{\\n\\tc = texture(iChannel0, vec3(f, 1.0));\\n}\","
        + "\"name\":\"Image\",\"description\":\"\",\"type\":\"image\"}]}]";

    /// <summary>临时本地库目录。</summary>
    private sealed class TempLibrary : IDisposable
    {
        public TempLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "mds-library-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响测试结论
            }
        }
    }
}
