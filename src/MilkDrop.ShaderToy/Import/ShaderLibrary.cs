namespace MilkDrop.ShaderToy.Import;

/// <summary>作品列表里的一项（只含"名字"级信息，细节等打开作品时再查）。</summary>
/// <param name="Id">Shadertoy 短 ID，同时是列表去重/查找的键。</param>
/// <param name="Title">标题（<c>shaders.Name</c>）。</param>
/// <param name="Author">作者（<c>shaders.UserName</c>）。</param>
/// <param name="DatabasePath">数据来源：<c>shadertoy.db</c> 的完整路径。</param>
/// <param name="CapturedAt">抓取时间（列表排序用）。</param>
public sealed record ShaderLibraryEntry(
    string Id,
    string Title,
    string Author,
    string DatabasePath,
    DateTime CapturedAt)
{
    /// <summary>列表里显示的一行文字（标题，没有标题就用 ID；左栏很窄，ID/作者另有位置）。</summary>
    public string DisplayName => string.IsNullOrEmpty(Title) ? Id : Title;

    /// <summary>列表排序键：抓取时间。</summary>
    public DateTime SortTime => CapturedAt;
}

/// <summary>
/// 本地作品库：从数据根目录下的 <c>shadertoy.db</c> 读作品列表。
/// </summary>
/// <remarks>
/// <para>
/// 目录约定见 <see cref="ShaderFiles"/>：作品数据（工程 JSON、pass 代码、通道绑定）**全部在数据库里**，
/// 磁盘上只有 <c>media/</c> 下的图片资源。所以这里不扫描任何 <c>shaders/</c> 目录，也不看 <c>meta.json</c>。
/// </para>
/// <para>
/// **列表只读"名字那几列"**（<c>ShaderId</c> / <c>Name</c> / <c>UserName</c> / <c>CapturedAt</c>）：
/// 程序一启动就要把列表摆出来，不能因为去数 pass、数缺图、读标签而变慢。pass 数、缺图、标签、代码
/// 这些细节都等"双击播放"时再由 <see cref="ShaderDatabase.TryReadDetails"/> /
/// <see cref="ShaderDatabase.TryLoadProject"/> 去取。
/// </para>
/// </remarks>
public sealed class ShaderLibrary
{
    private ShaderLibrary(string dataRoot, string databasePath)
    {
        DataRoot = dataRoot;
        DatabasePath = databasePath;
    }

    /// <summary>数据根目录（图片在 <c>&lt;数据根&gt;/media</c> 下）。</summary>
    public string DataRoot { get; }

    /// <summary>数据库文件路径（可能不存在）。</summary>
    public string DatabasePath { get; }

    /// <summary>扫到的作品，按抓取时间倒序。</summary>
    public List<ShaderLibraryEntry> Entries { get; } = [];

    /// <summary>扫描过程中的提示（库不存在、打不开数据库……）。</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>扫描数据根目录（不存在/没有数据库时返回空列表 + 一条提示，不抛）。</summary>
    public static ShaderLibrary Scan(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string root = Path.GetFullPath(dataRoot);
        string databasePath = Path.Combine(root, ShaderDatabase.FileName);
        var library = new ShaderLibrary(root, databasePath);

        if (!File.Exists(databasePath))
        {
            library.Warnings.Add(
                $"数据库不存在：{databasePath}（先用 ShaderToySnap 抓几个作品，或用 --data 指到它的 output 目录）");
            return library;
        }

        try
        {
            using var database = new ShaderDatabase(databasePath);

            if (!database.IsOpen)
            {
                library.Warnings.Add(database.Error ?? $"打不开数据库：{databasePath}");
                return library;
            }

            foreach (ShaderDatabaseRecord record in database.ReadShaders())
            {
                library.Entries.Add(new ShaderLibraryEntry(
                    record.Id,
                    record.Title,
                    record.Author,
                    databasePath,
                    record.CapturedAt));
            }

            if (database.Error is { Length: > 0 } error)
            {
                library.Warnings.Add(error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            library.Warnings.Add($"读取 {databasePath} 失败：{ex.Message}");
        }

        library.Entries.Sort((a, b) => b.SortTime.CompareTo(a.SortTime));
        return library;
    }

    /// <summary>
    /// 读某个作品的细节（pass 数 / 缺图数 / 标签）：打开作品时才调，列表不调。
    /// </summary>
    /// <returns>作品不存在或库打不开时返回 null。</returns>
    public ShaderDatabaseDetails? TryReadDetails(ShaderLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            using var database = new ShaderDatabase(entry.DatabasePath);
            return database.IsOpen ? database.TryReadDetails(entry.Id) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>按 ID 找作品（大小写不敏感，命令行 <c>作品ID</c> 用）。</summary>
    public ShaderLibraryEntry? Find(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        string needle = idOrName.Trim();

        return Entries.FirstOrDefault(entry => string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase))
               ?? Entries.FirstOrDefault(entry => entry.Id.Contains(needle, StringComparison.OrdinalIgnoreCase));
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
}
