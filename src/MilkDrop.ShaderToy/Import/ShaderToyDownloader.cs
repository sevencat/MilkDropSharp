using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Import;

/// <summary>下载行为的开关（UI / 命令行共用）。</summary>
public sealed class DownloadOptions
{
    /// <summary>同名作品已存在时是否覆盖（false 表示另存为 <c>&lt;id&gt;-2</c>）。</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>是否下载缩略图（失败只是没有图，不算缺资源）。</summary>
    public bool DownloadThumbnail { get; init; } = true;

    /// <summary>本地已存在且非空的资源跳过（S8：重复下载只做增量）。</summary>
    public bool SkipExistingAssets { get; init; } = true;

    /// <summary>同时下载的资源数（S8-4：≤4）。</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>429/5xx/网络错误的重试次数。</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>重试的基础退避时间（第 n 次等待 n 倍）。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>写入 meta.json 的"下载时间"（测试里固定它）。</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

/// <summary>一次下载的结果（不抛异常，UI 直接展示）。</summary>
/// <param name="Success">工程与至少一份 JSON 是否落地成功。</param>
/// <param name="Id">作品 ID（输入解析后的）。</param>
/// <param name="Directory">落盘目录（失败时为 null）。</param>
/// <param name="Title">标题（取自 <c>info.name</c>）。</param>
/// <param name="Author">作者（<c>info.username</c>）。</param>
/// <param name="PageUrl">来源页面。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Assets">成功落盘的资源（相对路径）。</param>
/// <param name="Missing">失败/缺失的资源。</param>
/// <param name="Warnings">容忍项（缩略图失败、volume 不支持……）。</param>
public sealed record DownloadResult(
    bool Success,
    string Id,
    string? Directory,
    string Title,
    string Author,
    string PageUrl,
    string? Error,
    IReadOnlyList<string> Assets,
    IReadOnlyList<MissingResource> Missing,
    IReadOnlyList<string> Warnings)
{
    /// <summary>一句话摘要（命令行/Toast 用）。</summary>
    public string Describe() => Success
        ? $"{Title} ({Id}) → {Directory}；资源 {Assets.Count} 个"
          + (Missing.Count > 0 ? $"，缺 {Missing.Count} 个" : string.Empty)
          + (Warnings.Count > 0 ? $"，提示 {Warnings.Count} 条" : string.Empty)
        : $"下载失败（{Id}）：{Error}";
}

/// <summary>
/// 从 shadertoy.com 下载一个作品到本地库（<c>shaderToyPlan.md</c> P8）。
/// </summary>
/// <remarks>
/// <para>
/// 流程：取页面 → 抠内嵌 JSON（<see cref="ShaderToyPage"/>）→ 用容忍型导入器校验/取元信息 →
/// 按 <c>inputs[]</c> 下载资源到 <c>textures/</c> → **在 JSON 原文上**把资源路径改成相对路径
/// （<see cref="JsonTextRewriter"/>）→ 写 <c>raw.json</c>（原样保真）+ <c>meta.json</c> + <c>thumbnail.*</c>。
/// </para>
/// <para>
/// 单个资源失败不中断整次下载：只记进 <see cref="ShaderMeta.MissingResources"/>，
/// 作品仍可加载（管线会退化成黑纹理并在 UI 提示）。
/// </para>
/// <para>
/// 目录名固定为 ID 的小写形式（S8-5）：标题里的 <c>: ? *</c> 与超长文本都不进路径。
/// </para>
/// </remarks>
public sealed class ShaderToyDownloader
{
    /// <summary>站点根（<c>filepath</c> 是站点绝对路径，要拼上它）。</summary>
    public const string SiteBase = "https://www.shadertoy.com";

    /// <summary>cd / 图床对空 UA 会 403，必须带一个正常的（S8-4）。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    /// <summary>资源子目录（相对作品目录，整目录可整体拷走）。</summary>
    public const string TextureFolder = ShaderFiles.TextureFolder;

    private static readonly Regex IdPattern = new("^[A-Za-z0-9]{4,16}$", RegexOptions.Compiled);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HttpClient _http;
    private readonly string _libraryRoot;

    /// <param name="libraryRoot">本地库根目录（不存在会在下载时创建）。</param>
    /// <param name="http">可注入的客户端（测试用假 handler）。为 null 时用内置的默认配置。</param>
    public ShaderToyDownloader(string libraryRoot, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        _libraryRoot = Path.GetFullPath(libraryRoot);
        _http = http ?? CreateHttpClient();
    }

    /// <summary>本地库根目录（绝对路径）。</summary>
    public string LibraryRoot => _libraryRoot;

    /// <summary>进程级默认代理（由 <c>Program</c> 启动时按 <c>--proxy</c>/环境变量/设置文件配置）。</summary>
    private static string? _defaultProxyUrl;

    /// <summary>当前默认代理（null 表示不指定，走系统默认）。</summary>
    public static string? DefaultProxyUrl => _defaultProxyUrl;

    /// <summary>
    /// 设置进程级默认代理。
    /// </summary>
    /// <param name="proxyUrl">
    /// 支持 <c>socks5://127.0.0.1:1080</c>、<c>http://host:port</c>，也接受裸的 <c>host:port</c>（按 socks5 处理）；
    /// <c>none</c>/<c>off</c>/空 表示不走代理。
    /// </param>
    public static void SetDefaultProxy(string? proxyUrl) => _defaultProxyUrl = proxyUrl;

    /// <summary>
    /// 按字符串造 <see cref="IWebProxy"/>（<c>socks5://</c> 由 SocketsHttpHandler 原生支持）。
    /// </summary>
    /// <returns>null 表示不走代理。</returns>
    public static IWebProxy? CreateProxy(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return null;
        }

        string text = proxyUrl.Trim();

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)
            || text.Equals("off", StringComparison.OrdinalIgnoreCase)
            || text == "-")
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            // 裸 host:port 按 socks5 处理（本项目的默认用法就是 socks5）
            text = "socks5://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ? new WebProxy(uri) : null;
    }

    /// <summary>默认的 <see cref="HttpClient"/>：UA / Referer / 解压 / 30 秒超时 / 可选代理。</summary>
    /// <param name="proxyUrl">覆盖默认代理（null 用 <see cref="DefaultProxyUrl"/>）。</param>
    public static HttpClient CreateHttpClient(string? proxyUrl = null)
    {
        string? effective = proxyUrl ?? _defaultProxyUrl;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (effective is not null)
        {
            IWebProxy? proxy = CreateProxy(effective);
            handler.Proxy = proxy;
            handler.UseProxy = proxy is not null;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Referrer = new Uri(SiteBase + "/");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json,image/*,*/*;q=0.8");
        return client;
    }

    /// <summary>
    /// 解析用户输入（<c>flGyWc</c> / <c>/view/flGyWc</c> / 完整 URL）成 ID 与页面地址。
    /// </summary>
    public static bool TryParseTarget(string input, out string id, out string pageUrl, out string? error)
    {
        id = string.Empty;
        pageUrl = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "请输入作品 ID 或 shadertoy 链接。";
            return false;
        }

        string text = input.Trim();

        string candidate;
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // .../view/<id> 或 .../view/<id>/<slug>
            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int viewIndex = Array.FindIndex(segments, s => string.Equals(s, "view", StringComparison.OrdinalIgnoreCase));
            candidate = viewIndex >= 0 && viewIndex + 1 < segments.Length
                ? segments[viewIndex + 1]
                : segments.Length > 0 ? segments[^1] : string.Empty;
        }
        else
        {
            string trimmed = text.TrimStart('/');
            if (trimmed.StartsWith("view/", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["view/".Length..];
            }

            candidate = trimmed.Split('/', '?', '#')[0];
        }

        if (!IdPattern.IsMatch(candidate))
        {
            error = $"\"{input}\" 不是有效的 Shadertoy 作品 ID / 链接。";
            return false;
        }

        id = candidate;
        pageUrl = $"{SiteBase}/view/{candidate}";
        return true;
    }

    /// <summary>
    /// 下载一个作品。
    /// </summary>
    /// <param name="idOrUrl">作品 ID 或页面链接。</param>
    /// <param name="options">下载开关。</param>
    /// <param name="progress">进度文本（在后台线程调用，UI 只应记字符串）。</param>
    /// <param name="cancellationToken">取消（UI 的"取消"按钮）。</param>
    public async Task<DownloadResult> DownloadAsync(
        string idOrUrl,
        DownloadOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DownloadOptions();

        if (!TryParseTarget(idOrUrl, out string id, out string pageUrl, out string? targetError))
        {
            return Failure(idOrUrl, string.Empty, string.Empty, targetError ?? "无效的目标。");
        }

        progress?.Report($"获取页面 {pageUrl}");

        string html;
        try
        {
            byte[] pageBytes = await GetBytesAsync(pageUrl, options, cancellationToken).ConfigureAwait(false);
            html = Utf8NoBom.GetString(pageBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException ex)
        {
            return Failure(id, pageUrl, string.Empty, "页面下载失败：" + ex.Message);
        }

        if (!ShaderToyPage.TryExtractProjectJson(html, out string json, out string? extractError))
        {
            string? dump = TryDumpFailed(id, html, "html");
            return Failure(id, pageUrl, string.Empty,
                extractError + (dump is null ? string.Empty : $"（原始页面已存档：{dump}）"));
        }

        if (!ShaderToyJsonImporter.TryParse(json, out ShaderToyProject? project, out string? parseError))
        {
            string? dump = TryDumpFailed(id, json, "json");
            return Failure(id, pageUrl, string.Empty,
                "工程 JSON 解析失败：" + parseError + (dump is null ? string.Empty : $"（原始 JSON 已存档：{dump}）"));
        }

        var warnings = new List<string>();
        List<AssetRequest> requests = PlanAssets(project!, warnings);

        progress?.Report($"写入 {id} …");

        (string directory, bool isCopy) = PrepareDirectory(id, options.Overwrite);
        if (isCopy)
        {
            warnings.Add($"同名作品已存在，已另存为 {Path.GetFileName(directory)}。");
        }

        var downloaded = new ConcurrentBag<string>();
        var missing = new ConcurrentBag<MissingResource>();
        var rewriteMap = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var completed = 0;
        int total = requests.Count;

        await Parallel.ForEachAsync(
            requests,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency),
                CancellationToken = cancellationToken,
            },
            async (request, token) =>
            {
                string fullPath = Path.Combine(
                    directory,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (options.SkipExistingAssets && File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
                {
                    downloaded.Add(request.RelativePath);
                    if (request.Rewrite)
                    {
                        rewriteMap[request.Literal] = request.RelativePath;
                    }

                    return;
                }

                progress?.Report($"[{Interlocked.Increment(ref completed)}/{total}] {request.RelativePath}");

                try
                {
                    byte[] bytes = await GetBytesAsync(request.Url, options, token).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await File.WriteAllBytesAsync(fullPath, bytes, token).ConfigureAwait(false);

                    downloaded.Add(request.RelativePath);
                    if (request.Rewrite)
                    {
                        rewriteMap[request.Literal] = request.RelativePath;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is DownloadException or IOException or UnauthorizedAccessException)
                {
                    // 单个资源失败只记录，不中断（P8 验收里专门有一条 404 的用例）
                    missing.Add(new MissingResource(request.Url, ex.Message));
                }
            }).ConfigureAwait(false);

        string savedJson = JsonTextRewriter.ReplaceStringValues(json, rewriteMap, out _);

        try
        {
            // raw.json：上游格式，只改写资源路径，其余原样（S8-3）；文件名与 ShaderToySnap 一致
            await File.WriteAllTextAsync(
                Path.Combine(directory, ShaderFiles.RawProject),
                savedJson,
                Utf8NoBom,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(id, pageUrl, directory, $"写入 {ShaderFiles.RawProject} 失败：" + ex.Message);
        }

        ShaderMeta? previous = ShaderMeta.TryLoad(directory);
        var meta = new ShaderMeta
        {
            Id = string.IsNullOrEmpty(project!.Metadata.Id) ? id : project.Metadata.Id,
            Title = project.Metadata.Name,
            Author = project.Metadata.Author,
            SourceUrl = pageUrl,
            DownloadedAt = options.Now,
            LastPlayed = previous?.LastPlayed,
            Favorite = previous?.Favorite ?? false,
        };

        foreach (string tag in project.Metadata.Tags)
        {
            if (!meta.Tags.Contains(tag, StringComparer.Ordinal))
            {
                meta.Tags.Add(tag);
            }
        }

        foreach (MissingResource item in missing)
        {
            meta.MissingResources.Add(item);
        }

        if (missing.Count > 0)
        {
            warnings.Add($"缺 {missing.Count} 个资源（作品仍可加载，缺的通道会用黑纹理）。");
        }

        progress?.Report("保存缩略图 …");
        if (options.DownloadThumbnail)
        {
            await DownloadThumbnailAsync(html, id, directory, options, warnings, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            meta.Save(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("meta.json 写入失败：" + ex.Message);
        }

        return new DownloadResult(
            true,
            id,
            directory,
            project.Metadata.Name,
            project.Metadata.Author,
            pageUrl,
            null,
            downloaded.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            missing.OrderBy(m => m.Url, StringComparer.Ordinal).ToList(),
            warnings);
    }

    /// <summary>
    /// 按工程里的 <c>inputs[]</c> 生成下载计划。
    /// </summary>
    /// <remarks>
    /// 同一 URL 只下一次（S8-2）。cubemap 的 6 面按站点命名约定 <c>_0.._5</c> 推导：
    /// 只把第 0 面写回 JSON，其余 5 面按原文件名落盘（将来加载时按"同一 stem + 后缀"取）。
    /// </remarks>
    private static List<AssetRequest> PlanAssets(ShaderToyProject project, List<string> warnings)
    {
        var requests = new List<AssetRequest>();
        var byUrl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (ShaderPassInput input in project.Passes.SelectMany(p => p.Inputs))
        {
            if (input.Type is not (ShaderInputType.Texture or ShaderInputType.Cubemap or ShaderInputType.Volume))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(input.Path))
            {
                continue;
            }

            if (!AssetHttp.TryToAbsoluteUrl(input.Path!, out string? url))
            {
                // 已经是本地相对路径（手工整理过的工程）：不下载
                continue;
            }

            if (!byUrl.Add(url))
            {
                continue;
            }

            if (input.Type == ShaderInputType.Volume)
            {
                // volume 资源的下载地址没有可靠约定（id 只是资源标识），先不猜
                warnings.Add($"volume 资源 {input.Id ?? input.Path} 暂不支持下载，仍用占位纹理。");
                continue;
            }

            string relative = AllocateRelativePath(url, byName);
            requests.Add(new AssetRequest(input.Path!, url, relative, Rewrite: true));

            if (input.Type != ShaderInputType.Cubemap)
            {
                continue;
            }

            if (!TryDeriveCubeFaces(url, out IReadOnlyList<string> faces))
            {
                warnings.Add($"cubemap {Path.GetFileName(url)} 不是 _0 命名，无法推导其余 5 面（只保存了单张）。");
                continue;
            }

            foreach (string faceUrl in faces)
            {
                if (!byUrl.Add(faceUrl))
                {
                    continue;
                }

                requests.Add(new AssetRequest(
                    Literal: string.Empty,
                    faceUrl,
                    AllocateRelativePath(faceUrl, byName),
                    Rewrite: false));
            }
        }

        return requests;
    }

    /// <summary>cubemap 的其余 5 面：<c>xxx_0.png</c> → <c>xxx_1.png</c> …（站点命名约定）。</summary>
    private static bool TryDeriveCubeFaces(string face0Url, out IReadOnlyList<string> faces)
    {
        faces = [];

        if (!Uri.TryCreate(face0Url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string fileName = Path.GetFileName(uri.AbsolutePath);
        int dot = fileName.LastIndexOf('.');
        string stem = dot > 0 ? fileName[..dot] : fileName;
        string extension = dot > 0 ? fileName[dot..] : string.Empty;

        if (!stem.EndsWith("_0", StringComparison.Ordinal))
        {
            return false;
        }

        string prefix = stem[..^2];
        string directoryPath = uri.AbsolutePath[..(uri.AbsolutePath.Length - fileName.Length)];

        var result = new List<string>(5);
        for (var i = 1; i <= 5; i++)
        {
            result.Add($"{uri.Scheme}://{uri.Authority}{directoryPath}{prefix}_{i}{extension}");
        }

        faces = result;
        return true;
    }

    /// <summary>资源在作品目录里的相对路径（<c>textures/&lt;文件名&gt;</c>，重名加序号）。</summary>
    private static string AllocateRelativePath(string url, Dictionary<string, string> byName)
    {
        string fileName = Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath));
        fileName = AssetHttp.SanitizeFileName(fileName);

        if (fileName.Length == 0)
        {
            fileName = "asset.bin";
        }

        string candidate = fileName;
        for (var index = 2; byName.TryGetValue(candidate, out string? owner); index++)
        {
            if (string.Equals(owner, url, StringComparison.OrdinalIgnoreCase))
            {
                return $"{TextureFolder}/{candidate}";
            }

            int dot = fileName.LastIndexOf('.');
            candidate = dot > 0
                ? $"{fileName[..dot]}-{index}{fileName[dot..]}"
                : $"{fileName}-{index}";
        }

        byName[candidate] = url;
        return $"{TextureFolder}/{candidate}";
    }

    /// <summary>确定落盘目录：覆盖模式用 <c>&lt;id&gt;</c>，否则找第一个空的 <c>&lt;id&gt;-N</c>。</summary>
    private (string Directory, bool IsCopy) PrepareDirectory(string id, bool overwrite)
    {
        Directory.CreateDirectory(_libraryRoot);

        string baseName = id.ToLowerInvariant();
        string directory = Path.Combine(_libraryRoot, baseName);

        if (overwrite || !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return (directory, false);
        }

        for (var index = 2; ; index++)
        {
            string candidate = Path.Combine(_libraryRoot, $"{baseName}-{index}");
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return (candidate, true);
            }
        }
    }

    private async Task DownloadThumbnailAsync(
        string html,
        string id,
        string directory,
        DownloadOptions options,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        string url = ShaderToyPage.TryExtractThumbnailUrl(html)
                     ?? $"{SiteBase}/media/shaders/{id}.jpg";

        try
        {
            byte[] bytes = await GetBytesAsync(url, options, cancellationToken).ConfigureAwait(false);

            string extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(extension) || extension.Length > 5)
            {
                extension = ".jpg";
            }

            // 换过扩展名时清掉旧的，免得扫描优先用了过期图
            foreach (string name in (string[])["thumbnail.png", "thumbnail.jpg", "preview.png"])
            {
                string stale = Path.Combine(directory, name);
                if (!string.Equals(name, "thumbnail" + extension, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }

            await File.WriteAllBytesAsync(
                Path.Combine(directory, "thumbnail" + extension),
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DownloadException or IOException or UnauthorizedAccessException)
        {
            warnings.Add("缩略图下载失败：" + ex.Message);
        }
    }

    /// <summary>带 UA/Referer 的 GET；429 与 5xx 退避重试，其余 4xx 直接失败。</summary>
    private Task<byte[]> GetBytesAsync(string url, DownloadOptions options, CancellationToken cancellationToken) =>
        AssetHttp.GetBytesAsync(_http, url, options, cancellationToken);

    /// <summary>解析失败时把原文存档，方便事后比对（S8-1）。</summary>
    private string? TryDumpFailed(string id, string content, string extension)
    {
        try
        {
            string directory = Path.Combine(_libraryRoot, "_failed");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory,
                $"{id.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd_HHmmss_fff}.{extension}");

            File.WriteAllText(path, content, Utf8NoBom);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static DownloadResult Failure(string id, string pageUrl, string title, string error) =>
        new(false, id, null, title, string.Empty, pageUrl, error, [], [], []);

    /// <summary>一条资源的下载计划。</summary>
    /// <param name="Literal">JSON 原文里的字符串值（改写的 key；不改写时为空）。</param>
    /// <param name="Url">下载地址。</param>
    /// <param name="RelativePath">落盘相对路径（相对作品目录）。</param>
    /// <param name="Rewrite">是否把 JSON 里的该字符串改成 <paramref name="RelativePath"/>。</param>
    private sealed record AssetRequest(string Literal, string Url, string RelativePath, bool Rewrite);
}
