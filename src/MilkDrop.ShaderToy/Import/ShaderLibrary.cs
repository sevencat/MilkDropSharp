using System.Text.Json;
using MilkDrop.ShaderToy.Pass;

namespace MilkDrop.ShaderToy.Import;

/// <summary>本地库里的一个作品。</summary>
/// <param name="Id">作品 ID（目录名或文件名，不含扩展名）。</param>
/// <param name="Title">标题（优先 <c>meta.json</c>，其次 <c>project.json</c> 的 <c>info.name</c>）。</param>
/// <param name="Author">作者（同上）。</param>
/// <param name="ProjectPath"><c>project.json</c> 的完整路径。</param>
/// <param name="Directory">作品所在目录（图片相对路径的基准）。</param>
/// <param name="ThumbnailPath">缩略图路径（没有就是 null）。</param>
/// <param name="Bytes"><c>project.json</c> 的字节数（UI 里显示"工程大小"用）。</param>
/// <param name="LastWrite"><c>project.json</c> 的修改时间（没有播放记录时用它排序）。</param>
/// <param name="LastPlayed">最近播放时间（<c>meta.json</c>，P8）。</param>
/// <param name="Favorite">是否收藏（<c>meta.json</c>）。</param>
/// <param name="MissingResources">上次下载没拿到的资源个数（<c>meta.json</c>）。</param>
public sealed record ShaderLibraryEntry(
    string Id,
    string Title,
    string Author,
    string ProjectPath,
    string Directory,
    string? ThumbnailPath,
    long Bytes,
    DateTime LastWrite,
    DateTimeOffset? LastPlayed = null,
    bool Favorite = false,
    int MissingResources = 0)
{
    /// <summary>列表里显示的一行文字。</summary>
    public string DisplayName => string.IsNullOrEmpty(Title) ? Id : $"{Title}  ({Id})";

    /// <summary>列表排序键：最近播放优先，没有记录就看文件时间。</summary>
    public DateTime SortTime => LastPlayed?.LocalDateTime ?? LastWrite;

    /// <summary>是否是"一个作品一个目录"的标准布局（平铺布局不能整目录删除）。</summary>
    public bool HasOwnDirectory =>
        string.Equals(Path.GetFileName(ProjectPath), "project.json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 本地作品库：扫一个目录，产出可点选的作品列表。
/// </summary>
/// <remarks>
/// <para>
/// 识别规则（两条都支持，见 <c>shaderToyPlan.md</c> P8 的目录布局）：
/// 1. 子目录里有 <c>project.json</c> —— 下载后的标准布局，ID 取目录名；
/// 2. 根目录下直接放 <c>*.json</c> —— 手工丢进去或仓库里的 <c>res/shadertoy/stpreset</c>，ID 取文件名。
/// </para>
/// <para>
/// 只读 <c>meta.json</c> + 少量元信息；**没有** <c>meta.json</c> 时才退化成完整解析 <c>project.json</c>
/// （导入器能容忍上游那堆坏转义，比另写一个宽松解析器划算）。
/// 全程不写盘：目录是事实来源，扫描结果只活在内存里。
/// </para>
/// </remarks>
public sealed class ShaderLibrary
{
    private ShaderLibrary(string root)
    {
        Root = root;
    }

    /// <summary>库根目录（可能不存在）。</summary>
    public string Root { get; }

    /// <summary>扫到的作品，按 <see cref="ShaderLibraryEntry.LastWrite"/> 倒序（最近的在前）。</summary>
    public List<ShaderLibraryEntry> Entries { get; } = [];

    /// <summary>扫描过程中的提示（目录不存在、JSON 读不出来……）。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>扫描一个目录（不存在时返回空列表 + 一条提示，不抛）。</summary>
    public static ShaderLibrary Scan(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var library = new ShaderLibrary(root);

        if (!Directory.Exists(root))
        {
            library.Warnings.Add($"库目录不存在：{root}");
            return library;
        }

        try
        {
            // 1) 标准布局：<root>/<id>/project.json
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string projectPath = Path.Combine(directory, "project.json");
                if (File.Exists(projectPath))
                {
                    library.Add(projectPath, Path.GetFileName(directory), directory);
                }
            }

            // 2) 平铺布局：<root>/*.json（跳过 meta.json）
            foreach (string file in Directory.EnumerateFiles(root, "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), "meta.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                library.Add(file, Path.GetFileNameWithoutExtension(file), root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            library.Warnings.Add($"扫描 {root} 失败：{ex.Message}");
        }

        library.Entries.Sort((a, b) => b.SortTime.CompareTo(a.SortTime));
        return library;
    }

    /// <summary>按 ID / 目录名 / 文件名找作品（大小写不敏感，命令行 <c>--shader &lt;id&gt;</c> 用）。</summary>
    public ShaderLibraryEntry? Find(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        string needle = idOrName.Trim();

        return Entries.FirstOrDefault(entry =>
                   string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Path.GetFileName(entry.Directory), needle, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(Path.GetFileNameWithoutExtension(entry.ProjectPath), needle, StringComparison.OrdinalIgnoreCase))
               ?? Entries.FirstOrDefault(entry =>
                   entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按标题 / 作者 / ID 过滤（空串返回全部）。</summary>
    public IEnumerable<ShaderLibraryEntry> Search(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Entries;
        }

        string needle = text.Trim();

        return Entries.Where(entry =>
            entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Author.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private void Add(string projectPath, string id, string directory)
    {
        try
        {
            var info = new FileInfo(projectPath);

            string title = string.Empty;
            string author = string.Empty;
            DateTimeOffset? lastPlayed = null;
            bool favorite = false;
            var missing = 0;

            if (TryReadMeta(directory, out MetaInfo meta))
            {
                // 下载后的标准布局：只读 meta.json，不解析代码（列表要快）
                id = string.IsNullOrEmpty(meta.Id) ? id : meta.Id!;
                title = meta.Title ?? string.Empty;
                author = meta.Author ?? string.Empty;
                lastPlayed = meta.LastPlayed;
                favorite = meta.Favorite;
                missing = meta.Missing;
            }
            else
            {
                // 没有 meta.json：完整解析一次（上游格式），取 info 节
                try
                {
                    ShaderToyProject project = ShaderToyJsonImporter.LoadFile(projectPath);
                    title = project.Metadata.Name;
                    author = project.Metadata.Author;

                    if (string.IsNullOrEmpty(id))
                    {
                        id = project.Metadata.Id;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or ShaderToyImportException
                                               or InvalidOperationException or ArgumentException)
                {
                    Warnings.Add($"{Path.GetFileName(projectPath)} 解析失败，只能按文件名列出：{ex.Message}");
                }
            }

            Entries.Add(new ShaderLibraryEntry(
                id,
                title,
                author,
                info.FullName,
                directory,
                FindThumbnail(directory),
                info.Length,
                info.LastWriteTime,
                lastPlayed,
                favorite,
                missing));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warnings.Add($"读取 {projectPath} 失败：{ex.Message}");
        }
    }

    /// <summary>读 <c>meta.json</c>（P8 下载后的标准布局；缺了/坏了就走解析回退）。</summary>
    private static bool TryReadMeta(string directory, out MetaInfo info)
    {
        info = default;

        string metaPath = ShaderMeta.PathFor(directory);
        if (!File.Exists(metaPath))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metaPath));
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            DateTimeOffset? lastPlayed = null;
            if (ReadString(root, "lastPlayed") is { } played
                && DateTimeOffset.TryParse(played, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                lastPlayed = parsed;
            }

            var missing = 0;
            if (root.TryGetProperty("missingResources", out JsonElement missingArray)
                && missingArray.ValueKind == JsonValueKind.Array)
            {
                // 只数"真不在本地"的：手工把图放进 textures/ 之后，meta 里那条旧记录不该继续报缺
                foreach (JsonElement item in missingArray.EnumerateArray())
                {
                    string? url = item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : ReadString(item, "url");

                    if (!string.IsNullOrEmpty(url) && !AssetExistsLocally(directory, url))
                    {
                        missing++;
                    }
                }
            }

            info = new MetaInfo(
                ReadString(root, "id"),
                ReadString(root, "title") ?? ReadString(root, "name"),
                ReadString(root, "author"),
                lastPlayed,
                root.TryGetProperty("favorite", out JsonElement favorite) && favorite.ValueKind == JsonValueKind.True,
                missing);

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // meta.json 坏了不影响作品本身
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// meta.json 里记的缺失资源是不是其实已经在本地（<c>textures/</c> 或作品目录下）。
    /// </summary>
    /// <remarks>
    /// 与 <c>ShaderToyPipeline.ResolveTexturePath</c> 的兜底同一套规则：只按文件名找，
    /// 这样"站点路径 + 手工放进 textures/ 的图"不会被误报成缺失。
    /// </remarks>
    private static bool AssetExistsLocally(string directory, string url)
    {
        string fileName = Path.GetFileName(url.Replace('/', Path.DirectorySeparatorChar));

        int query = fileName.IndexOf('?');
        if (query >= 0)
        {
            fileName = fileName[..query];
        }

        if (fileName.Length == 0)
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, "textures", fileName))
               || File.Exists(Path.Combine(directory, fileName));
    }

    /// <summary>meta.json 里列表需要的字段（其余留给 <see cref="ShaderMeta"/>）。</summary>
    private readonly record struct MetaInfo(
        string? Id,
        string? Title,
        string? Author,
        DateTimeOffset? LastPlayed,
        bool Favorite,
        int Missing);

    private static string? FindThumbnail(string directory)
    {
        foreach (string name in (string[])["thumbnail.png", "thumbnail.jpg", "preview.png"])
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
