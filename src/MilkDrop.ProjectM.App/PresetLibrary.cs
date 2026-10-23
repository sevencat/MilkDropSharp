namespace MilkDrop.ProjectM.App;

/// <summary>
/// 预设清单：扫描目录、解析文本播放列表、按关键词过滤。
/// </summary>
/// <remarks>
/// 只做"有哪些文件"这一层；播放顺序/历史由 <c>MilkDrop.ProjectM.Playlist</c> 负责。
/// </remarks>
public sealed class PresetLibrary
{
    private readonly List<string> _all = [];

    /// <summary>当前清单（去重，保持插入顺序）。</summary>
    public IReadOnlyList<string> All => _all;

    /// <summary>递归扫描目录下的 <c>.milk</c>，按文件名排序后追加。</summary>
    /// <returns>新增的文件数。</returns>
    public int AddDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"预设目录不存在: {directory}");
            return 0;
        }

        var found = new List<string>();

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.milk", SearchOption.AllDirectories))
            {
                found.Add(file);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"扫描预设目录失败: {ex.Message}");
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (string file in found)
        {
            if (AddFile(file))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// 解析文本播放列表：每行一个路径（相对路径相对于列表文件所在目录），<c>#</c> 开头为注释。
    /// </summary>
    /// <returns>新增的文件数。</returns>
    public int AddPlaylistFile(string playlistPath)
    {
        if (!File.Exists(playlistPath))
        {
            Console.WriteLine($"播放列表不存在: {playlistPath}");
            return 0;
        }

        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? ".";

        var added = 0;
        foreach (string rawLine in File.ReadLines(playlistPath))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string fullPath = Path.IsPathRooted(line) ? line : Path.Combine(baseDirectory, line);

            if (AddFile(fullPath))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>添加单个预设文件（去重）。</summary>
    public bool AddFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return false;
        }

        if (_all.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _all.Add(fullPath);
        return true;
    }

    /// <summary>按"文件名包含关键词"过滤（忽略大小写）；关键词为空时返回全部。</summary>
    public IReadOnlyList<string> Filter(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return _all;
        }

        return _all
            .Where(path => Path.GetFileName(path).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
