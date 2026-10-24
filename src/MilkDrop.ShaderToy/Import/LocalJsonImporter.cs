using System.Text;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Import;

/// <summary>一次本地导入的结果（不抛异常，UI/命令行直接展示）。</summary>
/// <param name="Success">是否成功落盘。</param>
/// <param name="Id">作品 ID（= 源文件名，不含扩展名）。</param>
/// <param name="Directory">目标目录（失败时为 null）。</param>
/// <param name="Title">标题（优先 <c>info.name</c>，缺失时用 ID）。</param>
/// <param name="Author">作者。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Assets">随 JSON 一起拷进 <c>textures/</c> 的资源（相对路径）。</param>
/// <param name="Warnings">容忍项（同名另存、资源找不到……）。</param>
public sealed record ImportResult(
    bool Success,
    string Id,
    string? Directory,
    string Title,
    string Author,
    string? Error,
    IReadOnlyList<string> Assets,
    IReadOnlyList<string> Warnings)
{
    /// <summary>一句话摘要。</summary>
    public string Describe() => Success
        ? $"{Title} ({Id}) → {Directory}；随文件带过来的资源 {Assets.Count} 个"
          + (Warnings.Count > 0 ? $"，提示 {Warnings.Count} 条" : string.Empty)
        : $"导入失败（{Id}）：{Error}";
}

/// <summary>
/// 把本地 JSON **导入**成库里的一个作品（目录布局与下载一致）。
/// </summary>
/// <remarks>
/// <para>
/// 约定：**文件名（不含扩展名）就是作品 ID**，目录固定为 <c>&lt;库&gt;/&lt;id 小写&gt;/</c>。
/// 文件名里的非法字符在目录名上替换成 <c>_</c>，meta.json 里保留原文件名。
/// </para>
/// <para>
/// 资源处理：JSON 里引用的**本地文件**（绝对路径，或相对 JSON 所在目录的路径）会一起拷进
/// <c>textures/</c> 并把 JSON 里的路径改写成相对路径（<see cref="JsonTextRewriter"/>）；
/// cubemap 会把同目录下 <c>_1.._5</c> 的 6 面一起带上。站点路径（<c>/media/...</c>、<c>http(s)://</c>）
/// 没法离线拿到，记进 <c>missingResources</c>，作品仍可导入（缺的通道用黑纹理）。
/// </para>
/// <para>
/// <c>project.json</c> 保存**原文**（只改资源路径），与下载侧一致（S8-3）。
/// </para>
/// </remarks>
public sealed class LocalJsonImporter
{
    /// <summary>资源子目录（相对作品目录）。</summary>
    public const string TextureFolder = ShaderToyDownloader.TextureFolder;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _libraryRoot;

    /// <param name="libraryRoot">本地库根目录（不存在会创建）。</param>
    public LocalJsonImporter(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        _libraryRoot = Path.GetFullPath(libraryRoot);
    }

    /// <summary>本地库根目录（绝对路径）。</summary>
    public string LibraryRoot => _libraryRoot;

    /// <summary>
    /// 导入一个本地 JSON 文件。
    /// </summary>
    /// <param name="sourcePath">源 JSON 路径（文件名即 ID）。</param>
    /// <param name="overwrite">同名作品已存在时是否覆盖（false 表示另存为 <c>&lt;id&gt;-2</c>）。</param>
    public ImportResult Import(string sourcePath, bool overwrite = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Failure(string.Empty, "没有指定文件。");
        }

        string sourceFull;
        try
        {
            sourceFull = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(sourcePath, "路径无效：" + ex.Message);
        }

        if (!File.Exists(sourceFull))
        {
            return Failure(Path.GetFileNameWithoutExtension(sourceFull), "文件不存在：" + sourceFull);
        }

        string id = Path.GetFileNameWithoutExtension(sourceFull).Trim();
        if (id.Length == 0)
        {
            return Failure(string.Empty, "文件名是空的，无法作为作品 ID。");
        }

        ShaderToyProject project;
        string rawJson;
        try
        {
            rawJson = ShaderToyJsonImporter.ReadTextTolerant(sourceFull);
            project = ShaderToyJsonImporter.Parse(rawJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or ShaderToyImportException
                                       or InvalidOperationException or ArgumentException)
        {
            return Failure(id, "不是可识别的 Shadertoy 工程：" + ex.Message);
        }

        if (IsInsideLibrary(sourceFull))
        {
            return Failure(id, "这个文件已经在库里了，不用重复导入：" + sourceFull);
        }

        string sourceDirectory = Path.GetDirectoryName(sourceFull) ?? _libraryRoot;
        var warnings = new List<string>();
        var missing = new List<MissingResource>();

        List<LocalAsset> plan = PlanAssets(project, sourceDirectory, missing, warnings);

        (string directory, bool isCopy) = PrepareDirectory(id, overwrite);
        if (isCopy)
        {
            warnings.Add($"同名作品已存在，已另存为 {Path.GetFileName(directory)}。");
        }

        string projectPath = Path.Combine(directory, "project.json");

        var copied = new List<string>();
        var rewrite = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (LocalAsset asset in plan)
        {
            string target = Path.Combine(
                directory,
                asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(asset.SourcePath, target, overwrite: true);

                copied.Add(asset.RelativePath);
                if (asset.Rewrite)
                {
                    rewrite[asset.Literal] = asset.RelativePath;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missing.Add(new MissingResource(asset.SourcePath, "拷贝失败：" + ex.Message));
            }
        }

        string savedJson = JsonTextRewriter.ReplaceStringValues(rawJson, rewrite, out _);

        try
        {
            File.WriteAllText(projectPath, savedJson, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(id, "写入 project.json 失败：" + ex.Message);
        }

        ShaderMeta? previous = ShaderMeta.TryLoad(directory);
        var meta = new ShaderMeta
        {
            // 导入以文件名为准：id 就是用户给这个作品起的名字
            Id = id,
            Title = string.IsNullOrEmpty(project.Metadata.Name) ? id : project.Metadata.Name,
            Author = project.Metadata.Author,
            SourceUrl = string.Empty,
            DownloadedAt = previous?.DownloadedAt,
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

        try
        {
            meta.Save(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add("meta.json 写入失败：" + ex.Message);
        }

        return new ImportResult(
            true,
            id,
            directory,
            meta.Title,
            meta.Author,
            null,
            copied.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            warnings);
    }

    /// <summary>
    /// 找出 JSON 里引用的**本地**资源，映射成 <c>textures/&lt;文件名&gt;</c>。
    /// </summary>
    private static List<LocalAsset> PlanAssets(
        ShaderToyProject project,
        string sourceDirectory,
        List<MissingResource> missing,
        List<string> warnings)
    {
        var result = new List<LocalAsset>();
        var byRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            string literal = input.Path!;

            if (IsSitePath(literal))
            {
                missing.Add(new MissingResource(
                    literal,
                    "工程里引用的是站点路径，没有随文件提供（需要联网下载或手工放到 textures/）"));
                continue;
            }

            string? resolved = ResolveLocal(literal, sourceDirectory);
            if (resolved is null)
            {
                missing.Add(new MissingResource(literal, "找不到这个本地文件"));
                continue;
            }

            string relative = AllocateRelative(resolved, literal, byRelative);
            result.Add(new LocalAsset(literal, resolved, relative, Rewrite: true));

            if (input.Type != ShaderInputType.Cubemap)
            {
                continue;
            }

            // cubemap：把同目录下 _1.._5 的面一起带上（站点命名约定，与下载侧一致）
            if (TryDeriveCubeFaces(resolved, out IReadOnlyList<string> faces))
            {
                foreach (string face in faces)
                {
                    if (!File.Exists(face))
                    {
                        missing.Add(new MissingResource(face, "cubemap 面文件不存在"));
                        continue;
                    }

                    result.Add(new LocalAsset(
                        Literal: string.Empty,
                        face,
                        AllocateRelative(face, face, byRelative),
                        Rewrite: false));
                }
            }
            else
            {
                warnings.Add($"cubemap {Path.GetFileName(resolved)} 不是 _0 命名，无法推导其余 5 面（只带上了单张）。");
            }
        }

        return result;
    }

    /// <summary>站点绝对路径（<c>/media/...</c>）或 http(s) 地址：本地拿不到。</summary>
    private static bool IsSitePath(string path) =>
        (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
        || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 JSON 里的路径解析成本地文件：绝对路径 → 相对 JSON 目录 → 同目录同名文件。</summary>
    private static string? ResolveLocal(string path, string sourceDirectory)
    {
        try
        {
            if (Path.IsPathRooted(path) && File.Exists(path))
            {
                return Path.GetFullPath(path);
            }

            string relative = path.Replace('/', Path.DirectorySeparatorChar);
            string candidate = Path.Combine(sourceDirectory, relative);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            string byName = Path.Combine(sourceDirectory, Path.GetFileName(relative));
            if (File.Exists(byName))
            {
                return Path.GetFullPath(byName);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    /// <summary>cubemap 的其余 5 面：<c>xxx_0.png</c> → <c>xxx_1.png</c> …</summary>
    private static bool TryDeriveCubeFaces(string face0Path, out IReadOnlyList<string> faces)
    {
        faces = [];

        string fileName = Path.GetFileName(face0Path);
        int dot = fileName.LastIndexOf('.');
        string stem = dot > 0 ? fileName[..dot] : fileName;
        string extension = dot > 0 ? fileName[dot..] : string.Empty;

        if (!stem.EndsWith("_0", StringComparison.Ordinal))
        {
            return false;
        }

        string prefix = stem[..^2];
        string directory = Path.GetDirectoryName(face0Path) ?? string.Empty;

        var result = new List<string>(5);
        for (var i = 1; i <= 5; i++)
        {
            result.Add(Path.Combine(directory, $"{prefix}_{i}{extension}"));
        }

        faces = result;
        return true;
    }

    /// <summary>目标相对路径：<c>textures/&lt;文件名&gt;</c>；不同来源撞名时加序号。</summary>
    private static string AllocateRelative(string sourcePath, string literal, Dictionary<string, string> byRelative)
    {
        string fileName = SanitizeFileName(Path.GetFileName(sourcePath));
        if (fileName.Length == 0)
        {
            fileName = "asset.bin";
        }

        string candidate = fileName;
        for (var index = 2; byRelative.TryGetValue(candidate, out string? owner); index++)
        {
            if (string.Equals(owner, sourcePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(owner, literal, StringComparison.OrdinalIgnoreCase))
            {
                return $"{TextureFolder}/{candidate}";
            }

            int dot = fileName.LastIndexOf('.');
            candidate = dot > 0
                ? $"{fileName[..dot]}-{index}{fileName[dot..]}"
                : $"{fileName}-{index}";
        }

        byRelative[candidate] = sourcePath;
        return $"{TextureFolder}/{candidate}";
    }

    /// <summary>去掉 Windows 非法字符（目录名/资源名用）。</summary>
    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (char c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString().Trim().TrimEnd('.');
    }

    /// <summary>落盘目录：覆盖模式用 <c>&lt;id&gt;</c>（小写），否则找第一个空的 <c>&lt;id&gt;-N</c>。</summary>
    private (string Directory, bool IsCopy) PrepareDirectory(string id, bool overwrite)
    {
        Directory.CreateDirectory(_libraryRoot);

        string baseName = SanitizeFileName(id.ToLowerInvariant());
        if (baseName.Length == 0)
        {
            baseName = "shader";
        }

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

    /// <summary>源文件是否已经在库根下（含 <c>_failed</c> 之类的子目录）。</summary>
    private bool IsInsideLibrary(string path)
    {
        string root = _libraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;

        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static ImportResult Failure(string id, string error) =>
        new(false, id, null, string.Empty, string.Empty, error, [], []);

    /// <summary>一条本地资源的拷贝计划。</summary>
    /// <param name="Literal">JSON 原文里的字符串值（改写的 key；不改写时为空）。</param>
    /// <param name="SourcePath">源文件绝对路径。</param>
    /// <param name="RelativePath">目标相对路径。</param>
    /// <param name="Rewrite">是否改写 JSON 里的该字符串。</param>
    private sealed record LocalAsset(string Literal, string SourcePath, string RelativePath, bool Rewrite);
}
