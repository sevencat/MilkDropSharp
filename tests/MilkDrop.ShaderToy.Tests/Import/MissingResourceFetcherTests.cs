using System.Net;
using MilkDrop.ShaderToy.Import;
using Xunit;

namespace MilkDrop.ShaderToy.Tests.Import;

/// <summary>
/// "补齐缺失资源"（P8 增补）：把 project.json 里还指向站点的资源抓下来存进 textures/，再改写路径。
/// </summary>
public sealed class MissingResourceFetcherTests : IDisposable
{
    private const string HashHex = "cbcbb5a6cfb55c36f8f021fbb0e3f69ac96339a39fa85cd96f2017a2192821b5";
    private const string MediaUrl = "https://www.shadertoy.com/media/a/" + HashHex + ".png";
    private const string MediaPath = "/media/a/" + HashHex + ".png";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mds-fetch-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Fetch_DownloadsSiteResource_AndRewritesJsonAndMeta()
    {
        string directory = WriteEntry(Project("\\/media\\/a\\/" + HashHex + ".png"), missingPath: MediaPath);

        var handler = new FakeHttpHandler().Bytes(MediaUrl, [1, 2, 3]);
        using var http = new HttpClient(handler);

        FetchResult result = await new MissingResourceFetcher(http).FetchAsync(directory);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(0, result.Failed);

        Assert.True(File.Exists(Path.Combine(directory, "textures", HashHex + ".png")));

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/" + HashHex + ".png\"", project);
        Assert.DoesNotContain("media\\/a\\/", project);   // 原文里的 \/ 转义形式

        ShaderMeta meta = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        Assert.Empty(meta.MissingResources);

        // 再补一次：文件已存在，不再发请求
        FetchResult again = await new MissingResourceFetcher(http).FetchAsync(directory);
        Assert.True(again.Success);
        Assert.Equal(1, handler.Hits[MediaUrl]);
    }

    [Fact]
    public async Task Fetch_ServerError_KeepsPathAndMissingMark()
    {
        string directory = WriteEntry(Project("\\/media\\/a\\/" + HashHex + ".png"), missingPath: MediaPath);

        using var http = new HttpClient(new FakeHttpHandler());   // 没登记 → 404
        FetchResult result = await new MissingResourceFetcher(http).FetchAsync(directory);

        Assert.True(result.Success);          // 整体不算失败
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(1, result.Failed);

        // 下载失败：JSON 保持站点路径（原文的 \/ 转义形式）
        Assert.Contains("media\\/a\\/", File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject)));

        ShaderMeta meta = Assert.IsType<ShaderMeta>(ShaderMeta.TryLoad(directory));
        MissingResource missing = Assert.Single(meta.MissingResources);
        Assert.Equal(MediaUrl, missing.Url);   // 统一成站点绝对 URL
        Assert.Contains("404", missing.Error);
    }

    [Fact]
    public async Task Fetch_Cubemap_GrabsAllSixFaces()
    {
        string cubeMedia = "/media/a/4dXGR8_0.png";
        string directory = WriteEntry(Project("\\/media\\/a\\/4dXGR8_0.png", type: "cubemap"), missingPath: cubeMedia);

        var handler = new FakeHttpHandler();
        for (var face = 0; face <= 5; face++)
        {
            handler.Bytes($"https://www.shadertoy.com/media/a/4dXGR8_{face}.png", [(byte)(0x20 + face)]);
        }

        using var http = new HttpClient(handler);
        FetchResult result = await new MissingResourceFetcher(http).FetchAsync(directory);

        Assert.True(result.Success, result.Error);
        Assert.Equal(6, result.Downloaded);

        for (var face = 0; face <= 5; face++)
        {
            Assert.True(File.Exists(Path.Combine(directory, "textures", $"4dXGR8_{face}.png")), $"缺第 {face} 面");
        }

        string project = File.ReadAllText(Path.Combine(directory, ShaderFiles.RawProject));
        Assert.Contains("\"filepath\":\"textures/4dXGR8_0.png\"", project);
    }

    [Fact]
    public async Task Fetch_NoSiteResources_ReportsNothingToDo()
    {
        string directory = WriteEntry(Project("textures/already.png"), missingPath: null);

        using var http = new HttpClient(new FakeHttpHandler());
        FetchResult result = await new MissingResourceFetcher(http).FetchAsync(directory);

        Assert.True(result.Success);
        Assert.Equal(0, result.Downloaded);
        Assert.Contains("没有需要补齐", result.Describe());
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

    private string WriteEntry(string projectJson, string? missingPath)
    {
        string directory = Path.Combine(_root, "work");
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, ShaderFiles.RawProject), projectJson);

        var meta = new ShaderMeta { Id = "work", Title = "Work" };

        if (missingPath is not null)
        {
            meta.MissingResources.Add(new MissingResource(
                missingPath,
                "工程里引用的是站点路径，没有随文件提供（需要联网下载或手工放到 textures/）"));
        }

        meta.Save(directory);
        return directory;
    }

    /// <summary>一个（可带 texture/cubemap 输入的）工程，代码里保留字面 <c>\n</c>。</summary>
    private static string Project(string filepathLiteral, string type = "texture") =>
        "[{\"ver\":\"0.1\",\"info\":{\"id\":\"work\",\"name\":\"Work\",\"username\":\"me\"},\"renderpass\":[{"
        + "\"inputs\":[{\"type\":\"" + type + "\",\"channel\":0,\"filepath\":\"" + filepathLiteral + "\","
        + "\"sampler\":{\"filter\":\"linear\",\"wrap\":\"repeat\",\"vflip\":\"true\"}}],"
        + "\"outputs\":[{\"id\":\"x\",\"channel\":0}],"
        + "\"code\":\"void mainImage(out vec4 c, in vec2 f)\\n{\\n\\tc = vec4(1.0);\\n}\","
        + "\"name\":\"Image\",\"description\":\"\",\"type\":\"image\"}]}]";
}

/// <summary>代理地址解析（下载/补齐资源都走它）。</summary>
public sealed class HttpProxyTests
{
    [Theory]
    [InlineData("localhost:1080", "socks5://localhost:1080/")]
    [InlineData("127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData("http://127.0.0.1:8888", "http://127.0.0.1:8888/")]
    public void CreateProxy_ParsesSchemes(string input, string expected)
    {
        var proxy = Assert.IsType<WebProxy>(ShaderToyDownloader.CreateProxy(input));
        Assert.Equal(expected, proxy.Address!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("off")]
    [InlineData("-")]
    public void CreateProxy_Disabled_ReturnsNull(string? input) =>
        Assert.Null(ShaderToyDownloader.CreateProxy(input));

    [Fact]
    public void CreateHttpClient_WithSocks5Proxy_DoesNotThrow()
    {
        using HttpClient client = ShaderToyDownloader.CreateHttpClient("socks5://127.0.0.1:1080");
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }
}
