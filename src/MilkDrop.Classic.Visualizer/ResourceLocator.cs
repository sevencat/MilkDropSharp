namespace MilkDrop.Classic.Visualizer;

/// <summary>
/// 定位原版资源目录（预设 / 纹理 / 形状 / 波形 / data）。
/// 对应 C 版 <c>GetPresetDir</c> / <c>FindValidPresetDir</c> 的角色，但为只读复用。
/// </summary>
public static class ResourceLocator
{
    public const string EnvironmentVariable = "MILKDROP_CLASSIC_ROOT";

    private static string? _projectRoot;
    private static string? _resourceRoot;

    /// <summary>仓库根目录（包含 <c>code/resources/plugins/Milkdrop2</c> 的目录）。</summary>
    public static string ProjectRoot => _projectRoot ??= FindProjectRoot()
        ?? throw new DirectoryNotFoundException(
            $"找不到 MilkDrop.Classic 仓库根目录。请设置环境变量 {EnvironmentVariable} 指向包含 code/resources 的目录。");

    /// <summary><c>code/resources/plugins/Milkdrop2</c></summary>
    public static string ResourceRoot => _resourceRoot ??= Path.Combine(ProjectRoot, "res", "MilkDrop3", "plugins", "Milkdrop2");

    public static string PresetsDir => Path.Combine(ResourceRoot, "presets");
    public static string TexturesDir => Path.Combine(ResourceRoot, "textures");
    public static string ShapesDir => Path.Combine(ResourceRoot, "shapes");
    public static string WavesDir => Path.Combine(ResourceRoot, "waves");

    /// <summary>sprite 图片目录（原版 <c>sprites\</c>）。</summary>
    public static string SpritesDir => Path.Combine(ResourceRoot, "sprites");
    public static string DataDir => Path.Combine(ResourceRoot, "data");

    /// <summary>着色器公共 include（<c>include.fx</c>）。</summary>
    public static string IncludeShaderPath => Path.Combine(DataDir, "include.fx");

    public static string? FindProjectRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && IsProjectRoot(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
            {
                if (IsProjectRoot(dir.FullName))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    private static bool IsProjectRoot(string path)
        => File.Exists(Path.Combine(path, "res", "MilkDrop3", "plugins", "Milkdrop2", "data", "include.fx"));

    /// <summary>枚举全部 <c>.milk</c> 预设（含子目录）。</summary>
    public static IEnumerable<string> EnumeratePresets()
        => Directory.Exists(PresetsDir)
            ? Directory.EnumerateFiles(PresetsDir, "*.milk", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            : [];
}
