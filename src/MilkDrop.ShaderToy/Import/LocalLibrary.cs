namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// 本地库的**写侧**：删除作品、统计占用、记录最近播放。
/// </summary>
/// <remarks>
/// 读侧在 <see cref="ShaderLibrary"/>（纯扫描、不写盘）。两者分开是为了让"列表"永远只读，
/// 写操作必须显式触发（用户点删除 / 真的播放了一个作品）。
/// </remarks>
public static class LocalLibrary
{
    /// <summary>统计一个作品目录的总占用（含纹理与缩略图）。</summary>
    public static long ComputeSize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件读不到就不算它
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return total;
        }

        return total;
    }

    /// <summary>
    /// 一个作品占用的空间：标准布局算整个目录，平铺布局只算那个 json
    /// （平铺时 <see cref="ShaderLibraryEntry.Directory"/> 是库根，统计整目录会把别人的作品也算进来）。
    /// </summary>
    public static long ComputeSize(ShaderLibraryEntry entry) =>
        entry.HasOwnDirectory ? ComputeSize(entry.Directory) : entry.Bytes;

    /// <summary>把字节数说成人话（UI/命令行共用）。</summary>
    public static string DescribeSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };

    /// <summary>
    /// 删除一个作品目录（只允许删库根下的**一级子目录**，且必须含 <c>project.json</c>）。
    /// </summary>
    /// <remarks>
    /// 平铺布局（库根直接放 <c>*.json</c>）不能整目录删，只提示手工处理 —— 免得把库根连同
    /// 别的作品一起删掉。
    /// </remarks>
    public static bool TryDelete(string root, ShaderLibraryEntry entry, out string? error)
    {
        error = null;

        string rootFull = Path.GetFullPath(root);
        string directoryFull;

        try
        {
            directoryFull = Path.GetFullPath(entry.Directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "路径无效：" + ex.Message;
            return false;
        }

        if (!Directory.Exists(directoryFull))
        {
            error = "目录已不存在。";
            return false;
        }

        if (IsSamePath(rootFull, directoryFull))
        {
            error = "不能删除库根目录。";
            return false;
        }

        string relative = Path.GetRelativePath(rootFull, directoryFull);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            error = "目录不在库根下，拒绝删除。";
            return false;
        }

        if (relative.Contains(Path.DirectorySeparatorChar))
        {
            error = "只支持删除库根下的一级作品目录。";
            return false;
        }

        if (!File.Exists(Path.Combine(directoryFull, "project.json")))
        {
            error = "目录里没有 project.json，看起来不是作品目录，拒绝删除。";
            return false;
        }

        try
        {
            Directory.Delete(directoryFull, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "删除失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 记录"刚播放过"（只更新已存在的 <c>meta.json</c>，不新建）。
    /// </summary>
    /// <returns>是否真的写了盘。</returns>
    public static bool MarkPlayed(string projectPath, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (directory is null)
            {
                return false;
            }

            ShaderMeta? meta = ShaderMeta.TryLoad(directory);
            if (meta is null)
            {
                // 手工拷进来的作品没有 meta.json：扫描时按默认值显示，不回写（S8-6）
                return false;
            }

            meta.MarkPlayed(now ?? DateTimeOffset.Now);
            meta.Save(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
