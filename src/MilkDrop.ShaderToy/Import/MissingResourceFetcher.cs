using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Import;

/// <summary>"补齐缺失资源"的结果。</summary>
/// <param name="Success">是否跑完（单个资源失败不算整体失败）。</param>
/// <param name="Directory">作品目录。</param>
/// <param name="Downloaded">下载成功的资源个数。</param>
/// <param name="Failed">失败的资源个数。</param>
/// <param name="StillMissing">仍然缺的资源（URL + 原因）。</param>
/// <param name="Error">整体失败原因（目录/JSON 有问题时）。</param>
public sealed record FetchResult(
    bool Success,
    string Directory,
    int Downloaded,
    int Failed,
    IReadOnlyList<MissingResource> StillMissing,
    string? Error)
{
    /// <summary>一句话摘要。</summary>
    public string Describe()
    {
        if (!Success)
        {
            return "补齐资源失败：" + Error;
        }

        if (Downloaded == 0 && Failed == 0)
        {
            return "没有需要补齐的资源（JSON 里都是本地相对路径）。";
        }

        return $"补齐资源：成功 {Downloaded} 个" + (Failed > 0 ? $"，仍缺 {Failed} 个" : string.Empty);
    }
}

/// <summary>
/// 把 <c>project.json</c> 里还指向站点（<c>/media/a/xxx.png</c>、<c>http(s)://</c>）的资源抓下来，
/// 存进作品目录的 <c>textures/</c> 并把 JSON 改写成相对路径。
/// </summary>
/// <remarks>
/// <para>
/// 典型场景：本地导入的 json 里写着 Shadertoy 的站点路径，导入时拿不到，只能记进
/// <c>meta.json</c> 的 <c>missingResources</c>（S8-10）。这里按同一个约定补下载：
/// 站点路径拼上 <see cref="ShaderToyDownloader.SiteBase"/>；cubemap 顺带 <c>_1.._5</c> 六面。
/// </para>
/// <para>
/// 已经存在且非空的文件跳过（增量）；单个失败不中断，剩下的继续；结束后同步 meta 的缺失列表，
/// 这样 UI 上的"缺 N 资源"会跟着变。
/// </para>
/// <para>
/// 走的是和下载整件作品同一套 HttpClient（含可配置的 SOCKS5 代理），见
/// <see cref="ShaderToyDownloader.CreateHttpClient(string?)"/>。
/// </para>
/// </remarks>
public sealed class MissingResourceFetcher
{
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly HttpClient _http;

    /// <param name="http">可注入的客户端（测试用假 handler / 带代理的客户端）。</param>
    public MissingResourceFetcher(HttpClient? http = null)
    {
        _http = http ?? ShaderToyDownloader.CreateHttpClient();
    }

    /// <summary>
    /// 补齐一个作品的缺失资源。
    /// </summary>
    /// <param name="projectPathOrDirectory"><c>project.json</c> 或作品目录。</param>
    /// <param name="options">重试/并发等开关（<c>Now</c> 会被忽略）。</param>
    /// <param name="progress">进度文本（后台线程调用）。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<FetchResult> FetchAsync(
        string projectPathOrDirectory,
        DownloadOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DownloadOptions();

        if (!TryResolveDirectory(projectPathOrDirectory, out string directory, out string? resolveError))
        {
            return Failure(directory, resolveError ?? "路径无效。");
        }

        string projectPath = Path.Combine(directory, "project.json");
        if (!File.Exists(projectPath))
        {
            return Failure(directory, "目录里没有 project.json。");
        }

        string rawJson;
        ShaderToyProject project;
        try
        {
            rawJson = ShaderToyJsonImporter.ReadTextTolerant(projectPath);
            project = ShaderToyJsonImporter.Parse(rawJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or ShaderToyImportException
                                       or InvalidOperationException or ArgumentException)
        {
            return Failure(directory, "project.json 读不出来：" + ex.Message);
        }

        List<FetchRequest> requests = PlanRequests(project);

        if (requests.Count == 0)
        {
            SyncMeta(directory, Array.Empty<string>(), Array.Empty<MissingResource>());
            return new FetchResult(true, directory, 0, 0, [], null);
        }

        progress?.Report($"补齐 {requests.Count} 个资源 …");

        var succeeded = new List<string>();
        var failed = new List<MissingResource>();
        var rewrite = new Dictionary<string, string>(StringComparer.Ordinal);
        var lockObject = new object();
        var completed = 0;

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
                    lock (lockObject)
                    {
                        succeeded.Add(request.Rewrite ? request.Literal : request.Url);
                        if (request.Rewrite)
                        {
                            rewrite[request.Literal] = request.RelativePath;
                        }
                    }

                    return;
                }

                progress?.Report($"[{Interlocked.Increment(ref completed)}/{requests.Count}] {request.RelativePath}");

                try
                {
                    byte[] bytes = await AssetHttp.GetBytesAsync(_http, request.Url, options, token).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await File.WriteAllBytesAsync(fullPath, bytes, token).ConfigureAwait(false);

                    lock (lockObject)
                    {
                        succeeded.Add(request.Rewrite ? request.Literal : request.Url);
                        if (request.Rewrite)
                        {
                            rewrite[request.Literal] = request.RelativePath;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is DownloadException or IOException or UnauthorizedAccessException)
                {
                    lock (lockObject)
                    {
                        failed.Add(new MissingResource(request.Url, ex.Message));
                    }
                }
            }).ConfigureAwait(false);

        if (rewrite.Count > 0)
        {
            string savedJson = JsonTextRewriter.ReplaceStringValues(rawJson, rewrite, out _);

            try
            {
                await File.WriteAllTextAsync(projectPath, savedJson, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failure(directory, "写 project.json 失败：" + ex.Message);
            }
        }

        SyncMeta(directory, succeeded, failed);

        return new FetchResult(
            true,
            directory,
            succeeded.Count,
            failed.Count,
            failed.OrderBy(f => f.Url, StringComparer.Ordinal).ToList(),
            null);
    }

    /// <summary>扫 <c>inputs[]</c>，挑出还指向站点的资源（含 cubemap 的其余 5 面）。</summary>
    private static List<FetchRequest> PlanRequests(ShaderToyProject project)
    {
        var requests = new List<FetchRequest>();
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
                continue;   // 已经是本地相对路径
            }

            if (!byUrl.Add(url))
            {
                continue;
            }

            requests.Add(new FetchRequest(input.Path!, url, AllocateRelative(url, byName), Rewrite: true));

            if (input.Type != ShaderInputType.Cubemap || !TryDeriveCubeFaces(url, out IReadOnlyList<string> faces))
            {
                continue;
            }

            foreach (string face in faces)
            {
                if (byUrl.Add(face))
                {
                    requests.Add(new FetchRequest(string.Empty, face, AllocateRelative(face, byName), Rewrite: false));
                }
            }
        }

        return requests;
    }

    /// <summary>cubemap 其余 5 面：<c>xxx_0.png</c> → <c>xxx_1.png</c> …</summary>
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

    /// <summary><c>textures/&lt;文件名&gt;</c>；不同 URL 撞名时加序号。</summary>
    private static string AllocateRelative(string url, Dictionary<string, string> byName)
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
                return $"{ShaderToyDownloader.TextureFolder}/{candidate}";
            }

            int dot = fileName.LastIndexOf('.');
            candidate = dot > 0
                ? $"{fileName[..dot]}-{index}{fileName[dot..]}"
                : $"{fileName}-{index}";
        }

        byName[candidate] = url;
        return $"{ShaderToyDownloader.TextureFolder}/{candidate}";
    }

    /// <summary>同步 meta.json 的缺失列表：下载成功的移除，新失败的补上。</summary>
    private static void SyncMeta(string directory, IReadOnlyList<string> succeeded, IReadOnlyList<MissingResource> failed)
    {
        try
        {
            ShaderMeta? meta = ShaderMeta.TryLoad(directory);
            if (meta is null)
            {
                return;
            }

            var done = new HashSet<string>(
                succeeded.Select(NormalizeUrl),
                StringComparer.OrdinalIgnoreCase);

            // 这次的新失败原因优先（比导入时那句"需要联网下载"更具体）
            var latest = new Dictionary<string, MissingResource>(StringComparer.OrdinalIgnoreCase);
            foreach (MissingResource item in failed)
            {
                string url = NormalizeUrl(item.Url);
                latest[url] = new MissingResource(url, item.Error);
            }

            // 重建列表：去掉已下到的，URL 统一成站点绝对形式，顺带去重
            var merged = new List<MissingResource>();

            foreach (MissingResource item in meta.MissingResources)
            {
                string url = NormalizeUrl(item.Url);
                if (done.Contains(url) || merged.Any(existing => Same(existing.Url, url)))
                {
                    continue;
                }

                merged.Add(latest.TryGetValue(url, out MissingResource? newer)
                    ? newer
                    : new MissingResource(url, item.Error));
            }

            foreach (MissingResource item in latest.Values)
            {
                if (!merged.Any(existing => Same(existing.Url, item.Url)))
                {
                    merged.Add(item);
                }
            }

            meta.MissingResources.Clear();
            meta.MissingResources.AddRange(merged);
            meta.Save(directory);

            static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // meta 写不动不影响资源本身
        }
    }

    /// <summary>记进 meta 的 URL 统一成站点绝对形式，方便和 <c>/media/...</c> 字面量互相比对。</summary>
    private static string NormalizeUrl(string url) =>
        url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
            ? ShaderToyDownloader.SiteBase + url
            : url;

    private static bool TryResolveDirectory(string input, out string directory, out string? error)
    {
        directory = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "没有指定作品。";
            return false;
        }

        try
        {
            string full = Path.GetFullPath(input);

            if (File.Exists(full))
            {
                directory = Path.GetDirectoryName(full) ?? full;
                return true;
            }

            if (Directory.Exists(full))
            {
                directory = full;
                return true;
            }

            error = "找不到：" + full;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "路径无效：" + ex.Message;
            return false;
        }
    }

    private static FetchResult Failure(string directory, string error) =>
        new(false, directory, 0, 0, [], error);

    /// <summary>一条待抓取的资源。</summary>
    private sealed record FetchRequest(string Literal, string Url, string RelativePath, bool Rewrite);
}
