using MilkDrop.ShaderToy.Import;

namespace MilkDrop.ShaderToy.App;

/// <summary>
/// 数据根目录与它下面的固定文件/子目录。
/// </summary>
/// <remarks>
/// <para>
/// 解析顺序：命令行 <c>--data</c> &gt; 设置文件（**exe 旁的 <c>shadertoy.json</c>**）的
/// <c>dataDir</c> &gt; exe 旁的 <c>stdata</c>。相对路径按 **exe 所在目录**解析，整个程序目录搬走也不用改配置。
/// </para>
/// <para>与 ShaderToySnap 的 <c>output/</c> 等价，两边可以指同一个目录：</para>
/// <code>
/// &lt;数据根&gt;/
///   shadertoy.db                   ← 作品数据（工程 JSON / pass 代码 / 通道绑定）
///   media/media/a/&lt;hash&gt;.png      ← 站点 /media/... 资源（图片只从这里拿）
///   screenshots/                   ← 截图
/// </code>
/// <para>
/// 这里只做路径拼接，不负责建目录：扫描时库不存在会给出提示，写盘时才按需创建。
/// </para>
/// </remarks>
internal static class DataPaths
{
    /// <summary>没配置时用的默认目录名（exe 旁）。</summary>
    public const string DefaultFolder = "stdata";

    /// <summary>截图子目录名。</summary>
    public const string ScreenshotFolder = "screenshots";

    /// <summary>解析数据根目录（绝对路径）。</summary>
    /// <param name="overridePath">命令行 <c>--data</c>；null 表示没给。</param>
    public static string ResolveRoot(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return MakeAbsolute(overridePath);
        }

        string? configured = AppSettings.LoadDataDir();
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, DefaultFolder)
            : MakeAbsolute(configured);
    }

    /// <summary>作品数据库（<c>&lt;数据根&gt;/shadertoy.db</c>）。</summary>
    public static string DatabasePath(string dataRoot) => Path.Combine(dataRoot, ShaderDatabase.FileName);

    /// <summary>站点资源目录（<c>&lt;数据根&gt;/media</c>）：<c>/media/a/x.png</c> 落在 <c>media/media/a/x.png</c>。</summary>
    public static string MediaDirectory(string dataRoot) => Path.Combine(dataRoot, ShaderFiles.MediaFolder);

    /// <summary>截图目录（<c>&lt;数据根&gt;/screenshots</c>）。</summary>
    public static string ScreenshotDirectory(string dataRoot) => Path.Combine(dataRoot, ScreenshotFolder);

    /// <summary>
    /// 图片搜索目录：站点资源树 + 数据根本身（<c>/media/a/x.png</c> 用前者，手工摆放的相对路径用后者兜底）。
    /// </summary>
    public static IEnumerable<string> TextureSearchPaths(string dataRoot)
    {
        yield return MediaDirectory(dataRoot);
        yield return dataRoot;
    }

    /// <summary>相对路径按 exe 目录解析（配置里写 <c>data</c> 就指 exe\data）。</summary>
    private static string MakeAbsolute(string path)
    {
        try
        {
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 配置写坏了不至于起不来：退回默认目录
            return Path.Combine(AppContext.BaseDirectory, DefaultFolder);
        }
    }
}
