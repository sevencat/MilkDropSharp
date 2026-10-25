namespace MilkDrop.ShaderToy.Import;

/// <summary>
/// 数据目录里的文件命名约定（布局见 <c>shaderToyPlan.md</c> P8）。
/// </summary>
/// <remarks>
/// <para>
/// 数据根目录默认是 exe 旁的 <c>stdata</c>，也可以在 exe 旁的设置文件
/// （<c>shadertoy.json</c> 的 <c>dataDir</c>）里改。
/// </para>
/// <code>
/// &lt;数据根&gt;/
///   shadertoy.db                   ← 作品数据（工程 JSON、pass 代码、通道绑定）**唯一来源**
///   media/media/a/&lt;hash&gt;.png      ← 站点 /media/... 资源（图片只从这里拿）
///   screenshots/                   ← 截图
///   shaders/&lt;id&gt;/                 ← 早期"文件布局"（下载/导入工具仍会写，播放侧不再读）
/// </code>
/// <para>
/// 播放侧只认 <c>shadertoy.db</c> + <c>media/</c>（见 <see cref="ShaderDatabase"/>、
/// <see cref="ShaderLibrary"/>）；下面的常量留给文件布局的写侧（<see cref="ShaderToyDownloader"/>、
/// <see cref="LocalJsonImporter"/>）与它们的测试，改名时保持一致。
/// </para>
/// </remarks>
public static class ShaderFiles
{
    /// <summary>工程文件名（现行，与 ShaderToySnap 一致）。</summary>
    public const string RawProject = "raw.json";

    /// <summary>工程文件名（早期布局，仍能识别）。</summary>
    public const string LegacyProject = "project.json";

    /// <summary>本地元信息文件名（<see cref="ShaderMeta"/> 维护）。</summary>
    public const string Meta = "meta.json";

    /// <summary>作品目录下存放自带资源的子目录。</summary>
    public const string TextureFolder = "textures";

    /// <summary>数据根下的作品目录。</summary>
    public const string ShaderFolder = "shaders";

    /// <summary>数据根下的站点资源目录（<c>/media/a/x.png</c> → <c>media/media/a/x.png</c>）。</summary>
    public const string MediaFolder = "media";

    /// <summary>缩略图候选文件名（依次尝试）。</summary>
    public static IReadOnlyList<string> Thumbnails { get; } = ["thumbnail.png", "thumbnail.jpg", "preview.png"];

    /// <summary>是不是工程文件（<c>raw.json</c> / <c>project.json</c>）。</summary>
    public static bool IsProjectFile(string fileName) =>
        string.Equals(fileName, RawProject, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, LegacyProject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 作品目录里的工程文件：<c>raw.json</c> 优先，其次老布局的 <c>project.json</c>。
    /// </summary>
    /// <returns>绝对路径；都不存在时返回 null。</returns>
    public static string? FindProject(string directory)
    {
        string raw = Path.Combine(directory, RawProject);
        if (File.Exists(raw))
        {
            return raw;
        }

        string legacy = Path.Combine(directory, LegacyProject);
        return File.Exists(legacy) ? legacy : null;
    }
}
